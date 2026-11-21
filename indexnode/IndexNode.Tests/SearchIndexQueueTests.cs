using System.Text;

namespace IndexNode.Tests;

/// <summary>0.13.1: the search index is rebuilt as we go: the singleton queue subject, its follow-up add, the If-Match head move, and the pre-check's brute force over fresh centers.</summary>
public class SearchIndexQueueTests : IDisposable
{
    static readonly Paths P = new("index/");
    const int D = CentersFile.Dim;
    readonly string root = Path.Combine(Path.GetTempPath(), "sq-" + Guid.NewGuid().ToString("N")[..8]);
    public SearchIndexQueueTests() => Directory.CreateDirectory(root);
    public void Dispose() { try { Directory.Delete(root, true); } catch { } }
    sealed class Fn(Func<JobContext, Task> f) : IStageHandler { public Task RunAsync(JobContext ctx) => f(ctx); }
    static readonly DateTimeOffset T0 = DateTimeOffset.Parse("2026-11-20T08:00:00Z");

    static NodeEngine Engine(FakeStore s, bool catchUp, List<string> built, Func<JobContext, Task>? step = null)
    {
        var h = new Dictionary<string, IStageHandler>();
        foreach (var st in new[] { "download", "refresh", "classify", "availability" }) h[st] = new Fn(_ => Task.CompletedTask);
        foreach (var st in Protocol.StepStages) h[st] = new Fn(step ?? (_ => Task.CompletedTask));
        h["process"] = h["frames"];
        h["search-index"] = new Fn(ctx => { built.Add(ctx.Job.JobId); return Task.CompletedTask; });
        var o = new EngineOptions
        {
            NodeId = "n1", Name = "n1", Stages = ["process"], Workers = 1, Threads = 4, Once = true, SearchIndexCatchUp = catchUp, Now = () => T0,
            PollInterval = TimeSpan.FromMilliseconds(30), RenewInterval = TimeSpan.FromMilliseconds(40), RegistryInterval = TimeSpan.FromMilliseconds(100), ControlInterval = TimeSpan.FromMilliseconds(40),
            TransientBackoff = _ => TimeSpan.Zero, Ops = new OpCounter(),
        };
        return new NodeEngine(s, P, o, h);
    }
    static Task Run(NodeEngine e) => e.RunAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(15));
    static async Task Process(FakeStore s, string id) { await s.AddJob(P, "process", id, Protocol.Utc(T0.AddMinutes(-10))); await s.PutAsync(P.Video(id, "download.zip"), [1, 2, 3], null, null, false, default); }
    static int SearchAdds(FakeStore s) => s.AppendText("index/jobs/queue.log").Split('\n', StringSplitOptions.RemoveEmptyEntries).Count(l => l.Contains("\"t\":\"add\"") && l.Contains("\"s\":\"search-index\""));

    // ---------------------------------------------------------------- 0.13.17: the debounce is pulled forward when nothing else is left

    static async Task Debounced(FakeStore s) { var q = new JobQueue(s, P, "t", () => T0); await q.LoadAsync(default); await q.AppendLinesAsync([SearchIndexJobs.AddLine(q.Read(st => st), T0, SearchIndexJobs.Delay)!], default); }
    static async Task Lines(FakeStore s, params QLine[] l) { var q = new JobQueue(s, P, "t", () => T0); await q.LoadAsync(default); await q.AppendLinesAsync(l, default); }

    [Fact]
    public async Task Only_a_not_yet_due_search_index_entry_is_built_now_and_exit_when_idle_0m_ends_with_a_fresh_index()
    {
        var s = new FakeStore(); await Debounced(s); var built = new List<string>();
        var e = Engine(s, false, built);
        await Run(e);
        Assert.Single(built);
        Assert.Equal("done", s.Replay(P.Prefix).Entry("search-index", "search-index")!.State);

        var s2 = new FakeStore(); await Debounced(s2); var built2 = new List<string>();
        var h = new Dictionary<string, IStageHandler> { ["search-index"] = new Fn(ctx => { built2.Add(ctx.Job.JobId); return Task.CompletedTask; }) };
        var o = new EngineOptions
        {
            NodeId = "n1", Name = "n1", Stages = ["search-index"], Workers = 1, Threads = 2, ExitWhenIdle = TimeSpan.Zero, IdleCheck = TimeSpan.FromMilliseconds(30), Now = () => T0,
            PollInterval = TimeSpan.FromMilliseconds(30), RenewInterval = TimeSpan.FromMilliseconds(40), RegistryInterval = TimeSpan.FromMilliseconds(100), ControlInterval = TimeSpan.FromMilliseconds(40),
            TransientBackoff = _ => TimeSpan.Zero, Ops = new OpCounter(),
        };
        var ne = new NodeEngine(s2, P, o, h);
        await Run(ne);
        Assert.Single(built2); Assert.True(ne.ExitedIdle);
    }

    [Fact]
    public async Task Another_nodes_running_claim_keeps_the_debounce()
    {
        var s = new FakeStore(); await Debounced(s); await Process(s, "v1"); var built = new List<string>();
        var other = new JobQueue(s, P, "other", () => T0); await other.LoadAsync(default);
        var held = await other.TryClaimAsync("v1", st => ["process"], default);   // a live claim: its active file is leased
        Assert.NotNull(held);
        var e = Engine(s, false, built);
        await Run(e);
        Assert.Empty(built);   // v1 is under another node's claim: the debounce stays
    }

    [Fact]
    public async Task Runnable_work_of_a_stage_this_node_runs_keeps_the_debounce_until_it_is_done()
    {
        var s = new FakeStore(); await Debounced(s); await Process(s, "v1");
        var order = new List<string>();
        var h = new Dictionary<string, IStageHandler>();
        foreach (var st in new[] { "download", "refresh", "classify", "availability" }) h[st] = new Fn(_ => Task.CompletedTask);
        foreach (var st in Protocol.StepStages) h[st] = new Fn(_ => { if (!order.Contains("process")) order.Add("process"); return Task.CompletedTask; });
        h["process"] = h["frames"];
        h["search-index"] = new Fn(_ => { order.Add("search-index"); return Task.CompletedTask; });
        var o = new EngineOptions { NodeId = "n1", Name = "n1", Stages = ["process"], Workers = 1, Threads = 4, Once = true, Now = () => T0, PollInterval = TimeSpan.FromMilliseconds(30), RenewInterval = TimeSpan.FromMilliseconds(40), RegistryInterval = TimeSpan.FromMilliseconds(100), ControlInterval = TimeSpan.FromMilliseconds(40), TransientBackoff = _ => TimeSpan.Zero, Ops = new OpCounter() };
        await Run(new NodeEngine(s, P, o, h));
        Assert.Equal("process", order[0]);   // the runnable drama goes first; the build is pulled forward only after it
        Assert.Equal("search-index", order[^1]);
    }

    // ---------------------------------------------------------------- 0.13.17: the start-up catch-up only when the index is behind

    static async Task GroupDone(FakeStore s, string id, DateTimeOffset at)
    {
        await s.AddJob(P, "group", id, Protocol.Utc(at.AddMinutes(-5)));
        var q = new JobQueue(s, P, "t", () => at); await q.LoadAsync(default);
        await q.AppendLinesAsync([new QLine { T = "claim", S = id, Stages = ["group"], By = "x", Epoch = 1, At = Protocol.Utc(at) }, new QLine { T = "done", S = id, Stage = "group", Fin = true, At = Protocol.Utc(at), Epoch = 1 }], default);
    }
    static NodeEngine CatchUpEngine(FakeStore s, DateTimeOffset? headBuiltAt, List<string> built)
    {
        var h = new Dictionary<string, IStageHandler> { ["search-index"] = new Fn(ctx => { built.Add("b"); return Task.CompletedTask; }) };
        var o = new EngineOptions
        {
            NodeId = "n1", Name = "n1", Stages = ["search-index"], Workers = 1, Threads = 2, Once = true, SearchIndexCatchUp = true, HeadBuiltAt = _ => Task.FromResult(headBuiltAt), Now = () => T0,
            PollInterval = TimeSpan.FromMilliseconds(30), RenewInterval = TimeSpan.FromMilliseconds(40), RegistryInterval = TimeSpan.FromMilliseconds(100), ControlInterval = TimeSpan.FromMilliseconds(40),
            TransientBackoff = _ => TimeSpan.Zero, Ops = new OpCounter(),
        };
        return new NodeEngine(s, P, o, h);
    }

    [Fact]
    public async Task A_start_with_an_up_to_date_head_adds_no_catch_up_entry()
    {
        var s = new FakeStore(); await GroupDone(s, "g1", T0.AddMinutes(-30)); var built = new List<string>();
        await Run(CatchUpEngine(s, T0.AddMinutes(-10), built));   // the head was built after the last finished group record
        Assert.Equal(0, SearchAdds(s)); Assert.Empty(built);
    }

    [Fact]
    public async Task A_start_with_one_newer_group_record_adds_one_catch_up_entry()
    {
        var s = new FakeStore(); await GroupDone(s, "g1", T0.AddMinutes(-30)); await GroupDone(s, "g2", T0.AddMinutes(-5)); var built = new List<string>();
        await Run(CatchUpEngine(s, T0.AddMinutes(-10), built));
        Assert.Equal(1, SearchAdds(s)); Assert.Single(built);
    }

    [Fact]
    public async Task A_process_done_that_wrote_centers_adds_search_index_due_in_15_minutes_and_another_add_while_pending_is_a_no_op()
    {
        var s = new FakeStore(); await Process(s, "v1"); await Process(s, "v2"); var built = new List<string>();
        await Run(Engine(s, false, built));
        var q = s.Replay(P.Prefix); var e = q.Entry("search-index", "search-index")!;
        Assert.Equal(1, SearchAdds(s));                 // two dramas finished their centers: ONE add (the second found it pending)
        Assert.Single(built);                            // 0.13.17: nothing else is left, so the debounce is pulled forward
        Assert.Equal(Protocol.Utc(T0), q.Get("v1")!.CentersAt);
    }

    [Fact]
    public void AddLine_is_a_no_op_while_pending_or_being_built_and_adds_again_after_a_finished_build()
    {
        var st = new QueueState();
        var l = SearchIndexJobs.AddLine(st, T0, SearchIndexJobs.Delay)!; st.Apply(l);
        Assert.Null(SearchIndexJobs.AddLine(st, T0.AddMinutes(1), SearchIndexJobs.Delay));
        st.Apply(new QLine { T = "claim", S = "search-index", By = "n1", At = Protocol.Utc(T0.AddMinutes(16)) });
        Assert.Null(SearchIndexJobs.AddLine(st, T0.AddMinutes(16), SearchIndexJobs.Delay));   // claimed and still pending: no second one
        st.Apply(new QLine { T = "done", S = "search-index", Stage = "search-index", At = Protocol.Utc(T0.AddMinutes(17)), Fin = true });
        var again = SearchIndexJobs.AddLine(st, T0.AddMinutes(18), SearchIndexJobs.Delay);
        Assert.NotNull(again); st.Apply(again!);
        var e = st.Entry("search-index", "search-index")!; Assert.Equal("pending", e.State); Assert.Equal(Protocol.Utc(T0.AddMinutes(33)), e.Due);
    }

    [Fact]
    public async Task First_run_with_no_pending_search_index_adds_one_and_any_processor_node_builds_it()
    {
        var s = new FakeStore(); var built = new List<string>();
        await Run(Engine(s, true, built));               // an idle processor node, empty queue
        Assert.Equal(["search-index"], built);
        Assert.Equal("done", s.Replay(P.Prefix).Entry("search-index", "search-index")!.State);
        var s2 = new FakeStore(); await s2.AddJob(P, "search-index", "search-index", Protocol.Utc(T0.AddMinutes(-1))); var b2 = new List<string>();
        await Run(Engine(s2, true, b2));                 // one pending already: no second add
        Assert.Equal(1, SearchAdds(s2)); Assert.Single(b2);
    }

    [Fact]
    public void Centers_done_times_come_from_the_queue_log_and_pick_the_uploads_newer_than_the_index()
    {
        var q = new JobQueue(new FakeStore(), P, "x", () => T0); var st = new QueueState();
        void Done(string id, int min) { st.Apply(new QLine { T = "add", S = id, Stage = "process", At = Protocol.Utc(T0) }); st.Apply(new QLine { T = "done", S = id, Stage = "process", Step = "centers", At = Protocol.Utc(T0.AddMinutes(min)) }); }
        Done("old", 1); Done("new", 30); Done("newer", 40);
        Assert.Equal(["new", "newer"], st.Subjects.Values.Where(x => string.CompareOrdinal(x.CentersAt, Protocol.Utc(T0.AddMinutes(10))) > 0).OrderBy(x => x.CentersAt, StringComparer.Ordinal).Select(x => x.Id).ToArray());
    }

    static SearchData Empty(int v) => new() { Version = v, MeanVersion = 1, Mean = new float[D], Bits = [], RefUpload = [], RefCenter = [], Uploads = [] };

    [Fact]
    public async Task The_head_moves_with_If_Match_and_a_lost_race_leaves_the_winner_and_old_versions_stay()
    {
        var s = new FakeStore();
        await Empty(1).PublishAsync(s, P, default);
        await Empty(2).PublishAsync(s, P, default);
        Assert.Equal(2, SearchData.ParseHead(s["index/search/head.yaml"].Data));
        Assert.True(s.Exists("index/search/v1/uploads.yaml") && s.Exists("index/search/v2/uploads.yaml"));   // never deleted
        // another node publishes v3 between our read of the head and our write: our If-Match fails
        var winner = SearchData.HeadYaml(3, "index/search/v3/");
        s.BeforeIfMatch = n => { s.Blobs[n].Data = winner; s.Blobs[n].Version++; };
        await Assert.ThrowsAsync<InvalidOperationException>(() => Empty(3).PublishAsync(s, P, default));
        Assert.Equal(winner, s["index/search/head.yaml"].Data);
        Assert.True(s.Exists("index/search/v1/uploads.yaml") && s.Exists("index/search/v2/uploads.yaml"));
        Assert.Equal(1, s.ZipConflicts);
    }

    [Fact]
    public void The_index_records_when_it_was_built()
    {
        var d = Empty(5); d.BuiltAt = T0;
        Assert.Equal(T0, SearchData.ParseBuiltAt(d.UploadsYaml()));
        Assert.Null(SearchData.ParseBuiltAt(Empty(5).UploadsYaml()));
    }

    // ------------------------------------------------------------------ pre-check: a fresh duplicate, not in the index yet
    static float[] Gauss(Random r) { var v = new float[D]; for (int j = 0; j < D; j++) v[j] = (float)(Math.Sqrt(-2 * Math.Log(1 - r.NextDouble())) * Math.Cos(2 * Math.PI * r.NextDouble())); Vec.Normalize(v); return v; }
    static float[] Make(int n, int groups, int seed)
    {
        var rng = new Random(seed); var dirs = Enumerable.Range(0, groups).Select(_ => Gauss(rng)).ToArray(); var x = new float[n * D];
        for (int i = 0; i < n; i++) { var d = dirs[i % groups]; var n2 = Gauss(rng); var row = new float[D]; for (int j = 0; j < D; j++) row[j] = d[j] + 0.5f * n2[j]; Vec.Normalize(row); row.CopyTo(x, i * D); }
        return x;
    }
    static byte[] F16(float[] x) { var b = new byte[x.Length * 2]; for (int i = 0; i < x.Length; i++) BitConverter.TryWriteBytes(b.AsSpan(i * 2, 2), (Half)x[i]); return b; }

    static async Task Drama(StageZipStore wr, string id, float[] x)
    {
        int n = x.Length / D; var frames = Enumerable.Range(0, n).Select(i => new ChunkFrame(i, i * 1.0, i * 1.0, [], null, i, 1)).ToList();
        await wr.PutAsync(P.Video(id, "frames/0000.json"), FrameChunks.JpegIndexJson("0000", 0, 360, 640, 85, frames), null, null, false, default);
        await wr.PutAsync(P.Video(id, "frames/manifest.json"), FrameChunks.ManifestJson("frames", 360, 640, 0, n, [("0000", 0, n, (long)n)], null, "mjpeg", "jpeg"), null, null, false, default);
        await wr.PutAsync(P.Video(id, "sscd/0000.f16"), F16(x), null, null, false, default);
        await wr.PutAsync(P.Video(id, "sscd/manifest.json"), Encoding.UTF8.GetBytes($"{{\"v\":1,\"step\":\"sscd\",\"chunks\":[{{\"chunk\":\"0000\",\"start\":0,\"rows\":{n}}}]}}"), null, null, false, default);
        foreach (var st in new[] { "frames", "sscd" }) Assert.True(await StageZipStore.Stage(wr)!.CommitStageAsync(id, st, "n1", false, false, default));
        await VideoJsonSeed.PutAsync(wr, P, id, 1800);   // 0.13.2: the descriptor the download wrote
    }

    [Fact]
    public async Task The_pre_check_finds_a_duplicate_of_something_processed_after_the_index_was_built_and_never_matches_the_subject_itself()
    {
        var local = new LocalStore(root); var wr = (StageZipStore)StageZipStore.Wrap(local, Path.Combine(root, ".indexnode-zips"));
        foreach (var (id, seed) in new[] { ("a1", 41), ("b2", 42) }) await Drama(wr, id, Make(300, 20, seed));
        var fresh = Make(300, 20, 44);
        var old = Console.Out; Console.SetOut(new StringWriter());
        try
        {
            Assert.Equal(0, await CentersCommands.CentersAsync(Args.Parse(["backfill", "--local", root, "--threads", "2"])));
            Assert.Equal(0, await CentersCommands.SearchIndexAsync(Args.Parse(["build", "--local", root, "--threads", "2"])));   // the index has a1, b2
            await Drama(wr, "d4", fresh);                                                            // d4: processed after the build
            Assert.Equal(0, await CentersCommands.CentersAsync(Args.Parse(["backfill", "--local", root, "--threads", "2"])));
        }
        finally { Console.SetOut(old); }
        var store = CentersCommands.OpenStore(Args.Parse(["--local", root])).Store;
        var rows = F16(fresh.AsSpan(40 * D, 20 * D).ToArray());   // the frames of a re-upload of d4
        PreCheckAnalyzer An(Func<DateTimeOffset?, IReadOnlyList<string>>? f) => new(new Tools(new HttpClient(), new NodeConfig()), new NodeConfig(), new HttpClient(), store, P, new SearchIndexCache(Path.Combine(root, "c-" + Guid.NewGuid().ToString("N")[..4]), TimeSpan.FromMinutes(10)))
        { SkipModelCheck = true, FreshUploads = f, EmbedOverride = (dir, count, box, th, prog, ct) => Task.FromResult(rows) };
        var s = new PreCheckSettings(); var fdir = Path.Combine(root, "fr"); Directory.CreateDirectory(fdir);
        var secs = new Dictionary<string, double>();

        var blind = await An(null).AnalyzeFramesAsync(fdir, 20, BarCrop.None, null, s, 2, _ => { }, secs, null, default);   // the index alone: d4 is not in it
        Assert.DoesNotContain(blind.Top, m => m.Upload == "d4"); Assert.Equal(20, blind.NoHit);

        DateTimeOffset? seenBuiltAt = null;
        var hit = await An(b => { seenBuiltAt = b; return ["d4"]; }).AnalyzeFramesAsync(fdir, 20, BarCrop.None, null, s, 2, _ => { }, secs, null, default);
        Assert.NotNull(seenBuiltAt);                                       // the lookup was told when the index was built
        Assert.Equal("d4", hit.Top[0].Upload); Assert.Equal(1.0, hit.Top[0].Share); Assert.True(hit.Top[0].BestCosine > 0.99);
        Assert.InRange(hit.Top[0].BestTime, 40, 59);   // one of the planted d4 frames: found by brute force over its centers.zip

        var self = await An(_ => ["d4"]).AnalyzeFramesAsync(fdir, 20, BarCrop.None, null, s, 2, _ => { }, secs, "d4", default);   // the subject itself is excluded
        Assert.DoesNotContain(self.Top, m => m.Upload == "d4");
    }
}
