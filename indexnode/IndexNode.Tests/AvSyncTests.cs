namespace IndexNode.Tests;

public class AvSyncTests
{
    // a drama of 600 s: a cue every 6 s, each 6 distinct characters; the transcript has the same characters, starting `shift` after the cue
    private static (List<(double, string)> Cues, List<AsrSegment> Segs) Synthetic(Func<double, double> shift)
    {
        var cues = new List<(double, string)>(); var segs = new List<AsrSegment>();
        var alphabet = "天地玄黄宇宙洪荒日月盈昃辰宿列张寒来暑往秋收冬藏闰余成岁律吕调阳云腾致雨露结为霜金生丽水玉出昆冈剑号巨阙珠称夜光果珍李柰菜重芥姜海咸河淡鳞潜羽翔龙师火帝鸟官人皇";
        for (int i = 0; i < 100; i++)
        {
            double start = i * 6.0 + 1;
            var text = new string(Enumerable.Range(0, 6).Select(k => alphabet[(i * 7 + k * 3) % alphabet.Length]).ToArray());
            cues.Add((start, text));
            double t0 = start + shift(start);
            segs.Add(new AsrSegment(t0, t0 + 3, text + "。", "zh", text.Select((c, k) => new AsrChar(t0 + k * 0.4, c.ToString())).Append(new AsrChar(t0 + 2.4, "。")).ToList()));
        }
        return (cues, segs);
    }

    [Fact]
    public void A_transcript_in_sync_gives_a_small_offset_and_no_suspicion()
    {
        var (cues, segs) = Synthetic(_ => 0.08);
        var r = AvSync.Measure(cues, segs);
        Assert.Equal(100, r.Matches);
        Assert.Equal(0.08, r.Offset, 2);
        Assert.Equal(0, r.Drift, 2);
        Assert.False(r.Suspect);
        Assert.Equal(2, r.Windows.Count);
        Assert.Equal(50, r.Windows[0].N);
    }

    [Fact]
    public void A_second_half_shifted_by_half_a_second_gives_drift_and_suspect()
    {
        var (cues, segs) = Synthetic(t => t < 300 ? 0.0 : 0.5);
        var r = AvSync.Measure(cues, segs);
        Assert.Equal(0.5, r.Drift, 2);
        Assert.True(r.Suspect);
        Assert.Equal(0.0, r.Windows[0].Median, 2);
        Assert.Equal(0.5, r.Windows[1].Median, 2);
        Assert.Equal(0.25, r.Offset, 1);      // the overall median sits between the two halves
    }

    [Fact]
    public void A_constant_offset_over_a_quarter_second_is_suspect_without_drift()
    {
        var r = AvSync.Measure(Synthetic(_ => 0.6).Cues, Synthetic(_ => 0.6).Segs);
        Assert.Equal(0.6, r.Offset, 2); Assert.Equal(0, r.Drift, 2); Assert.True(r.Suspect);
    }

    [Fact]
    public void Short_cues_runs_not_starting_at_the_cue_start_and_far_matches_are_ignored()
    {
        var stream = AvSync.CharStream([new AsrSegment(10, 14, "你好世界朋友", "zh", [])]);
        var times = stream.Select(s => s.T).ToArray();
        Assert.Null(AvSync.CueOffset("你好吗", 10, stream, 0, stream.Count));                 // 3 characters: too short
        Assert.NotNull(AvSync.CueOffset("你好世界吗", 10, stream, 0, stream.Count));          // 4 matching characters from the first one
        Assert.Null(AvSync.CueOffset("吗你好世界", 10, stream, 0, stream.Count));              // the run starts at the cue's second character
        var near = AvSync.Measure([(100, "你好世界朋友")], [new AsrSegment(10, 14, "你好世界朋友", "zh", [])]); // 90 s away: outside ±4 s
        Assert.Equal(0, near.Matches);
    }

    [Fact]
    public void Median_and_quantiles_and_the_window_needs_20_matches_for_drift()
    {
        Assert.Equal(2.5, AvSync.Median([1, 2, 3, 4]));
        Assert.Equal(2, AvSync.Median([5, 1, 2]));
        Assert.Equal(2.5, AvSync.Quantile([1, 2, 3, 4], .5));
        // 10 cues in each of two windows, shifted differently: windows have n < 20, so drift stays 0
        var (cues, segs) = Synthetic(t => t < 300 ? 0 : 0.5);
        var few = AvSync.Measure(cues.Where((_, i) => i % 5 == 0).ToList(), segs);
        Assert.All(few.Windows, w => Assert.True(w.N < AvSync.MinWindowN));
        Assert.Equal(0, few.Drift);
    }

    [Fact]
    public void Sync_reads_the_stored_files_including_the_reference_transcript_format()
    {
        var lines = System.Text.Encoding.UTF8.GetBytes("""{"zh":[{"start":1.0,"end":2.0,"text":"顧長淵來了","t2s":"顾长渊来了"}]}""");
        var asr = System.Text.Encoding.UTF8.GetBytes("""{"segments":[{"start":1.2,"end":3,"text":"顾长渊来了。","words":[[1.3,0,"顾"],[1.5,0,"长"],[1.7,0,"渊"],[1.9,0,"来"],[2.1,0,"了"],[2.3,0,"。"]]}]}""");
        var r = AvSync.FromFiles(lines, asr)!;
        Assert.Equal(1, r.Matches);
        Assert.Equal(0.3, r.Offset, 2);
        var json = System.Text.Json.JsonDocument.Parse(AvSync.ToJson(r)).RootElement;
        Assert.True(json.TryGetProperty("offset", out _) && json.TryGetProperty("drift", out _) && json.TryGetProperty("suspect", out _));
        Assert.Equal(1, json.GetProperty("windows").GetArrayLength());
    }
}
