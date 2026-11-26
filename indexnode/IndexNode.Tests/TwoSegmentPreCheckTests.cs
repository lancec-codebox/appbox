using Xunit;

namespace IndexNode.Tests;

/// <summary>0.13.15: the two-segment pre-check rule. Fixture ids: new drama N (duration chosen per test), group "g1" with members m1/m2/m3, ungrouped upload "solo".</summary>
public class TwoSegmentPreCheckTests
{
    static readonly PreCheckSettings S = new();

    /// <summary>Segments centred at 0.30 and 0.70 of the new video (gap 0.4 N); per upload: (share1, time1, share2, time2).</summary>
    static PreCheckAnalysis An(double n, Dictionary<string, (double S1, double T1, double S2, double T2)> m, string? group = "g1", Dictionary<string, double>? durations = null, Dictionary<string, string?>? langs = null, SpeechResult? speech = null)
    {
        var w = PreCheck.Windows(n, 300, S.WindowPositions);
        var seg = new List<PreCheckSegment>();
        for (int k = 0; k < w.Count; k++)
            seg.Add(new PreCheckSegment(w[k].Position, w[k].Start, w[k].End, 30, m.Where(x => (k == 0 ? x.Value.S1 : x.Value.S2) > 0).ToDictionary(x => x.Key, x => new SegmentShare(k == 0 ? x.Value.S1 : x.Value.S2, k == 0 ? x.Value.T1 : x.Value.T2, (int)Math.Round((k == 0 ? x.Value.S1 : x.Value.S2) * 30)), StringComparer.Ordinal)));
        var ug = m.Keys.ToDictionary(u => u, u => group != null && u.StartsWith('m') ? group : null, StringComparer.Ordinal);
        var copies = m.Keys.Where(u => u.StartsWith('m')).Select(u => new GroupCopy(u, langs != null && langs.TryGetValue(u, out var l) ? l : "zh", null, durations?.GetValueOrDefault(u))).ToList();
        return new PreCheckAnalysis(null, 1, 60, 0, [], 0.1, new Dictionary<string, double>()) { Segments = seg, UploadGroups = ug, GroupMembers = group == null ? new Dictionary<string, IReadOnlyList<GroupCopy>>() : new Dictionary<string, IReadOnlyList<GroupCopy>> { [group] = copies }, Speech = speech };
    }

    static (double, double, double, double) Ok(double t1, double t2) => (0.95, t1, 0.93, t2);
    static Dictionary<string, double> D(double m1, double m2 = 0, double m3 = 0) => new() { ["m1"] = m1, ["m2"] = m2, ["m3"] = m3 };

    [Fact]
    public void Two_matching_segments_in_order_give_a_duplicate()
    {
        var d = PreCheck.Decide(An(6000, new() { ["m1"] = Ok(1800, 4200) }, durations: D(6000)), 6000, _ => null, S);
        Assert.Equal("duplicate", d.Decision); Assert.Equal("m1", d.MatchedMember); Assert.Single(d.Checked); Assert.True(d.Checked[0].Passed);
    }

    [Fact]
    public void A_48_minute_part_inside_a_100_minute_member_is_a_subset()
    {
        // N = 2880 s: segments at 0.30/0.70 of N (gap 1152 s); they sit at 1500 s and 4000 s in the 6000 s member
        var d = PreCheck.Decide(An(2880, new() { ["m1"] = Ok(1500, 4000) }, durations: D(6000)), 2880, _ => null, S);
        Assert.Equal("subset", d.Decision); Assert.True(PreCheckDecision.IsSkip(d.Decision));
        Assert.Equal("m1", d.MatchedMember); Assert.Equal("m1", (string?)d.Extra!["subsetOf"]);
        Assert.Equal(6000, (double)d.Extra["memberDuration"]!); Assert.Equal(2880, (double)d.Extra["duration"]!);
        Assert.Equal(1500, (double)d.Extra["timesInMember"]![0]!); Assert.Equal(4000, (double)d.Extra["timesInMember"]![1]!);
    }

    [Fact]
    public void Reversed_segments_or_different_members_are_not_authoritative()
    {
        var d = PreCheck.Decide(An(6000, new() { ["m1"] = (0.95, 4200, 0.93, 1800) }, durations: D(6000)), 6000, _ => null, S);
        Assert.Equal("overlap", d.Decision); Assert.Null(d.MatchedMember); Assert.Contains("out of order", d.Checked[0].Reason);
        var split = An(6000, new() { ["m1"] = (0.95, 1800, 0.0, 0), ["m2"] = (0.0, 0, 0.93, 4200) }, durations: D(6000, 6000));   // seg 1 matches m1 only, seg 2 m2 only
        var e = PreCheck.Decide(split, 6000, _ => null, S);
        Assert.Equal("overlap", e.Decision); Assert.Null(e.MatchedMember); Assert.Equal(2, e.Checked.Count); Assert.All(e.Checked, c => Assert.False(c.Passed));
    }

