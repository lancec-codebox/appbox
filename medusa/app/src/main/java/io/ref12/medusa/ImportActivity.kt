package io.ref12.medusa

import android.app.Activity
import android.app.AlertDialog
import android.content.Intent
import android.net.Uri
import android.os.Bundle
import android.provider.OpenableColumns
import android.view.View
import android.widget.AdapterView
import android.widget.ArrayAdapter
import android.widget.Button
import android.widget.EditText
import android.widget.Spinner
import android.widget.LinearLayout
import android.widget.TextView
import android.widget.Toast

/**
 * Adds a subtitle file (.srt, .vtt, *.medusa-track.json) to a library item. Reached by opening or sharing a file
 * with Medusa, or from the item screen (which passes EXTRA_MEDIA and starts with the system file picker).
 */
class ImportActivity : Activity() {
    private var forcedMedia: String? = null

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        forcedMedia = intent.getStringExtra(EXTRA_MEDIA)
        val uri = fileUri(intent)
        if (uri != null) { load(uri); return }
        if (forcedMedia == null) { Toast.makeText(this, "No subtitle file", Toast.LENGTH_LONG).show(); finish(); return }
        startActivityForResult(Intent(Intent.ACTION_OPEN_DOCUMENT).addCategory(Intent.CATEGORY_OPENABLE).setType("*/*"), PICK)
    }

    @Deprecated("Deprecated in Java")
    override fun onActivityResult(requestCode: Int, resultCode: Int, data: Intent?) {
        super.onActivityResult(requestCode, resultCode, data)
        val u = data?.data
        if (requestCode == PICK && resultCode == RESULT_OK && u != null) load(u) else finish()
    }

    @Suppress("DEPRECATION")
    private fun fileUri(i: Intent): Uri? = when (i.action) {
        Intent.ACTION_VIEW -> i.data
        Intent.ACTION_SEND -> i.getParcelableExtra(Intent.EXTRA_STREAM)
        else -> null
    }

    private fun nameOf(u: Uri): String {
        if (u.scheme == "content") try {
            contentResolver.query(u, arrayOf(OpenableColumns.DISPLAY_NAME), null, null, null)?.use { if (it.moveToFirst()) return it.getString(0) }
        } catch (e: Exception) { /* fall through */ }
        return u.lastPathSegment ?: "subtitles"
    }

    private fun load(u: Uri) {
        val name = nameOf(u)
        Thread {
            val r = try {
                val bytes = contentResolver.openInputStream(u)!!.use { it.readNBytes(TrackRules.MAX_BYTES + 1) }
                Result.success(SubtitleFiles.parse(name, String(bytes, Charsets.UTF_8)))
            } catch (e: TrackRejected) { Result.failure(e) } catch (e: Exception) { Result.failure(TrackRejected("Could not read this file: " + e.message)) }
            runOnUiThread { r.fold({ choose(name, it) }, { fail(it.message ?: "Could not read this file") }) }
        }.start()
    }

    private fun fail(msg: String) {
        AlertDialog.Builder(this).setTitle("Import subtitles").setMessage(msg).setPositiveButton("OK") { _, _ -> finish() }.setOnCancelListener { finish() }.show()
    }

    private fun choose(fileName: String, f: ImportedFile) {
        val all = app.media.list()
        if (all.isEmpty()) { fail("Your library is empty. Download the video first, then import its subtitles."); return }
        val matched = forcedMedia?.let { id -> all.firstOrNull { it.mediaId == id } } ?: ImportMatch.find(all, f.mediaId, f.url)
        // matched item first, then most recently played, then newest downloads
        val items = all.sortedWith(compareByDescending<MediaMeta> { it.mediaId == matched?.mediaId }
            .thenByDescending { app.settings.lastPlayed(it.mediaId) }.thenByDescending { it.createdAt })
        var pick = 0
        val detected = LanguageTools.detect(this, fileName, f.language, f.lines.map { it.text })
        val common = LanguageTools.order(LanguageTools.COMMON, null, { it })
        val langs = if (common.any { it.equals(detected, true) }) common else listOf(detected) + common
        val langLabels = langs.map { LanguageTools.displayName(it) + " (" + it + ")" } + "Other..."
        val body = LinearLayout(this).apply { orientation = LinearLayout.VERTICAL; setPadding(40, 16, 40, 0) }
        fun header(t: String) = TextView(this).apply { text = t; textSize = 14f; setPadding(0, 16, 0, 4); setTypeface(typeface, android.graphics.Typeface.BOLD) }
        fun spinner(labels: List<String>, sel: Int, desc: String, onSel: (Int) -> Unit) = Spinner(this).apply {
            adapter = ArrayAdapter(this@ImportActivity, android.R.layout.simple_spinner_dropdown_item, labels)
            setSelection(sel); contentDescription = desc; tag = desc
            onItemSelectedListener = object : AdapterView.OnItemSelectedListener {
                override fun onItemSelected(p: AdapterView<*>?, v: View?, pos: Int, id: Long) = onSel(pos)
                override fun onNothingSelected(p: AdapterView<*>?) {}
            }
        }
        val importBtn = Button(this).apply { text = "Import"; tag = "import"; contentDescription = "Import" }
        body.addView(importBtn)
        body.addView(header("Video"))
        if (matched == null) body.addView(TextView(this).apply { text = "Medusa could not tell which video this belongs to. Choose it below (recently played first)." })
        if (forcedMedia != null || matched != null && items.size == 1) body.addView(TextView(this).apply { text = items[0].title; textSize = 16f })
        else body.addView(spinner(items.map { it.title }, 0, "Video") { pick = it })
        body.addView(header("Language"))
        var langPick = langs.indexOfFirst { it.equals(detected, true) }.coerceAtLeast(0)
        val other = EditText(this).apply { hint = "Language tag, e.g. pt-BR"; contentDescription = "Other language"; tag = "language"; setSingleLine(); visibility = View.GONE }
        body.addView(spinner(langLabels, langPick, "Language") { langPick = it; other.visibility = if (it == langs.size) View.VISIBLE else View.GONE })
        body.addView(other)
        body.addView(header("Label (shown in the subtitle menu)"))
        val label = EditText(this).apply { setText(f.label ?: ""); hint = "English (AI)"; contentDescription = "Label"; tag = "label"; setSingleLine() }
        body.addView(label)
        body.addView(header("File"))
        body.addView(TextView(this).apply { text = fileName + "\n" + f.lines.size + " lines" + (f.lines.lastOrNull()?.let { ", " + (it.end / 60000) + " min" } ?: "") })
        body.addView(header("Preview"))
        body.addView(TextView(this).apply { text = f.lines.take(5).joinToString("\n") { it.text }; textSize = 13f })
        AlertDialog.Builder(this).setTitle("Import subtitles").setView(android.widget.ScrollView(this).apply { addView(body) })
            .setNegativeButton("Cancel") { _, _ -> finish() }.setOnCancelListener { finish() }
            .create().also { d ->
                importBtn.setOnClickListener {
                    val m = items[pick]
                    val language = (if (langPick < langs.size) langs[langPick] else other.text.toString()).trim()
                    val json = org.json.JSONObject().put("language", language).put("lines", org.json.JSONArray().also { a ->
                        for (l in f.lines) a.put(org.json.JSONObject().put("start", l.start).put("end", l.end).put("text", l.text))
                    }).put("origin", f.origin ?: "import")
                    label.text.toString().trim().takeIf { it.isNotEmpty() }?.let { json.put("label", it) }
                    try {
                        val t = TrackRules.parse(json)
                        app.media.addTrack(m.mediaId, t) ?: throw TrackRejected("That video is no longer in the library")
                        Toast.makeText(this, "Added " + (t.label ?: t.language) + " to " + m.title, Toast.LENGTH_LONG).show()
                        d.dismiss()
                        if (forcedMedia == null) startActivity(Intent(this, MainActivity::class.java).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK))
                        finish()
                    } catch (e: TrackRejected) { Toast.makeText(this, e.message, Toast.LENGTH_LONG).show() }
                }
            }.show()
    }

    companion object {
        const val EXTRA_MEDIA = "media"
        private const val PICK = 7
    }
}
