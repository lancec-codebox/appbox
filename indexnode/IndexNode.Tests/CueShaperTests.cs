namespace IndexNode.Tests;

public class CueShaperTests
{
    private const string Long = "他走进了那间昏暗的屋子，桌上放着一封没有署名的信，他犹豫了很久才伸手去拿。信里只有一句话：今晚子时，城西老井边见！他把信折好放进怀里，转身走出门去，夜风吹得灯火摇晃不定。";

    private static ShapeCue Seg(string text, double start, double per)
    {
        var t = Enumerable.Range(0, text.Length).Select(i => start + i * per).ToArray();
        return new ShapeCue(start, start + text.Length * per, text, t);
    }

    [Fact]
    public void A_26s_85_char_segment_with_char_times_splits_into_short_cues()
    {
        var text = Long; while (text.Length < 85) text += "他"; text = text[..85];
        var cues = CueShaper.Shape([Seg(text, 10, 26.0 / 85)]);
        Assert.True(cues.Count >= 5);
        Assert.All(cues, c => { Assert.True(c.End - c.Start <= 6.0001); Assert.All(c.Text.Split('\n'), l => Assert.True(l.Length <= 18)); Assert.True(c.Text.Split('\n').Length <= 2); });
        var st = CueShaper.Stats(cues);
        Assert.Equal(0, st.Over6s); Assert.Equal(0, st.OverChars); Assert.Equal(0, st.Overlaps);
        Assert.Contains(cues, c => c.Text.EndsWith("。") || c.Text.EndsWith("！"));
    }

    [Fact]
    public void Without_timings_time_is_shared_in_proportion()
    {
        var cues = CueShaper.Shape([new ShapeCue(0, 20, "第一句话在这里。第二句话紧随其后！第三句话也来了。第四句话结束了。")]);
        Assert.Equal(4, cues.Count);
        Assert.True(cues[1].Start > 4 && cues[1].Start < 6);
        Assert.Equal(0, CueShaper.Stats(cues).Overlaps);
    }

    [Fact]
    public void Glossary_names_are_never_cut()
    {
        var text = "一二三四五六七八九十甲乙丙丁戊己庚辛壬癸子丑寅卯辰巳";
        var cues = CueShaper.Shape([new ShapeCue(0, 5, text)], ["十甲乙丙丁戊"]);
        Assert.All(cues, c => { var j = c.Text.Replace("\n", ""); Assert.True(!j.Contains("十") || !j.Contains("甲") || j.Contains("十甲乙丙丁戊") || !j.Contains("戊") ); });
        Assert.Contains(cues, c => c.Text.Replace("\n", "").Contains("十甲乙丙丁戊"));
    }

    [Fact]
    public void Latin_text_wraps_at_42_over_two_lines()
    {
        var cues = CueShaper.Shape([new ShapeCue(0, 5, "This is a fairly long English subtitle line that needs wrapping on two lines")]);
        Assert.Single(cues);
        var lines = cues[0].Text.Split('\n'); Assert.Equal(2, lines.Length); Assert.All(lines, l => Assert.True(l.Length <= 42));
    }

    [Fact]
    public void Rolling_auto_captions_are_deduped()
    {
        var vtt = "WEBVTT\n\n00:00:01.000 --> 00:00:03.000\nhello there my friend\n\n00:00:03.000 --> 00:00:03.010\nhello there my friend\n\n00:00:03.010 --> 00:00:05.000\nhello there my friend\nhow are you doing today\n\n00:00:05.000 --> 00:00:05.010\nhow are you doing today\n\n00:00:05.010 --> 00:00:07.000\nhow are you doing today\nI am fine\n";
        var (shaped, stats) = DownloadStage.ShapeSubs(vtt, true);
        var cues = CueShaper.ParseVtt(shaped);
        Assert.Equal(["hello there my friend", "how are you doing today", "I am fine"], cues.Select(c => c.Text));
        Assert.Equal(3, stats.Cues); Assert.Equal(0, stats.Overlaps);
    }

