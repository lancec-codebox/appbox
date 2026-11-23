using Xunit;

namespace IndexNode.Tests;

/// <summary>0.13.35: the snapshot keeps a finished subject's process/group entries and step versions, whatif agrees between a snapshot node and a full replay, and `queue rebuild-snapshot` restores what the 0.13.0-0.13.34 compaction dropped. In-memory store only: no disk paths.</summary>
public class SnapshotRebuildTests
{
    static readonly Paths P = new("index/");
    sealed class Clock { public DateTimeOffset T = new(2026, 11, 23, 8, 0, 0, TimeSpan.Zero); public DateTimeOffset Now() => T; }
    const string At = "2026-01-01T00:00:00Z";

    static List<QLine> Upload(string id, int seq, int? castVer)
    {
        var l = new List<QLine> { new() { T = "enter", S = id, Data = "full", At = At }, QLine.Add(id, "download", new JobDescriptor { Stage = "download", JobId = id }, At), new() { T = "done", S = id, Stage = "download", At = At },
            QLine.Add(id, "process", new JobDescriptor { Stage = "process", JobId = id }, At) };
        foreach (var step in NodeEngine.StandardSteps) l.Add(new QLine { T = "done", S = id, Stage = "process", Step = step, StepVer = step == "cast" ? castVer : null, At = At });
        l.Add(new QLine { T = "done", S = id, Stage = "process", At = At }); l.Add(new QLine { T = "seq", S = id, Seq = seq, At = At });
        l.Add(QLine.Add(id, "group", new JobDescriptor { Stage = "group", JobId = id }, At)); l.Add(new QLine { T = "done", S = id, Stage = "group", JoinKind = "new", At = At });
        return l;
    }
    static QueueState Replay(IEnumerable<QLine> lines) { var st = new QueueState(); foreach (var l in lines) st.Apply(l); return st; }
    static List<QLine> Lines() => [.. Upload("a", 1, 2), .. Upload("b", 2, null), .. Upload("c", 3, 2)];
    static QueueState RoundTrip(QueueState st) => QueueState.FromSnapshot(st.ToSnapshot(1, 0, 1, "2026-11-23T08:00:00Z", new Clock().T)).State;

    [Fact]
    public void Done_download_process_group_and_step_versions_survive_a_snapshot_round_trip()
    {
        var back = RoundTrip(Replay(Lines()));
        foreach (var id in new[] { "a", "b", "c" })
        {
            var s = back.Get(id)!;
            Assert.All(new[] { "download", "process", "group" }, k => Assert.Equal("done", s.Entries[k].State));
            Assert.Equal(NodeEngine.StandardSteps.OrderBy(x => x), s.Entries["process"].StepsDone!.OrderBy(x => x));
            Assert.True(s.IsFull); Assert.Equal("new", s.JoinKind);
        }
        Assert.Equal(2, back.Get("a")!.VerOf("cast")); Assert.Equal(1, back.Get("b")!.VerOf("cast")); Assert.Equal(2, back.Get("c")!.Seq == 3 ? 2 : 0);
    }

    [Fact]
    public void A_node_loading_the_snapshot_gives_the_same_whatif_as_a_full_replay()
    {
        var full = Replay(Lines()); var snap = RoundTrip(full);
        var ctl = new ControlFile(); var now = Protocol.Utc(new Clock().T);
        foreach (var stage in new[] { "process", "group", "actors" })
        {
            var x = WhatIf.Run(full, stage, ctl, now); var y = WhatIf.Run(snap, stage, ctl, now);
            Assert.Equal(x.Total, y.Total); Assert.Equal(x.Ready, y.Ready); Assert.Equal(x.Blockers.OrderBy(k => k.Key), y.Blockers.OrderBy(k => k.Key));
        }
    }

    [Fact]
    public async Task Rebuild_snapshot_restores_entries_the_old_compaction_dropped_and_refuses_a_changed_snapshot()
    {
        var c = new Clock(); var s = new FakeStore { Clock = c.Now };
        var q = new JobQueue(s, P, "n", c.Now); await q.LoadAsync(default);
        await q.AppendLinesAsync(Lines(), default);
        await q.RollOverAsync(default);   // writes the (fixed) snapshot; segment 1 is sealed
        // simulate the old compaction: a snapshot where every finished subject is only its download entry
        var snap = await s.GetAsync(P.QueueSnapshotGz, default);
        var (st, doc) = QueueState.FromSnapshot(snap!.Data);
        foreach (var sub in st.Subjects.Values) foreach (var k in sub.Entries.Keys.Where(k => k != "download").ToList()) sub.Entries.Remove(k);
        Assert.True(await s.PutIfMatchAsync(P.QueueSnapshotGz, QueueState.Pack(st.ToSnapshot(doc.Seg, doc.Off, doc.Blocks, "2026-11-23T08:00:00Z", c.T)), snap.ETag, default));

        var dry = await QueueRebuild.RunAsync(s, P, false, default);
        Assert.True(dry.SegmentsOk); Assert.False(dry.Written); Assert.Equal(3, dry.SubjectsRestored); Assert.Equal(6, dry.EntriesRestored);
        Assert.Equal(1, QueueState.FromSnapshot((await s.GetAsync(P.QueueSnapshotGz, default))!.Data).State.Get("a")!.Entries.Count);   // dry run wrote nothing

        var real = await QueueRebuild.RunAsync(s, P, true, default);
        Assert.True(real.Written);
        var after = QueueState.FromSnapshot((await s.GetAsync(P.QueueSnapshotGz, default))!.Data).State;
        Assert.All(new[] { "a", "b", "c" }, id => { Assert.Equal("done", after.Get(id)!.Entries["process"].State); Assert.Equal("done", after.Get(id)!.Entries["group"].State); });
        Assert.Equal(2, after.Get("a")!.VerOf("cast"));
        // an unconditional change between read and write is refused: simulate by a stale ETag
        Assert.False(await s.PutIfMatchAsync(P.QueueSnapshotGz, [1], "\"stale\"", default));
    }
}
