using System.Text.Json.Nodes;

namespace IndexNode.Tests;

[Collection("console")]
public class Backfill0133Tests
{
    static readonly Paths P = new("index-test/");

    [Theory]
    [InlineData(25059)] [InlineData(28944.5)] [InlineData(600)]
    public async Task Backfill_reads_a_long_duration_from_idx_summary(double dur)
    {
        var s = new FakeStore();
        await s.PutAsync(P.Video("m2D3ptvZ1nA", "download.zip"), [1, 2, 3], null, null, false, default);
        await s.SetMetadataAsync(P.Video("m2D3ptvZ1nA", "download.zip"), new Dictionary<string, string> { ["idx_summary"] = "{\"v\":1,\"bytes\":9000000000,\"height\":480,\"durationSeconds\":" + dur.ToString(System.Globalization.CultureInfo.InvariantCulture) + ",\"subs\":[]}" }, null, default);
        var rep = await VideosBackfill.BackfillAsync(s, P, false, 0.5, 1, null, default);
        Assert.Empty(rep.Failed); Assert.Equal(1, rep.Created);
        var root = JsonNode.Parse((await s.GetAsync(VideoJson.Name(P, "m2D3ptvZ1nA"), default))!.Data)!.AsObject();
        Assert.Equal(dur, VideoJson.Duration(root));
    }

    static byte[] Zip(params (string Name, string Json)[] members)
    {
        var ms = new MemoryStream();
        using (var za = new System.IO.Compression.ZipArchive(ms, System.IO.Compression.ZipArchiveMode.Create, true)) foreach (var (n, j) in members) { using var w = new StreamWriter(za.CreateEntry(n).Open()); w.Write(j); }
        return ms.ToArray();
    }

    [Fact]
    public async Task An_empty_idx_summary_reads_downloaded_json_inside_download_zip_then_info_json()
    {
        var s = new FakeStore();
        await s.PutAsync(P.Video("45EoRPziG7s", "download.zip"), Zip(("downloaded.json", "{\"v\":1,\"durationSeconds\":26100.5}")), null, null, false, default);
        await s.SetMetadataAsync(P.Video("45EoRPziG7s", "download.zip"), new Dictionary<string, string> { ["idx_summary"] = "{}" }, null, default);
        await s.PutAsync(P.Video("AUjZhz0dP3U", "download.zip"), Zip(("info.json", "{\"title\":\"t\",\"duration\":31000}")), null, null, false, default);
        await s.SetMetadataAsync(P.Video("AUjZhz0dP3U", "download.zip"), new Dictionary<string, string> { ["idx_summary"] = "{}" }, null, default);
        var rep = await VideosBackfill.BackfillAsync(s, P, false, 0.5, 1, null, default);
        Assert.Empty(rep.Failed); Assert.Equal(2, rep.Created);
        var a = JsonNode.Parse((await s.GetAsync(VideoJson.Name(P, "45EoRPziG7s"), default))!.Data)!.AsObject();
        Assert.Equal(26100.5, VideoJson.Duration(a)); Assert.Equal("download.zip:downloaded.json", (string?)a["youtube"]!["durationSource"]);
        var c = JsonNode.Parse((await s.GetAsync(VideoJson.Name(P, "AUjZhz0dP3U"), default))!.Data)!.AsObject();
        Assert.Equal(31000, VideoJson.Duration(c));
    }

    [Fact]
    public async Task No_duration_anywhere_is_recorded_as_unknown_not_a_failure()
    {
        var s = new FakeStore();
        await s.PutAsync(P.Video("nodur000001", "download.zip"), Zip(("other.json", "{}")), null, null, false, default);
        await s.SetMetadataAsync(P.Video("nodur000001", "download.zip"), new Dictionary<string, string> { ["idx_summary"] = "{}" }, null, default);
        var rep = await VideosBackfill.BackfillAsync(s, P, false, 0.5, 1, null, default);
        Assert.Empty(rep.Failed); Assert.Equal(1, rep.Created);
        var r = JsonNode.Parse((await s.GetAsync(VideoJson.Name(P, "nodur000001"), default))!.Data)!.AsObject();
        Assert.Null(VideoJson.Duration(r)); Assert.Null(r["youtube"]!["durationSeconds"]); Assert.Equal("unknown", (string?)r["youtube"]!["durationSource"]);
    }