    [Fact]
    public void An_overlap_of_the_same_length_stops_and_one_of_another_length_is_processed()
    {
        // 0.13.61 (owner, 2026-11-25: "Yes, same length only"): both segments match m1 in order at 0.60+, not at 0.80
        static (double, double, double, double) Weak(double t1, double t2) => (0.73, t1, 0.70, t2);
        var d = PreCheck.Decide(An(6000, new() { ["m1"] = Weak(1800, 4200) }, durations: D(6050)), 6000, _ => null, S);
        Assert.Equal("duplicate", d.Decision); Assert.Equal("m1", d.MatchedMember); Assert.True((bool)d.Extra!["sameLengthOverlap"]!);
        Assert.False(d.Checked[0].Passed); Assert.Contains("length is the same", d.Checked[0].Reason); Assert.Contains("overlap of the same length", d.Reasons[1]);
        // the member is a compilation (far longer than the new upload), or a shorter cut: an overlap, processed
        Assert.Equal("overlap", PreCheck.Decide(An(6000, new() { ["m1"] = Weak(1800, 4200) }, durations: D(9000)), 6000, _ => null, S).Decision);
        Assert.Equal("overlap", PreCheck.Decide(An(6000, new() { ["m1"] = Weak(1800, 4200) }, durations: D(5000)), 6000, _ => null, S).Decision);
        // same length, but only one segment reaches 0.60, or the two are out of order: an overlap
        Assert.Equal("overlap", PreCheck.Decide(An(6000, new() { ["m1"] = (0.73, 1800, 0.40, 4200) }, durations: D(6000)), 6000, _ => null, S).Decision);
        Assert.Equal("overlap", PreCheck.Decide(An(6000, new() { ["m1"] = Weak(4200, 1800) }, durations: D(6000)), 6000, _ => null, S).Decision);
        // switched off: as before 0.13.61
        Assert.Equal("overlap", PreCheck.Decide(An(6000, new() { ["m1"] = Weak(1800, 4200) }, durations: D(6050)), 6000, _ => null, new PreCheckSettings { OverlapSameLengthStops = false }).Decision);
        // a member that passes at 0.80 decides, wherever it stands: here m2 passes and the upload is longer than it, so it is processed
        var both = PreCheck.Decide(An(6000, new() { ["m1"] = Weak(1800, 4200), ["m2"] = Ok(1500, 3900) }, durations: D(6000, 5000)), 6000, _ => null, S);
        Assert.Equal("longer", both.Decision); Assert.Equal("m2", both.MatchedMember);
        // the canonical exception holds for it too: zh audio and the only known copy is a dub
        var zh = An(6000, new() { ["m1"] = Weak(1800, 4200) }, durations: D(6000), langs: new() { ["m1"] = "en" }, speech: new SpeechResult("zh", false, 3, 40, 60, null));
        Assert.Equal("canonical", PreCheck.Decide(zh, 6000, _ => null, S).Decision);
    }

    [Fact]
    public void A_gap_shrink_within_the_tolerance_is_fine_and_a_gap_that_grows_too_much_is_not()
    {
        // the gap in N is 2400 s; in M 2400-120 = 2280 s passes (tolerance 120), 2279 s does not; a LONGER gap in M is fine
        Assert.Equal("duplicate", PreCheck.Decide(An(6000, new() { ["m1"] = Ok(1800, 1800 + 2280) }, durations: D(6000)), 6000, _ => null, S).Decision);
        var d = PreCheck.Decide(An(6000, new() { ["m1"] = Ok(1800, 1800 + 2279) }, durations: D(6000)), 6000, _ => null, S);
        Assert.Equal("overlap", d.Decision); Assert.Contains("shorter than the gap", d.Checked[0].Reason);
        Assert.Equal("duplicate", PreCheck.Decide(An(6000, new() { ["m1"] = Ok(1000, 5000) }, durations: D(6000)), 6000, _ => null, S).Decision);
    }

    [Fact]
    public void Longer_than_the_member_plus_the_slack_is_longer_and_processed()
    {
        // slack = max(2% of 6000 = 120 s, 60 s) = 120 s
        Assert.Equal("duplicate", PreCheck.Decide(An(6120, new() { ["m1"] = Ok(1800, 4200) }, durations: D(6000)), 6120, _ => null, S).Decision);
        var d = PreCheck.Decide(An(6121, new() { ["m1"] = Ok(1800, 4200) }, durations: D(6000)), 6121, _ => null, S);
        Assert.Equal("longer", d.Decision); Assert.False(PreCheckDecision.IsSkip(d.Decision)); Assert.Equal("m1", d.MatchedMember);
    }

