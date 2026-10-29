package io.ref12.mediahelper

import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test

class SubtitleConverterTest {
    @Test fun json3_basic_and_skips_newline_only_events() {
        val json = """{"events":[
          {"tStartMs":0,"dDurationMs":1000},
          {"tStartMs":1200,"dDurationMs":2000,"segs":[{"utf8":"你好，"},{"utf8":"我是小明"}]},
          {"tStartMs":3200,"dDurationMs":10,"aAppend":1,"segs":[{"utf8":"\n"}]},
          {"tStartMs":4000,"dDurationMs":1500,"segs":[{"utf8":"第二行"}]}
        ]}"""
        val l = SubtitleConverter.parseJson3(json)
        assertEquals(listOf(Line(1.2, 3.2, "你好，我是小明"), Line(4.0, 5.5, "第二行")), l)
    }

    @Test fun json3_multiline_chinese_joins_without_space_latin_with_space() {
        val json = """{"events":[
          {"tStartMs":0,"dDurationMs":1000,"segs":[{"utf8":"你好\n世界"}]},
          {"tStartMs":2000,"dDurationMs":1000,"segs":[{"utf8":"hello\nworld"}]}]}"""
        val l = SubtitleConverter.parseJson3(json)
        assertEquals("你好世界", l[0].text)
        assertEquals("hello world", l[1].text)
    }

    @Test fun json3_merges_repeated_adjacent_events() {
        val json = """{"events":[
          {"tStartMs":0,"dDurationMs":1000,"segs":[{"utf8":"重复"}]},
          {"tStartMs":1000,"dDurationMs":1000,"segs":[{"utf8":"重复"}]}]}"""
        assertEquals(listOf(Line(0.0, 2.0, "重复")), SubtitleConverter.parseJson3(json))
    }

    @Test fun vtt_plain_cues_with_tags_entities_and_header() {
        val vtt = "WEBVTT\nKind: captions\nLanguage: zh-Hans\n\n" +
            "00:00:01.000 --> 00:00:03.500 align:start position:0%\n<c>你好</c>&amp;世界\n\n" +
            "NOTE ignore me\n\n" +
            "1\n00:01:05.000 --> 00:01:06.000\n第二句\n"
        val l = SubtitleConverter.parseVtt(vtt)
        assertEquals(listOf(Line(1.0, 3.5, "你好&世界"), Line(65.0, 66.0, "第二句")), l)
    }

    @Test fun vtt_short_timestamps_and_crlf() {
        val vtt = "WEBVTT\r\n\r\n01:02.500 --> 01:04.000\r\n行一\r\n"
        assertEquals(listOf(Line(62.5, 64.0, "行一")), SubtitleConverter.parseVtt(vtt))
    }

    @Test fun vtt_rolling_autocaptions_are_deduplicated() {
        // YouTube style: each cue repeats the previous line and adds a new one, plus tiny filler cues.
        val vtt = """WEBVTT

00:00:00.000 --> 00:00:02.000 align:start position:0%
<00:00:00.500><c>大家好</c>

00:00:02.000 --> 00:00:02.010 align:start position:0%
大家好

00:00:02.010 --> 00:00:04.000 align:start position:0%
大家好
今天<00:00:03.000><c>我们来看</c>

00:00:04.000 --> 00:00:04.010 align:start position:0%
今天我们来看

00:00:04.010 --> 00:00:06.000 align:start position:0%
今天我们来看
一个故事
"""
        val l = SubtitleConverter.parseVtt(vtt)
        assertEquals(listOf("大家好", "今天我们来看", "一个故事"), l.map { it.text })
        assertEquals(0.0, l[0].start, 1e-6)
        assertEquals(2.01, l[1].start, 1e-6)
        assertEquals(4.01, l[2].start, 1e-6)
        // non-overlapping, in order
        for (i in 0 until l.size - 1) assertTrue(l[i].end <= l[i + 1].start + 1e-9)
    }

    @Test fun empty_input_gives_no_lines() {
        assertTrue(SubtitleConverter.parseVtt("WEBVTT\n\n").isEmpty())
        assertTrue(SubtitleConverter.parseJson3("""{"events":[]}""").isEmpty())
    }
}
