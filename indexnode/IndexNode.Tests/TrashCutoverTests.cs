using System.Text;
namespace IndexNode.Tests;

public class TrashCutoverTests
{
    static readonly Paths P = new("index/");
    static async Task<(FakeStore, JobQueue)> Q()
    {
        var s = new FakeStore(); var q = new JobQueue(s, P, "n1"); await q.LoadAsync(default);
        await q.AppendLinesAsync([QLine.Add("v1", "process", new JobDescriptor { Stage = "process", JobId = "v1" }, "2026-11-01T00:00:00Z")], default);
        await q.AppendLinesAsync(state => { var l = new QLine { T = "claim", S = "v1", Stages = ["process"], By = "n1", Epoch = 1, At = "2026-11-01T00:00:00Z" }; return [l]; }, default);
        await q.AppendLinesAsync([new QLine { T = "done", S = "v1", Stage = "process", State = "duplicate", Trash = "download.zip/video", Why = "duplicate of b2", Epoch = 1, Fin = true, At = "2026-11-01T00:00:00Z", Job = new JobDescriptor { Stage = "process", JobId = "v1", DuplicateOf = "b2", DuplicateShare = 0.97 } }], default);
        await s.PutAsync(P.Video("v1", "video.mp4"), [1, 2, 3], null, null, false, default);
        await s.PutAsync(P.Video("v1", "download.zip"), [4, 5], null, null, false, default);
        return (s, q);
    }

    [Fact]
    public async Task Trash_list_shows_the_mark_and_dry_run_writes_nothing()
    {
        var (s, q) = await Q();
        var items = q.Read(Trash.List); Assert.Single(items); Assert.Equal("download.zip/video", items[0].Mark); Assert.Equal("duplicate of b2", items[0].Why);
        var before = s.Blobs.Keys.OrderBy(x => x).ToList(); var blocks = q.Blocks; var off = q.Offset;
        var res = await Trash.EmptyAsync(q, s, P, TimeSpan.FromDays(1), true, default);
        Assert.Single(res); Assert.Equal(before, s.Blobs.Keys.OrderBy(x => x).ToList()); Assert.Equal(off, q.Offset); Assert.Equal(blocks, q.Blocks);
        Assert.Empty(await Trash.EmptyAsync(q, s, P, TimeSpan.FromDays(100000), false, default));   // too young: nothing
    }

    [Fact]
    public async Task Trash_empty_removes_only_the_video_leaves_download_zip_and_appends_deleted()
    {
        var (s, q) = await Q();
        await Trash.EmptyAsync(q, s, P, TimeSpan.FromDays(1), false, default);
        Assert.False(s.Exists(P.Video("v1", "video.mp4"))); Assert.Equal(new byte[] { 4, 5 }, s[P.Video("v1", "download.zip")].Data);
        Assert.NotNull(q.Read(Trash.List)[0].DeletedAt);
        var q2 = new JobQueue(s, P, "n2"); await q2.LoadAsync(default); Assert.NotNull(q2.Read(Trash.List)[0].DeletedAt);   // the log carries it
        Assert.Empty(await Trash.EmptyAsync(q2, s, P, TimeSpan.FromDays(1), false, default));
    }

    [Fact]
    public async Task A_node_refuses_without_a_queue_snapshot_and_the_control_file_is_jobs_control_json()
    {
        var s = new FakeStore();
        var e = await Assert.ThrowsAsync<QueueNotMigratedException>(() => JobQueue.RequireSnapshotAsync(s, P, default));
        Assert.Contains("indexnode jobs migrate --to log", e.Message);
        Assert.Equal("index/jobs/control.json", P.Control); Assert.Equal("index/control.json", P.LegacyControl);
        await s.PutAsync(P.QueueSnapshotGz, Encoding.UTF8.GetBytes("{}"), null, null, false, default);
        await JobQueue.RequireSnapshotAsync(s, P, default);
        Assert.DoesNotContain(s.Blobs.Keys, k => k.Contains("/jobs/download/"));   // never old job blobs
    }

    static async Task Blob(FakeStore s, string stage, string id, string state, int pri = 0, string? due = null, Action<JobDescriptor>? fix = null)
    {
        var j = stage == "download" ? JobDescriptor.NewDownload("https://www.youtube.com/watch?v=" + id.PadRight(11, 'x'), 480, null) : stage == "refresh" ? new JobDescriptor { Stage = "refresh", JobId = id, Kind = "channel", Name = "n", Id = id } : JobDescriptor.NewStep(stage, id);
        j.Stage = stage; j.JobId = id; j.Priority = pri; j.AddedAt = "2026-11-01T00:00:00Z"; fix?.Invoke(j);
        await s.PutAsync(P.Job(stage, id), j.ToBytes(), Tags.Job(stage, state, j.AddedAt, due, pri), null, false, default);
    }

