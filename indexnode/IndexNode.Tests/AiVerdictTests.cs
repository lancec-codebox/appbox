using System.Text.Json.Nodes;
using Xunit;

namespace IndexNode.Tests;

/// <summary>0.13.44: the owner's verdict on an upload (`ai set <id> real|ai`): it wins over the probe's flag, releases or sets the AI holds, and outlives later probe results.</summary>
public class AiVerdictTests
{
    static readonly Paths P = new("index/");
    static QLine Add(string id, string stage, string? hold = null) => new() { T = "add", S = id, Stage = stage, Job = new JobDescriptor { Stage = stage, JobId = id, AddedAt = "2026-11-24T08:00:00Z" }, At = "2026-11-24T08:00:00Z", Due = "2026-11-24T08:00:00Z", Hold = hold, AiScore = hold == null ? null : 0.53 };
    static async Task Doc(FakeStore s, string id, bool flag, double score) => await VideoJson.UpdateAsync(s, P, id, r => { r["youtube"] = new JsonObject { ["title"] = id }; VideoJson.SetAi(r, score, flag, "precheck"); }, default);

    [Fact]
    public void The_verdict_wins_over_the_flag_and_a_later_probe_result_keeps_it()
    {
        var root = new JsonObject(); VideoJson.SetAi(root, 0.53, true, "precheck");
        Assert.True(AiHold.Flagged(root)); Assert.Null(AiHold.Verdict(root));
        ((JsonObject)root["ai"]!)["verdict"] = "real"; ((JsonObject)root["ai"]!)["verdictBy"] = "owner";
        Assert.False(AiHold.Flagged(root)); Assert.Equal("real", AiHold.Verdict(root));
        VideoJson.SetAi(root, 0.71, true, "centers");                       // a later probe run: new score and flag, the verdict stays
        Assert.Equal(0.71, (double)root["ai"]!["score"]!); Assert.Equal("real", (string)root["ai"]!["verdict"]!); Assert.Equal("owner", (string)root["ai"]!["verdictBy"]!);
        Assert.False(AiHold.Flagged(root));
        var missed = new JsonObject(); VideoJson.SetAi(missed, 0.2, false, "precheck"); ((JsonObject)missed["ai"]!)["verdict"] = "ai";
        Assert.True(AiHold.Flagged(missed));                                   // the probe missed it, the owner says AI
        var odd = new JsonObject(); VideoJson.SetAi(odd, 0.9, true, "precheck"); ((JsonObject)odd["ai"]!)["verdict"] = "maybe";
        Assert.Null(AiHold.Verdict(odd)); Assert.True(AiHold.Flagged(odd));    // anything but real or ai is no verdict
        // a check record has no verdict of its own: the upload's video.json decides
        var rec = new JsonObject { ["ai"] = new JsonObject { ["flag"] = true, ["score"] = 0.6 } };
        Assert.True(AiHold.Flagged(rec, null)); Assert.False(AiHold.Flagged(rec, root)); Assert.True(AiHold.Flagged(new JsonObject(), missed));
    }

