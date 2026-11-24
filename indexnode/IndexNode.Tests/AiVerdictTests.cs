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
