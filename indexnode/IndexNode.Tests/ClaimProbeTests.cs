using Xunit;

namespace IndexNode.Tests;

/// <summary>0.13.37: a version bump that makes many subjects candidates costs no per-candidate read; --repair-only keeps the given --stages.</summary>
public class ClaimProbeTests
{
    static readonly Paths P = new("index/");
    const string At = "2026-01-01T00:00:00Z";

    [Fact]
    public async Task A_candidate_with_nothing_for_this_node_to_run_touches_no_blob_before_the_claim()
    {
        var s = new FakeStore(); var q = new JobQueue(s, P, "n"); await q.LoadAsync(default);
        var lines = new List<QLine>();
        for (int i = 0; i < 1000; i++) { var id = "v" + i.ToString("D4"); lines.Add(new QLine { T = "enter", S = id, Data = "full", At = At }); lines.Add(QLine.Add(id, "process", new JobDescriptor { Stage = "process", JobId = id }, At, null, null, "done")); }
        await q.AppendLinesAsync(lines, default);
        for (int i = 0; i < 100; i++) await s.PutAsync(P.Active("v" + i.ToString("D4")), "{}"u8.ToArray(), null, null, true, default);   // 100 stale claim markers
        var before = s.Ops.Sum(o => o.Value); var callsBefore = s.Calls.Count;
        int claimed = 0;
        for (int i = 0; i < 1000; i++) if (await q.TryClaimAsync("v" + i.ToString("D4"), (_, sub) => null, default) != null) claimed++;   // the picker says: nothing for this node
        Assert.Equal(0, claimed);
        Assert.Equal(before, s.Ops.Sum(o => o.Value));   // 0 requests of any kind (no HEAD, no put, no lease)
        Assert.Equal(callsBefore, s.Calls.Count);
    }

    [Fact]
    public void Repair_only_is_a_flag_it_keeps_the_stages_given_and_the_printed_stage_line_matches()
    {
        var a = Args.Parse(["run", "--shadow", "--out-local", "shadow38", "--set", "versions.cast=3", "--repair-only", "--stages", "process,cast,actors,actors-index", "--lanes", "2", "--max-jobs", "6"]);
        Assert.True(a.Has("repair-only")); Assert.Equal("process,cast,actors,actors-index", a.Get("stages")); Assert.Equal("2", a.Get("lanes")); Assert.Equal("6", a.Get("max-jobs"));
        var stages = Protocol.ExpandStages(a.Get("stages")!.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        Assert.Equal("process,frames,sscd,centers,dino,thumbs,clip,transcribe,ocrsubs,cast,sync,describe,translate,actors,actors-index", string.Join(',', stages));
        Assert.DoesNotContain("classify", stages);
        // the flag in any position
        var b = Args.Parse(["run", "--stages", "cast,actors", "--repair-only"]); Assert.True(b.Has("repair-only")); Assert.Equal("cast,actors", b.Get("stages"));
    }
}