    [Fact]
    public async Task The_stored_video_header_is_probed_with_ffprobe_as_the_last_resort()
    {
        var dir = Path.Combine(Path.GetTempPath(), "bf0133-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(dir); var mp4 = Path.Combine(dir, "v.mp4");
        var psi = new System.Diagnostics.ProcessStartInfo("ffmpeg") { RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (var x in new[] { "-v", "error", "-f", "lavfi", "-i", "color=c=black:s=64x64:d=3", "-movflags", "+faststart", "-pix_fmt", "yuv420p", mp4 }) psi.ArgumentList.Add(x);
        using (var p = System.Diagnostics.Process.Start(psi)!) { await p.WaitForExitAsync(); Assert.Equal(0, p.ExitCode); }
        var s = new FakeStore(); await s.PutAsync(P.Video("probe000001", "video.mp4"), await File.ReadAllBytesAsync(mp4), null, null, false, default);
        var rep = await VideosBackfill.BackfillAsync(s, P, false, 0.5, 1, null, default);
        Assert.Empty(rep.Failed);
        var r = JsonNode.Parse((await s.GetAsync(VideoJson.Name(P, "probe000001"), default))!.Data)!.AsObject();
        Assert.InRange(VideoJson.Duration(r) ?? 0, 2.9, 3.2); Assert.Equal("ffprobe", (string?)r["youtube"]!["durationSource"]);
        Directory.Delete(dir, true);
    }

    [Fact]
    public async Task EnsureAsync_builds_a_missing_video_json_and_leaves_an_existing_one()
    {
        var s = new FakeStore();
        await s.PutAsync(P.Video("claim000001", "download.zip"), [1], null, null, false, default);
        await s.SetMetadataAsync(P.Video("claim000001", "download.zip"), new Dictionary<string, string> { ["idx_summary"] = "{\"durationSeconds\":25059}" }, null, default);
        var r = await VideosBackfill.EnsureAsync(s, P, "claim000001", 0.5, default);
        Assert.Equal(25059, VideoJson.Duration(r)); Assert.NotNull(await s.GetAsync(VideoJson.Name(P, "claim000001"), default));
        var again = await VideosBackfill.EnsureAsync(s, P, "claim000001", 0.5, default); Assert.Equal(25059, VideoJson.Duration(again));
    }

    [Fact]
    public void Check_pairs_counts_the_listed_pairs_that_share_a_group()
    {
        var set = new GroupSet { Groups = [new DramaGroup { Id = "g-a", Members = [new GroupMember { Upload = "a" }, new GroupMember { Upload = "b" }] }, new DramaGroup { Id = "g-c", Members = [new GroupMember { Upload = "c" }] }] };
        var (same, total, apart) = GroupsCommand.CheckPairs(set, [("a", "b"), ("a", "c"), ("a", "zz")], ["a", "b", "c"]);
        Assert.Equal((1, 3), (same, total)); Assert.Equal(2, apart.Count); Assert.Contains("different groups", apart[0]); Assert.Contains("not indexed", apart[1]);
    }

    [Fact]
    public void Bootstrap_breaks_a_join_cycle_so_both_uploads_share_one_group_and_the_earlier_owns_it()
    {
        GroupProposal J(string to) => new(GroupProposal.JoinKind, null, to, 0, 0.9, 0.9, 8, 0.9, false, []);
        var set = new GroupSet();
        set.Apply([new NewUpload("b", "zh", false, 600, "2026-01-02T00:00:00Z", J("a")), new NewUpload("a", "zh", false, 600, "2026-01-01T00:00:00Z", J("b"))], null, breakCycles: true);
        Assert.Single(set.Groups); Assert.Equal("g-a", set.Groups[0].Id); Assert.Equal(2, set.Groups[0].Members.Count);
        var plain = new GroupSet(); plain.Apply([new NewUpload("b", "zh", false, 1, "2", J("a")), new NewUpload("a", "zh", false, 1, "1", J("b"))]);
        Assert.Equal(2, plain.Groups.Count);   // without bootstrap a cycle still leaves each alone
    }
}

/// <summary>Spawns the real ffmpeg/ffprobe by PATH, so it runs with the other path-env tests (they put fake ones in front of PATH).</summary>
[Collection("path-env")]
public class Backfill0133ProbeTests
{
    static readonly Paths P = new("index-test/");


}
