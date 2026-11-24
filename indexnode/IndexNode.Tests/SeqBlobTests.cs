using System.Text.Json.Nodes;
using Xunit;

namespace IndexNode.Tests;

[Collection("SeqBlobs")]
public class SeqBlobTests
{
    static readonly Paths P = new("index/");

    [Fact]
    public async Task Concurrent_appends_get_unique_increasing_seqs_and_the_blobs_are_the_predecessor_list()
    {
        var s = new FakeStore(); var ids = Enumerable.Range(0, 60).Select(i => "u" + i.ToString("D3")).ToArray();
        var pos = new System.Collections.Concurrent.ConcurrentDictionary<string, SeqBlobs.Pos>();
        await Parallel.ForEachAsync(ids, new ParallelOptions { MaxDegreeOfParallelism = 12 }, async (id, ct) => pos[id] = await SeqBlobs.AppendAsync(s, P, id, id.EndsWith('7'), ct));
        Assert.Equal(60, pos.Values.Select(p => p.Seq).Distinct().Count());
        var read = await SeqBlobs.ReadAsync(s, P, default);
        Assert.Equal(60, read.Count);
        Assert.All(read, e => Assert.Equal(pos[e.Id].Seq, e.Seq));
        Assert.Equal(read.Select(e => e.Seq).Order(), read.Select(e => e.Seq));   // file order = seq order
        Assert.All(read, e => Assert.Equal(e.Id.EndsWith('7'), e.Ai));
        // a reader holding a prefix reads only what follows
        await SeqBlobs.AppendAsync(s, P, "late", false, default);
        var more = await SeqBlobs.ReadAsync(s, P, default, read);
        Assert.Equal(61, more.Count); Assert.Equal("late", more[^1].Id);
        Assert.Single(more.Where(e => e.Id == "u000"));
    }

    [Fact]
    public async Task A_repeated_id_keeps_its_first_position()
    {
        var s = new FakeStore();
        var a = await SeqBlobs.AppendAsync(s, P, "x", false, default); await SeqBlobs.AppendAsync(s, P, "y", false, default); await SeqBlobs.AppendAsync(s, P, "x", false, default);
        var r = await SeqBlobs.ReadAsync(s, P, default);
        Assert.Equal(["x", "y"], r.Select(e => e.Id)); Assert.Equal(a.Seq, r[0].Seq);
    }

    [Fact]
    public async Task Rollover_at_the_block_limit_creates_the_next_blob_and_seq_stays_unique_with_racing_writers()
    {
        var old = SeqBlobs.BlockLimit; SeqBlobs.BlockLimit = 5;
        try
        {
            var s = new FakeStore(); var ids = Enumerable.Range(0, 23).Select(i => "r" + i).ToArray();
            var pos = new System.Collections.Concurrent.ConcurrentDictionary<string, SeqBlobs.Pos>();
            await Parallel.ForEachAsync(ids, new ParallelOptions { MaxDegreeOfParallelism = 8 }, async (id, ct) => pos[id] = await SeqBlobs.AppendAsync(s, P, id, false, ct));
            Assert.Equal(23, pos.Values.Select(p => p.Seq).Distinct().Count());
            Assert.True(pos.Values.Max(p => p.Blob) >= 4);
            Assert.All(pos.Values, p => Assert.Equal((long)p.Blob * 5 + p.Count, p.Seq));
            var read = await SeqBlobs.ReadAsync(s, P, default);
            Assert.Equal(23, read.Count); Assert.Equal(read.Select(e => e.Seq).Order(), read.Select(e => e.Seq));
        }
        finally { SeqBlobs.BlockLimit = old; }
    }

    [Fact]
    public async Task Assign_appends_once_merges_the_row_and_a_second_call_does_not_append_again()
    {
        var s = new FakeStore(); var t = new FakeMediaTable();
        await SeqBlobs.AppendAsync(s, P, "first", false, default);
        var (mine, before) = await SeqBlobs.AssignAsync(s, P, t, "me", true, default);
        Assert.Equal(["first"], before.Select(e => e.Id));
        var row = await t.GetAsync("upload", "me", null, default);
        Assert.Equal(mine.Seq, long.Parse(row!.Props["seq"]!.ToString())); Assert.Equal(mine.Blob, int.Parse(row.Props["seqBlob"]!.ToString())); Assert.Equal(mine.Offset, long.Parse(row.Props["seqOff"]!.ToString()));
        int appends = s.Ops.GetValueOrDefault("append");
        await SeqBlobs.AssignAsync(s, P, t, "me", true, default);
        Assert.Equal(appends, s.Ops.GetValueOrDefault("append"));
        Assert.Empty(SeqBlobs.ActorsPredecessors(new[] { mine }.AsEnumerable()));   // an AI entry is skipped...
        Assert.Single(SeqBlobs.ActorsPredecessors(new[] { mine }, new HashSet<string> { "me" }));   // ...unless added by hand
    }

    [Fact]
    public async Task Renumber_local_then_apply_equals_direct_and_a_rerun_is_a_no_op()
    {
        var s = new FakeStore(); var t = new FakeMediaTable();
        foreach (var (id, seq, ai, ok) in new[] { ("a", 10L, false, true), ("b", 20L, true, true), ("c", 30L, false, false), ("d", 40L, false, true) })
        {
            await t.MergeAsync("upload", id, new JsonObject { ["seq"] = seq, ["aiFlag"] = ai, ["eligible"] = ok, ["data"] = "full" }, default);
            await SeqBlobs.AppendAsync(s, P, id, ai, default);
        }
        var plan = await SeqBlobs.PlanFromTableAsync(t, default);
        Assert.Equal(["a", "b", "d"], plan.Map.Select(m => m.Id)); Assert.Equal([1L, 2L, 3L], plan.Map.Select(m => m.New)); Assert.Equal(["c"], plan.Dropped);
        var dir = Path.Combine(Path.GetTempPath(), "seqplan-" + Guid.NewGuid().ToString("N"));
        try
        {
            SeqBlobs.WriteLocal(plan, dir);
            var local = SeqBlobs.ReadLocal(dir);
            Assert.Equal(plan.Map, local.Map); Assert.Equal(plan.Dropped, local.Dropped);
            await SeqBlobs.ApplyAsync(s, P, t, local, default);
            var read = await SeqBlobs.ReadAsync(s, P, default);
            Assert.Equal(["a", "b", "d"], read.Select(e => e.Id)); Assert.Equal([1L, 2L, 3L], read.Select(e => e.Seq)); Assert.True(read[1].Ai);
            var again = await SeqBlobs.PlanFromTableAsync(t, default);
            Assert.Equal(["a", "b", "d"], again.Map.Select(m => m.Id)); Assert.Equal([1L, 2L, 3L], again.Map.Select(m => m.New));
            Assert.All(again.Map, m => Assert.Equal(m.New, m.Old));   // idempotent: nothing moves
            Assert.Equal(3, long.Parse((await t.GetAsync("upload", "d", null, default))!.Props["seq"]!.ToString()));
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }
}
