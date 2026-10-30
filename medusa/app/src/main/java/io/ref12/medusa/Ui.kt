package io.ref12.medusa

import android.content.Context
import android.graphics.Typeface
import android.graphics.drawable.GradientDrawable
import android.text.TextUtils
import android.view.Gravity
import android.view.View
import android.view.ViewGroup
import android.widget.Button
import android.widget.ImageButton
import android.widget.ImageView
import android.widget.LinearLayout
import android.widget.TextView
import java.text.DateFormat
import java.util.Date

/** Small view factories so every screen uses the same dense, themed look (colours come from res/values[-night]/colors.xml). */
object Ui {
    fun dp(c: Context, x: Int) = (x * c.resources.displayMetrics.density + 0.5f).toInt()
    fun color(c: Context, id: Int) = c.getColor(id)

    fun rounded(c: Context, fill: Int, radiusDp: Int = 8, stroke: Int? = null) = GradientDrawable().apply {
        setColor(fill); cornerRadius = dp(c, radiusDp).toFloat(); if (stroke != null) setStroke(dp(c, 1), stroke)
    }

    fun text(c: Context, t: CharSequence, sp: Float = 14f, colorId: Int = R.color.on_surface, bold: Boolean = false) = TextView(c).apply {
        text = t; textSize = sp; setTextColor(color(c, colorId)); if (bold) setTypeface(typeface, Typeface.BOLD)
    }

    fun muted(c: Context, t: CharSequence, sp: Float = 13f) = text(c, t, sp, R.color.muted)

    /** A section header: small, bold, teal, upper case. */
    fun header(c: Context, t: String) = text(c, t.uppercase(), 12f, R.color.accent, true).apply {
        letterSpacing = 0.06f; setPadding(dp(c, 16), dp(c, 18), dp(c, 16), dp(c, 6))
    }

    fun divider(c: Context) = View(c).apply {
        setBackgroundColor(color(c, R.color.divider)); layoutParams = LinearLayout.LayoutParams(-1, 1)
    }

    /** A block with the surface colour, hairline-bordered, for a group of rows. */
    fun card(c: Context) = LinearLayout(c).apply {
        orientation = LinearLayout.VERTICAL
        background = rounded(c, color(c, R.color.surface), 10, color(c, R.color.divider))
        setPadding(dp(c, 12), dp(c, 8), dp(c, 12), dp(c, 8))
    }

    fun icon(c: Context, res: Int, desc: String, tintId: Int = R.color.on_surface, onClick: () -> Unit) = ImageButton(c).apply {
        setImageResource(res); setColorFilter(color(c, tintId)); contentDescription = desc; tag = desc
        setBackgroundColor(0); scaleType = ImageView.ScaleType.CENTER
        layoutParams = LinearLayout.LayoutParams(dp(c, 48), dp(c, 48))
        setOnClickListener { onClick() }
    }

    /** primary = filled teal; otherwise an outlined button. */
    fun button(c: Context, t: String, primary: Boolean = false, danger: Boolean = false, onClick: (View) -> Unit) = Button(c).apply {
        text = t; isAllCaps = false; textSize = 14f; minHeight = 0; minimumHeight = dp(c, 40); minWidth = 0; minimumWidth = 0
        setPadding(dp(c, 14), 0, dp(c, 14), 0); contentDescription = t; tag = t
        stateListAnimator = null
        if (primary) { setTextColor(color(c, R.color.on_accent)); background = rounded(c, color(c, R.color.accent)) }
        else {
            val col = color(c, if (danger) R.color.danger else R.color.accent)
            setTextColor(col); background = rounded(c, 0, 8, col)
        }
        setOnClickListener { onClick(this) }
    }

    /** A compact text action in a row of actions; [on] marks the current choice. */
    fun chipButton(c: Context, t: String, on: Boolean = false, tagText: String = t, onClick: () -> Unit) = TextView(c).apply {
        text = t; textSize = 13f; gravity = Gravity.CENTER; contentDescription = tagText; tag = tagText
        setPadding(dp(c, 10), dp(c, 6), dp(c, 10), dp(c, 6))
        setTextColor(color(c, if (on) R.color.on_accent else R.color.on_surface))
        background = rounded(c, color(c, if (on) R.color.accent else R.color.chip), 6)
        setOnClickListener { onClick() }
    }

    /** A small read-only badge (track language, "AI", "video"). */
    fun badge(c: Context, t: String, accent: Boolean = false) = TextView(c).apply {
        text = t; textSize = 11f; maxLines = 1
        setTextColor(color(c, if (accent) R.color.accent else R.color.muted))
        background = rounded(c, color(c, if (accent) R.color.selected else R.color.chip), 4)
        setPadding(dp(c, 6), dp(c, 1), dp(c, 6), dp(c, 1))
    }

    fun oneLine(v: TextView): TextView { v.maxLines = 1; v.ellipsize = TextUtils.TruncateAt.END; return v }

    fun lp(w: Int = -1, h: Int = -2, weight: Float = 0f, l: Int = 0, t: Int = 0, r: Int = 0, b: Int = 0, c: Context? = null): LinearLayout.LayoutParams {
        val p = LinearLayout.LayoutParams(w, h, weight)
        if (c != null) p.setMargins(dp(c, l), dp(c, t), dp(c, r), dp(c, b))
        return p
    }

    fun spacer(c: Context, h: Int) = View(c).apply { layoutParams = LinearLayout.LayoutParams(-1, dp(c, h)) }

    // ---- formatting
    fun duration(sec: Int?): String? = sec?.let { if (it >= 3600) "%d:%02d:%02d".format(it / 3600, it / 60 % 60, it % 60) else "%d:%02d".format(it / 60, it % 60) }

    fun size(bytes: Long): String = when {
        bytes >= 1024L * 1024 * 1024 -> "%.1f GB".format(bytes / 1073741824.0)
        bytes >= 1024L * 1024 -> "%d MB".format(bytes / 1048576)
        bytes >= 1024 -> "%d KB".format(bytes / 1024)
        else -> "$bytes B"
    }

    fun date(ms: Long): String = DateFormat.getDateInstance(DateFormat.MEDIUM).format(Date(ms))
}