    [Fact]
    public void Every_member_is_checked_in_turn_and_the_third_one_passes()
    {
        // ranked by share sum: m2 (0.78+1.00) then m1 (1.00+0.75: the best for seg 1, fails seg 2) then m3 (0.85+0.85 passes both)
        var a = An(6000, new() { ["m1"] = (1.0, 1800, 0.75, 4200), ["m2"] = (0.78, 1800, 1.0, 4200), ["m3"] = (0.85, 1800, 0.85, 4200) }, durations: D(6000, 6000, 6000));
        var d = PreCheck.Decide(a, 6000, _ => null, S);
        Assert.Equal("duplicate", d.Decision); Assert.Equal("m3", d.MatchedMember);
        Assert.Equal(["m2", "m1", "m3"], d.Checked.Select(c => c.Member).ToArray());
        Assert.Equal([false, false, true], d.Checked.Select(c => c.Passed).ToArray());
        // the same group with no member passing, the members far longer than the upload: overlap, every member recorded
        var weak = new Dictionary<string, (double, double, double, double)> { ["m1"] = (1.0, 1800, 0.75, 4200), ["m2"] = (0.78, 1800, 1.0, 4200), ["m3"] = (0.70, 1800, 0.85, 4200) };
        var none = PreCheck.Decide(An(6000, weak, durations: D(9000, 9000, 9000)), 6000, _ => null, S);
        Assert.Equal("overlap", none.Decision); Assert.Equal(3, none.Checked.Count);
        // 0.13.61: the members have the upload's length: the first one tried that matches both segments at 0.60 makes it a copy, and every member is still recorded
        var same = PreCheck.Decide(An(6000, weak, durations: D(6000, 6000, 6000)), 6000, _ => null, S);
        Assert.Equal("duplicate", same.Decision); Assert.Equal("m2", same.MatchedMember); Assert.Equal(3, same.Checked.Count); Assert.All(same.Checked, c => Assert.False(c.Passed));
    }

    [Fact]
    public void Low_shares_are_new_and_the_canonical_exception_still_applies()
    {
        Assert.Equal("new", PreCheck.Decide(An(6000, new() { ["m1"] = (0.5, 1800, 0.4, 4200) }, durations: D(6000)), 6000, _ => null, S).Decision);
        Assert.Equal("overlap", PreCheck.Decide(An(6000, new() { ["m1"] = (0.7, 1800, 0.65, 4200) }, durations: D(9000)), 6000, _ => null, S).Decision);   // 0.13.61: against a member of the same length this is a copy
        var dubs = new Dictionary<string, string?> { ["m1"] = "en", ["m2"] = "ja" };
        var zh = new SpeechResult("zh", false, 3, 40, 60, null);
        var a = An(6000, new() { ["m1"] = Ok(1800, 4200), ["m2"] = Ok(1800, 4200) }, durations: D(6000, 6000), langs: dubs, speech: zh);
        Assert.Equal("canonical", PreCheck.Decide(a, 6000, _ => null, S).Decision);                  // zh audio, every copy a dub: processed
        Assert.True(PreCheck.SpeechNeeded(a with { Speech = null }, PreCheck.Decide(a with { Speech = null }, 6000, _ => null, S)));   // asked for exactly here
        var zhCopies = An(6000, new() { ["m1"] = Ok(1800, 4200) }, durations: D(6000));
        var dd = PreCheck.Decide(zhCopies, 6000, _ => null, S);
        Assert.Equal("duplicate", dd.Decision); Assert.False(PreCheck.SpeechNeeded(zhCopies, dd));   // a zh copy in the group: the language ID is not run
        Assert.False(PreCheck.SpeechNeeded(a, PreCheck.Decide(An(6000, new() { ["m1"] = (0.5, 1800, 0.4, 4200) }, durations: D(6000)), 6000, _ => null, S)));
    }

    [Fact]
    public void The_two_download_sections_and_the_windows()
    {
        var w = PreCheck.Windows(6000, 300, S.WindowPositions);
        Assert.Equal([(1650.0, 1950.0), (4050.0, 4350.0)], w.Select(x => (x.Start, x.End)).ToArray());   // 0.30*6000 = 1800 and 0.70*6000 = 4200, +-150
        var args = PreCheck.SampleArgs(new PreCheckSettings { VideoHeight = 240 }, w.Select(x => (x.Start, x.End)).ToList(), "u");
        Assert.Equal(2, args.Count(x => x == "--download-sections"));
        Assert.Equal(["*1650-1950", "*4050-4350"], args.Where((x, i) => i > 0 && args[i - 1] == "--download-sections").ToArray());
        Assert.Equal("u", args[^1]);
        Assert.Equal([(0.0, 200.0)], PreCheck.Windows(200, 300, S.WindowPositions).Select(x => (x.Start, x.End)).ToArray());   // a short video is one whole segment
    }

    [Fact]
    public void Old_records_keep_their_partial_and_a_single_window_analysis_writes_overlap()
    {
        Assert.Equal("partial", PreCheckDecision.Partial);
        var single = new PreCheckAnalysis(null, 1, 60, 0, [new PreCheckMatch("up1", 0.65, 0.9, 10, 39)], 0.1, new Dictionary<string, double>());
        Assert.Equal("overlap", PreCheck.Decide(single, 1800, _ => 1800, S).Decision);
    }
}
