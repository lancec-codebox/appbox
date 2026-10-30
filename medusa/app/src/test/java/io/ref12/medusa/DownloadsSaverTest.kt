package io.ref12.medusa

import org.junit.Assert.assertEquals
import org.junit.Test

class DownloadsSaverTest {
    @Test fun a_free_name_is_kept() = assertEquals("T [en].srt", DownloadsSaver.uniqueName("T [en]", "srt") { false })
    @Test fun a_taken_name_gets_a_counter_instead_of_being_overwritten() {
        val taken = setOf("T.srt", "T (2).srt")
        assertEquals("T (3).srt", DownloadsSaver.uniqueName("T", "srt") { it in taken })
    }
    @Test fun the_double_extension_of_json_tracks_stays_whole() {
        val (b, e) = DownloadsSaver.split("Title [zh].medusa-track.json", ExportFormat.JSON)
        assertEquals("Title [zh]", b); assertEquals("medusa-track.json", e)
        assertEquals("Title [zh] (2).medusa-track.json", DownloadsSaver.uniqueName(b, e) { it == "Title [zh].medusa-track.json" })
    }
    @Test fun mime_types_match_the_formats() {
        assertEquals("application/x-subrip", ExportFormat.SRT.mime); assertEquals("text/vtt", ExportFormat.VTT.mime); assertEquals("application/json", ExportFormat.JSON.mime)
    }
}
