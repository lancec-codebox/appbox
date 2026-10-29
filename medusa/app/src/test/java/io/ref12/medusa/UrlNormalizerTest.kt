package io.ref12.medusa

import org.junit.Assert.assertEquals
import org.junit.Assert.assertNotEquals
import org.junit.Test

class UrlNormalizerTest {
    private val canon = "https://www.youtube.com/watch?v=4mcienmpsnk"

    @Test fun every_youtube_spelling_maps_to_one_url() {
        for (u in listOf(
            "https://www.youtube.com/watch?v=4mcienmpsnk",
            "https://m.youtube.com/watch?v=4mcienmpsnk&feature=share&t=30s",
            "https://youtube.com/watch?feature=share&v=4mcienmpsnk",
            "https://music.youtube.com/watch?v=4mcienmpsnk&list=RDAMVM",
            "http://YouTu.be/4mcienmpsnk?si=abc&t=10",
            "https://www.youtube.com/shorts/4mcienmpsnk?feature=share",
            "https://www.youtube.com/embed/4mcienmpsnk",
            "https://www.youtube.com/live/4mcienmpsnk?si=x",
            "  https://m.youtube.com/watch?v=4mcienmpsnk#t=5  ",
        )) assertEquals(u, canon, UrlNormalizer.canonical(u))
    }

    @Test fun different_videos_stay_different_and_other_sites_are_left_alone() {
        assertNotEquals(UrlNormalizer.canonical("https://youtu.be/4mcienmpsnk"), UrlNormalizer.canonical("https://youtu.be/AAAAAAAAAAA"))
        assertEquals("https://www.bilibili.com/video/BV1xx?p=2", UrlNormalizer.canonical("https://WWW.bilibili.com/video/BV1xx?p=2"))
        assertEquals("https://www.youtube.com/playlist?list=PL1", UrlNormalizer.canonical("https://www.youtube.com/playlist?list=PL1"))
        assertEquals("not a url", UrlNormalizer.canonical(" not a url "))
        assertEquals("https://www.youtube.com/watch?v=short", UrlNormalizer.canonical("https://www.youtube.com/watch?v=short")) // not an 11 char id
    }
}