    [Fact]
    public async Task Real_releases_the_holds_in_both_queues_records_the_verdict_and_a_dry_run_writes_nothing()
    {
        var s = new FakeStore(); await Doc(s, "u1", true, 0.53);
        var proc = new JobQueue(s, P, "t"); await proc.LoadAsync(default);
        var dl = new JobQueue(s, P.DownloadQueue, "t"); await dl.LoadAsync(default);
        await proc.AppendLinesAsync([Add("u1", "process", "ai"), Add("u1", "actors", "ai"), Add("u2", "process", "ai")], default);
        await dl.AppendLinesAsync([Add("u1", "download", "ai")], default);
        var dry = await AiHold.SetVerdictAsync(proc, dl, s, P, "u1", "real", "looked at the sheet", "owner", true, default);
        Assert.Equal(["processing/process", "processing/actors", "download/download"], dry.Released);
        Assert.Equal("ai", proc.Read(st => st.Entry("u1", "process")!.Hold)); Assert.Null(AiHold.Verdict((await VideoJson.ReadAsync(s, P, "u1", default))!.Root));   // nothing written
        var r = await AiHold.SetVerdictAsync(proc, dl, s, P, "u1", "real", "looked at the sheet", "owner", false, default);
        Assert.True(r.Known); Assert.Equal(3, r.Released.Count); Assert.Empty(r.Held);
        Assert.Null(proc.Read(st => st.Entry("u1", "process")!.Hold)); Assert.Null(proc.Read(st => st.Entry("u1", "actors")!.Hold)); Assert.Null(dl.Read(st => st.Entry("u1", "download")!.Hold));
        Assert.Equal("pending", proc.Read(st => st.Entry("u1", "process")!.State));
        Assert.Equal("ai", proc.Read(st => st.Entry("u2", "process")!.Hold));                         // another upload's hold is untouched
        VideoJson.ClearCache(s);
        var ai = (JsonObject)(await VideoJson.ReadAsync(s, P, "u1", default))!.Root["ai"]!;
        Assert.Equal("real", (string)ai["verdict"]!); Assert.Equal("owner", (string)ai["verdictBy"]!); Assert.Equal("looked at the sheet", (string)ai["verdictReason"]!);
        Assert.True((bool)ai["flag"]!); Assert.Equal(0.53, (double)ai["score"]!);                     // the probe's own result is kept as it was
        // a node that starts later replays the same lines and sees the entries released
        var late = new JobQueue(s, P, "late"); await late.LoadAsync(default);
        Assert.Null(late.Read(st => st.Entry("u1", "process")!.Hold));
        Assert.Empty((await AiHold.SetVerdictAsync(proc, dl, s, P, "u1", "real", null, "owner", false, default)).Released);   // a rerun releases nothing more
    }

    [Fact]
    public async Task Ai_holds_the_pending_entries_of_an_upload_the_probe_did_not_flag()
    {
        var s = new FakeStore(); await Doc(s, "m1", false, 0.31);
        var proc = new JobQueue(s, P, "t"); await proc.LoadAsync(default);
        await proc.AppendLinesAsync([Add("m1", "process")], default);
        var r = await AiHold.SetVerdictAsync(proc, null, s, P, "m1", "ai", null, "owner", false, default);
        Assert.Equal(["processing/process"], r.Held); Assert.Empty(r.Released);
        Assert.Equal("ai", proc.Read(st => st.Entry("m1", "process")!.Hold));
        VideoJson.ClearCache(s);
        Assert.True(AiHold.Flagged((await VideoJson.ReadAsync(s, P, "m1", default))!.Root));
        await Assert.ThrowsAsync<ArgumentException>(() => AiHold.SetVerdictAsync(proc, null, s, P, "m1", "maybe", null, "owner", false, default));
        // an upload the store does not know: the queue lines are still written, the report says nothing was recorded
        await proc.AppendLinesAsync([Add("ghost", "process", "ai")], default);
        var g = await AiHold.SetVerdictAsync(proc, null, s, P, "ghost", "real", null, "owner", false, default);
        Assert.False(g.Known); Assert.Equal(["processing/process"], g.Released);
    }