    [Fact]
    public async Task Migration_keeps_state_steps_priorities_counts_per_stage_and_refuses_leased_jobs_and_holds()
    {
        var s = new FakeStore();
        await Blob(s, "download", "d1", "done"); await Blob(s, "download", "d2", "pending", pri: 5, due: "2026-12-01T00:00:00Z"); await Blob(s, "download", "d3", "failed", fix: j => { j.LastError = "gone"; j.Permanent = true; j.Attempts = 3; });
        await Blob(s, "frames", "p1", "done"); await Blob(s, "sscd", "p1", "pending", pri: 7); await Blob(s, "comments", "d1", "pending");
        await Blob(s, "refresh", "ch-1", "pending", fix: j => { j.Schedule = "off"; });
        await s.PutAsync(P.Hold("h1"), [1], null, null, false, default); await s.PutAsync(P.Prefix + "gpu-ready/sscd.p1", [1], null, null, false, default);
        var (st, rep) = await Migration.BuildAsync(s, P, "2026-11-20T00:00:00Z", default);
        Assert.Equal(0, rep.Running);
        Assert.Equal("done", st.Entry("d1", "download")!.State); Assert.Equal("pending", st.Entry("d2", "download")!.State); Assert.Equal(5, st.Entry("d2", "download")!.Priority); Assert.Equal("2026-12-01T00:00:00Z", st.Entry("d2", "download")!.Due);
        var f = st.Entry("d3", "download")!; Assert.Equal("failed", f.State); Assert.Equal(3, f.Attempts); Assert.Equal("gone", f.LastError);
        var p = st.Entry("p1", "process")!; Assert.Equal("pending", p.State); Assert.Equal(7, p.Priority); Assert.Contains("frames", p.StepsDone!); Assert.DoesNotContain("sscd", p.StepsDone!);   // the finished step is not redone
        Assert.Equal("off", st.Entry("ch-1", "refresh")!.Job!.Schedule);
        Assert.Equal(1, rep.Holds); Assert.Equal(1, rep.GpuMarkers);
        var text = string.Join("\n", rep.Lines()); Assert.Contains("blobs   download", text); Assert.Contains("not carried over: holds=1", text); Assert.Contains("comments jobs=1", text);
        Assert.False(s.Exists(P.QueueSnapshotGz));   // BuildAsync writes nothing
        // refusal: a leased job blob, and a leased hold
        s.Blobs[P.Job("download", "d2")].Lease = "L";
        await Assert.ThrowsAsync<InvalidOperationException>(() => Migration.RunAsync(s, P, false, false, "2026-11-20T00:00:00Z", default));
        s.Blobs[P.Job("download", "d2")].Lease = null; s.Blobs[P.Hold("h1")].Lease = "L";
        await Assert.ThrowsAsync<InvalidOperationException>(() => Migration.RunAsync(s, P, false, false, "2026-11-20T00:00:00Z", default));
        Assert.False(s.Exists(P.QueueSnapshotGz));
        s.Blobs[P.Hold("h1")].Lease = null;
        var before = s.Blobs.Keys.Where(k => k.Contains("/jobs/") && !k.Contains("queue")).OrderBy(k => k).ToList();
        await Migration.RunAsync(s, P, false, false, "2026-11-20T00:00:00Z", default);
        Assert.True(s.Exists(P.QueueSnapshotGz)); Assert.Equal(before, s.Blobs.Keys.Where(k => k.Contains("/jobs/") && !k.Contains("queue")).OrderBy(k => k).ToList());   // old blobs untouched, none written
    }

    [Fact]
    public async Task Migration_carries_describe_and_translate_only_when_their_inputs_are_done_and_an_add_for_a_pending_entry_is_a_no_op()
    {
        var s = new FakeStore();
        // a: process still running (cast pending), describe + translate pending -> neither carried; b: process done, describe pending, translate pending -> describe only; c: describe done, translate pending -> translate
        foreach (var id in new[] { "a", "b", "c" }) foreach (var st in new[] { "frames", "sscd", "centers", "thumbs", "transcribe", "ocrsubs", "sync" }) await Blob(s, st, id, "done");
        await Blob(s, "cast", "a", "pending"); await Blob(s, "cast", "b", "done"); await Blob(s, "cast", "c", "done");
        await Blob(s, "describe", "a", "pending"); await Blob(s, "translate", "a", "pending", pri: 4);
        await Blob(s, "describe", "b", "pending", pri: 6); await Blob(s, "translate", "b", "pending");
        await Blob(s, "describe", "c", "done"); await Blob(s, "translate", "c", "pending");
        var (st2, rep) = await Migration.BuildAsync(s, P, "2026-11-20T00:00:00Z", default);
        Assert.Null(st2.Entry("a", "describe")); Assert.Null(st2.Entry("a", "translate")); Assert.Equal("pending", st2.Entry("a", "process")!.State);
        Assert.Equal("pending", st2.Entry("b", "describe")!.State); Assert.Equal(6, st2.Entry("b", "describe")!.Priority); Assert.Null(st2.Entry("b", "translate"));
        Assert.Null(st2.Entry("c", "describe")); Assert.Equal("pending", st2.Entry("c", "translate")!.State);
        Assert.Equal(1, rep.Entries["describe"]["pending"]); Assert.Equal(1, rep.Entries["translate"]["pending"]);
        var text = string.Join("\n", rep.Lines()); Assert.Contains("note: describe entries not carried over", text); Assert.Contains("note: translate entries not carried over", text);
        // an add for a subject+stage that is already pending changes nothing (the follow-up of a step that finishes later)
        var q = new JobQueue(s, P, "n", () => DateTimeOffset.Parse("2026-11-20T12:00:00Z")); await q.LoadAsync(default);
        await Migration.RunAsync(s, P, false, false, "2026-11-20T00:00:00Z", default); q = new JobQueue(s, P, "n", () => DateTimeOffset.Parse("2026-11-20T12:00:00Z")); await q.LoadAsync(default);
        var before = q.Read(x => x.Entry("b", "describe"))!;
        await q.AppendLinesAsync([QLine.Add("b", "describe", JobDescriptor.NewProcess("b"), "2026-11-21T00:00:00Z", 99)], default);
        var after = q.Read(x => x.Entry("b", "describe"))!; Assert.Equal(before.Priority, after.Priority); Assert.Equal(before.Added, after.Added); Assert.Equal("pending", after.State);
    }
}
