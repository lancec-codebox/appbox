package io.ref12.medusa

import org.json.JSONArray
import org.json.JSONObject
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Assert.fail
import org.junit.Rule
import org.junit.Test
import org.junit.rules.TemporaryFolder

class AddedTracksTest {
    @get:Rule val tmp = TemporaryFolder()

    private fun body(lines: String = """[{"start":1,"end":2,"text":"Hello"},{"start":3,"end":4.5,"text":"World"}]""", extra: String = """"language":"en"""") =
        JSONObject("{" + extra + ""","lines":""" + lines + "}")

    private fun rejects(j: JSONObject, part: String) {
        try { TrackRules.parse(j); fail("accepted " + j) } catch (e: TrackRejected) { assertTrue(e.message, e.message!!.contains(part)) }
    }

    private fun meta(id: String = "m1", url: String = "https://youtu.be/abc") = MediaMeta(id, url, "Title: A/B?", 60, "youtube", "abc", null, null, 1L)

    // --- API validation
    @Test fun valid_body_parses() {
        val t = TrackRules.parse(body(extra = """"language":"pt-BR","label":"Portuguese (AI)","source":"added","origin":"linguaweave""""))
        assertEquals("pt-BR", t.language); assertEquals("Portuguese (AI)", t.label); assertEquals("linguaweave", t.origin)
        assertEquals(2, t.lines.size)
    }
    @Test fun rejects_bad_lines() {
        rejects(body("""[{"start":-1,"end":2,"text":"x"}]"""), "negative")
        rejects(body("""[{"start":2,"end":2,"text":"x"}]"""), "greater")
        rejects(body("""[{"start":3,"end":4,"text":"x"},{"start":1,"end":2,"text":"y"}]"""), "sorted")
        rejects(body("""[{"start":1,"end":2,"text":"<b>x</b>"}]"""), "plain text")
        rejects(body("""[{"start":1,"end":2,"text":"  "}]"""), "empty")
        rejects(body("""[{"start":1,"end":2}]"""), "text")
        rejects(body("[]"), "empty")
    }
    @Test fun rejects_bad_fields() {
        rejects(body(extra = """"language":"""""), "language")
        rejects(body(extra = """"language":"en","source":"captions""""), "source")
        rejects(body(extra = """"language":"en","origin":"a b/c""""), "origin")
        rejects(body(extra = """"language":"en","label":"<i>x</i>""""), "label")
        rejects(JSONObject("""{"language":"en"}"""), "lines")
    }
    @Test fun same_language_and_origin_share_an_id() {
        val a = TrackRules.parse(body(extra = """"language":"en","origin":"x""""))
        val b = TrackRules.parse(body(extra = """"language":"EN","origin":"x""""))
        val c = TrackRules.parse(body(extra = """"language":"en","origin":"y""""))
        assertEquals(a.trackId, b.trackId); assertTrue(a.trackId != c.trackId)
    }

    // --- store: replace by language+origin, coexistence, removal
    @Test fun replace_by_language_and_origin_and_remove() {
        val s = MediaStore(tmp.root)
        s.save(meta())
        s.save(meta().copy(tracks = listOf(TrackInfo("en", "captions"))))
        val a1 = TrackRules.parse(body(extra = """"language":"en","label":"English (AI)","origin":"lw""""))
        val a2 = TrackRules.parse(body("""[{"start":5,"end":6,"text":"new"}]""", """"language":"en","label":"English v2","origin":"lw""""))
        val other = TrackRules.parse(body(extra = """"language":"en","origin":"hexad""""))
        val id1 = s.addTrack("m1", a1)!!.trackId!!
        val id2 = s.addTrack("m1", a2)!!.trackId!!
        assertEquals(id1, id2)
        s.addTrack("m1", other)
        val tracks = s.get("m1")!!.tracks
        assertEquals(3, tracks.size) // captured + two origins
        assertEquals("English v2", tracks.first { it.trackId == id1 }.name)
        assertEquals("new", s.loadAdded("m1", id1)!!.lines.single().text)
        assertNull(s.addTrack("nope", a1))
        assertTrue(s.removeTrack("m1", id1)); assertFalse(s.removeTrack("m1", id1))
        assertNull(s.loadAdded("m1", id1))
        assertEquals(2, s.get("m1")!!.tracks.size)
        val j = JSONObject(MediaJson.item(s.get("m1")!!, emptyList()).toString())
        val t = j.getJSONArray("tracks").getJSONObject(1)
        assertEquals("added", t.getString("source")); assertEquals("hexad", t.getString("origin")); assertTrue(t.has("trackId"))
    }

    // --- file parsing
    @Test fun srt_parses() {
        val l = SubtitleFiles.parseCues("\uFEFF1\r\n00:00:01,500 --> 00:00:03,000\r\n<i>Hello</i>\r\nthere\r\n\r\n2\r\n00:01:00,000 --> 00:01:02,250\r\nBye\r\n")
        assertEquals(listOf(Line(1.5, 3.0, "Hello\nthere"), Line(60.0, 62.25, "Bye")), l)
    }
    @Test fun vtt_parses_header_notes_settings_and_short_timestamps() {
        val l = SubtitleFiles.parseCues("WEBVTT\n\nNOTE a comment\n\nSTYLE\n::cue {color:red}\n\nintro\n00:01.000 --> 00:02.500 align:start\nHi &amp; bye\n\n01:00:00.000 --> 01:00:01.000\nLate\n")
        assertEquals(listOf(Line(1.0, 2.5, "Hi & bye"), Line(3600.0, 3601.0, "Late")), l)
    }
    @Test fun empty_or_foreign_file_is_rejected() {
        try { SubtitleFiles.parse("a.srt", "nothing here"); fail() } catch (e: TrackRejected) {}
        try { SubtitleFiles.parse("a.json", "not json"); fail() } catch (e: TrackRejected) {}
        try { SubtitleFiles.parse("a.srt", "x".repeat(TrackRules.MAX_BYTES + 1)); fail() } catch (e: TrackRejected) {}
    }
    @Test fun json_file_carries_media_and_language() {
        val f = SubtitleFiles.parse("x.medusa-track.json", """{"mediaId":"m1","url":"https://youtu.be/abc","language":"fr","label":"French","origin":"lw","lines":[{"start":0,"end":1,"text":"a"}]}""")
        assertEquals("m1", f.mediaId); assertEquals("fr", f.language); assertEquals("French", f.label); assertEquals("lw", f.origin)
    }
    @Test fun language_from_file_name() {
        assertEquals("en", SubtitleFiles.languageFromName("Movie [x].en.srt"))
        assertEquals("pt-BR", SubtitleFiles.languageFromName("movie.pt-BR.vtt"))
        assertNull(SubtitleFiles.languageFromName("movie.srt"))
    }

    // --- import matching
    @Test fun matches_by_media_id_then_url() {
        val items = listOf(meta("a", "https://youtu.be/abcdefghijk"), meta("b", "https://www.youtube.com/watch?v=zzzzzzzzzzz"))
        assertEquals("b", ImportMatch.find(items, "b", null)!!.mediaId)
        assertEquals("b", ImportMatch.find(items, "nope", "https://youtube.com/watch?v=zzzzzzzzzzz&t=5")!!.mediaId)
        assertEquals("a", ImportMatch.find(items, null, "https://www.youtube.com/watch?v=abcdefghijk")!!.mediaId)
        assertNull(ImportMatch.find(items, null, null))
        assertNull(ImportMatch.find(items, "x", "https://example.com/v"))
    }

    // --- export round trip
    private val lines = listOf(Line(1.5, 3.0, "Hello\nthere"), Line(3725.125, 3726.0, "Late"))
    @Test fun srt_round_trips() {
        assertEquals(lines, SubtitleFiles.parse("a.srt", TrackExport.srt(lines)).lines)
    }
    @Test fun vtt_round_trips() {
        assertEquals(lines, SubtitleFiles.parse("a.vtt", TrackExport.vtt(lines)).lines)
    }
    @Test fun json_round_trips_and_imports_back_to_the_same_media() {
        val m = meta()
        val t = TrackInfo("fr", "added", "French (AI)", "t-1", "lw")
        val f = SubtitleFiles.parse("x.medusa-track.json", TrackExport.content(ExportFormat.JSON, m, t, lines))
        assertEquals(lines, f.lines); assertEquals("fr", f.language); assertEquals("French (AI)", f.label); assertEquals("lw", f.origin)
        assertEquals("m1", ImportMatch.find(listOf(m), f.mediaId, f.url)!!.mediaId)
    }
    @Test fun file_names_are_sanitised() {
        assertEquals("Title A B [en].srt", TrackExport.fileName("Title: A/B?", "en", ExportFormat.SRT))
        assertEquals("x [pt-BR].medusa-track.json", TrackExport.fileName("x", "pt-BR", ExportFormat.JSON))
        assertEquals("subtitles [und].vtt", TrackExport.fileName("...", "", ExportFormat.VTT))
    }
}