    [Fact]
    public async Task Release_below_frees_the_holds_under_the_threshold_in_both_queues_sets_the_flag_false_and_records_no_verdict()
    {
        var s = new FakeStore();
        await Doc(s, "low1", true, 0.53); await Doc(s, "low2", true, 0.6199); await Doc(s, "edge", true, 0.62); await Doc(s, "high", true, 0.8); await Doc(s, "said", true, 0.5);
        await VideoJson.UpdateAsync(s, P, "said", r => ((JsonObject)r["ai"]!)["verdict"] = "ai", default);   // the owner looked at it: AI, whatever it scores
        var proc = new JobQueue(s, P, "t"); await proc.LoadAsync(default);
        var dl = new JobQueue(s, P.DownloadQueue, "t"); await dl.LoadAsync(default);
        await dl.AppendLinesAsync([Add("low1", "download", "ai"), Add("edge", "download", "ai"), Add("high", "download", "ai"), Add("said", "download", "ai"), Add("free", "download")], default);
        await proc.AppendLinesAsync([Add("low2", "process", "ai"), Add("ghost", "process", "ai")], default);   // ghost: no record; its line carries 0.53
        VideoJson.ClearCache(s);
        var dry = await AiHold.ReleaseBelowAsync(proc, dl, s, P, 0.62, "cli", true, TimeSpan.Zero, default);
        Assert.Equal((1, 2, 2, 1, 0, 2), (dry.Download, dry.Process, dry.KeptAtOrAbove, dry.KeptVerdictAi, dry.KeptNoScore, dry.Records));
        Assert.Equal(["ghost", "low1", "low2"], dry.Ids);
        Assert.Equal("ai", dl.Read(st => st.Entry("low1", "download")!.Hold)); VideoJson.ClearCache(s); Assert.True(AiHold.Flagged((await VideoJson.ReadAsync(s, P, "low1", default))!.Root));   // nothing written
        var r = await AiHold.ReleaseBelowAsync(proc, dl, s, P, 0.62, "cli", false, TimeSpan.Zero, default);
        Assert.Equal((1, 2, 2, 1, 0, 2), (r.Download, r.Process, r.KeptAtOrAbove, r.KeptVerdictAi, r.KeptNoScore, r.Records));
        Assert.Null(dl.Read(st => st.Entry("low1", "download")!.Hold)); Assert.Null(proc.Read(st => st.Entry("low2", "process")!.Hold)); Assert.Null(proc.Read(st => st.Entry("ghost", "process")!.Hold));
        Assert.Equal("ai", dl.Read(st => st.Entry("edge", "download")!.Hold));    // at the threshold is AI, as the flag itself (score >= flag)
        Assert.Equal("ai", dl.Read(st => st.Entry("high", "download")!.Hold)); Assert.Equal("ai", dl.Read(st => st.Entry("said", "download")!.Hold));
        Assert.Null(dl.Read(st => st.Entry("free", "download")!.Hold)); Assert.Equal("pending", dl.Read(st => st.Entry("low1", "download")!.State));
        VideoJson.ClearCache(s);
        foreach (var id in new[] { "low1", "low2" })
        {
            var ai = (JsonObject)(await VideoJson.ReadAsync(s, P, id, default))!.Root["ai"]!;
            Assert.False((bool)ai["flag"]!); Assert.Null(ai["verdict"]); Assert.Equal("precheck", (string)ai["source"]!); Assert.True((double)ai["score"]! < 0.62);   // the score and its source stay; no verdict
        }
        Assert.True(AiHold.Flagged((await VideoJson.ReadAsync(s, P, "high", default))!.Root)); Assert.True(AiHold.Flagged((await VideoJson.ReadAsync(s, P, "said", default))!.Root));
        // a rerun finds nothing more, and a node that starts later sees the same
        var again = await AiHold.ReleaseBelowAsync(proc, dl, s, P, 0.62, "cli", false, TimeSpan.Zero, default);
        Assert.Equal((0, 0, 2, 1, 0), (again.Download, again.Process, again.KeptAtOrAbove, again.KeptVerdictAi, again.Records));
        var late = new JobQueue(s, P.DownloadQueue, "late"); await late.LoadAsync(default); Assert.Null(late.Read(st => st.Entry("low1", "download")!.Hold));
    }

    [Fact]
    public void An_unforced_hold_line_without_a_hold_changes_nothing()
    {
        var st = new QueueState();
        st.Apply(Add("u1", "process", "ai"));
        st.Apply(new QLine { T = "hold", S = "u1", Stage = "process" });                 // what an older writer could never produce; ignored
        Assert.Equal("ai", st.Entry("u1", "process")!.Hold);
        st.Apply(new QLine { T = "hold", S = "u1", Stage = "process", Force = true });
        Assert.Null(st.Entry("u1", "process")!.Hold);
    }
}