    [Fact]
    public void Nothing_overlaps_and_short_cues_are_held_without_running_into_the_next()
    {
        var cues = CueShaper.Shape([new ShapeCue(0, 0.2, "好。"), new ShapeCue(0.4, 0.6, "嗯。"), new ShapeCue(3, 3.1, "走吧。"), new ShapeCue(2.9, 5, "我们走吧，天快黑了。")]);
        for (int i = 0; i + 1 < cues.Count; i++) Assert.True(cues[i].End <= cues[i + 1].Start + 1e-6);
        Assert.Equal(0, CueShaper.Stats(cues).Overlaps);
        Assert.True(cues[0].End <= cues[1].Start);
        Assert.All(cues, c => Assert.True(c.End > c.Start));
    }

    [Fact]
    public void Counts_land_in_every_sidecar()
    {
        var segs = new[] { new AsrSegment(0, 26, Long, "zh", []) };
        var j = System.Text.Json.JsonDocument.Parse(Transcribe.ToJson(segs, "zh", false, 26, 1)).RootElement.GetProperty("shape");
        Assert.True(j.GetProperty("cues").GetInt32() > 3);
        Assert.Equal(0, j.GetProperty("over6s").GetInt32()); Assert.Equal(0, j.GetProperty("overChars").GetInt32()); Assert.Equal(0, j.GetProperty("overlaps").GetInt32());
        var cue = new Cue { Start = 0, End = 20, Text = Long, TextHans = Long };
        var o = System.Text.Json.JsonDocument.Parse(OcrCues.ToJson([cue], [], 0, 10, 100)).RootElement.GetProperty("shape");
        Assert.Equal(0, o.GetProperty("zh").GetProperty("over6s").GetInt32());
        Assert.Equal(0, o.GetProperty("zhHans").GetProperty("overChars").GetInt32());
        Assert.Equal(0, o.GetProperty("en").GetProperty("cues").GetInt32());
    }

    // ---- reading time (owner, 2026-11-14): a cue stays up until it can be read, into the gap before the next cue ----

    [Fact]
    public void QuickLineStaysForItsReadingTime()
    {
        var c = CueShaper.Shape([new ShapeCue(0, 0.6, "你好吗我很好谢谢你啊"), new ShapeCue(5, 6, "再见")]);
        Assert.Equal(10 / 9.0, c[0].End, 2);               // 10 characters at 9/s, more than speech end + 0.5 s hold
    }

    [Fact]
    public void ReadingTimeStopsShortOfTheNextCue()
    {
        var c = CueShaper.Shape([new ShapeCue(0, 1.0, "他把信折好放进怀里转身走出门去夜风吹"), new ShapeCue(1.5, 2.5, "好的")]);
        Assert.Equal(1.4, c[0].End, 3);                     // wants 2 s, ends 0.1 s before the next cue
        Assert.True(c[0].End <= c[1].Start);
    }

    [Fact]
    public void EnglishReadsAtItsOwnSpeed()
    {
        var c = CueShaper.Shape([new ShapeCue(0, 1.0, "I will be waiting by the old well tonight")]);
        Assert.Equal(41 / 17.0, c[0].End, 2);
    }

    [Fact]
    public void FastExchangeKeepsTheSpeechEnd()
    {
        var c = CueShaper.Shape([new ShapeCue(0, 1.0, "你到底想怎么样我受够了"), new ShapeCue(1.0, 2.0, "你听我解释")]);
        Assert.Equal(1.0, c[0].End, 3);                     // no time before the next cue: it ends where the speech ends
        Assert.True(c[0].End - c[0].Start < CueShaper.ReadingSeconds(c[0].Text));
        Assert.Equal(1, CueShaper.Stats(c).ShortRead);
        Assert.Equal(0, CueShaper.Stats(c).Overlaps);
    }

    [Fact]
    public void ReadingTimeNeverPassesSixSeconds()
    {
        var text = "他犹豫了很久才伸手去拿桌上的信";                  // 15 characters: 1.7 s to read, spoken over 5.8 s
        var c = CueShaper.Shape([new ShapeCue(0, 5.8, text)]);
        Assert.True(c.All(x => x.End - x.Start <= CueShaper.MaxSeconds + 1e-6));
    }
}
