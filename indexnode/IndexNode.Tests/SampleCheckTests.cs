using System.Text.Json.Nodes;
using Xunit;

namespace IndexNode.Tests;

/// <summary>0.13.16: sample and check on the queue (duplicate, new, stored video), the sample-first migration, the separate sample budget, the write-free paths, downloader/processor separation.</summary>
[Collection("path-env")]
public class SampleCheckTests
{
    static readonly Paths P = new("index-test/");
    static readonly PreCheckSettings S = new();
    sealed class Fn(Func<JobContext, Task> f) : IStageHandler { public Task RunAsync(JobContext ctx) => f(ctx); }
    static string T(int minute) => $"2026-11-20T08:{minute:D2}:00Z";

    sealed class FakeAnalyzer(Func<PreCheckAnalysis> make) : IPreCheckAnalyzer
    {
        public List<IReadOnlyList<SampleSegment>> Seen = [];
        public Task<string?> UnavailableAsync(PreCheckSettings s, CancellationToken ct) => Task.FromResult<string?>(null);
        public Task<PreCheckAnalysis> AnalyzeAsync(string samplePath, PreCheckSettings s, int threads, Action<string> progress, CancellationToken ct) => throw new NotSupportedException();
        public Task<PreCheckAnalysis> AnalyzeSegmentsAsync(IReadOnlyList<SampleSegment> segments, PreCheckSettings s, int threads, Action<string> progress, CancellationToken ct)
        {
            Seen.Add(segments.Select(x => new SampleSegment(x.Path, x.Start, x.End, x.Position)).ToList());
            foreach (var x in segments) Assert.True(File.Exists(x.Path) && new FileInfo(x.Path).Length > 0, "segment file " + x.Path);
            return Task.FromResult(make());
        }
        public Task<SpeechResult> SpeechAsync(string samplePath, PreCheckSettings s, int threads, CancellationToken ct) => Task.FromResult(new SpeechResult(null, false, 0, 0, 0, null));
    }

    static PreCheckAnalysis An(double n, Dictionary<string, (double S1, double T1, double S2, double T2)> m)
    {
        var w = PreCheck.Windows(n, 300, S.WindowPositions); var seg = new List<PreCheckSegment>();
        for (int k = 0; k < w.Count; k++)
            seg.Add(new PreCheckSegment(w[k].Position, w[k].Start, w[k].End, 30, m.Where(x => (k == 0 ? x.Value.S1 : x.Value.S2) > 0).ToDictionary(x => x.Key, x => new SegmentShare(k == 0 ? x.Value.S1 : x.Value.S2, k == 0 ? x.Value.T1 : x.Value.T2, 28), StringComparer.Ordinal)));
        var ug = m.Keys.ToDictionary(u => u, u => (string?)"g1", StringComparer.Ordinal);
        var copies = m.Keys.Select(u => new GroupCopy(u, "zh", null, n)).ToList();
        return new PreCheckAnalysis(null, 1, 60, 0, [], 0.1, new Dictionary<string, double>()) { Segments = seg, UploadGroups = ug, GroupMembers = m.Count == 0 ? new Dictionary<string, IReadOnlyList<GroupCopy>>() : new Dictionary<string, IReadOnlyList<GroupCopy>> { ["g1"] = copies } };
    }
    static PreCheckAnalysis Dup() => An(6000, new() { ["m1"] = (0.95, 1800, 0.93, 4200) });
    static PreCheckAnalysis New() => An(6000, new());

    static EngineOptions Opts(params string[] stages) => new()
    {
        NodeId = "test-node", Name = "test", Stages = stages, Workers = 1, Threads = 8, Once = true, SearchIndexCatchUp = false,
        PollInterval = TimeSpan.FromMilliseconds(30), RenewInterval = TimeSpan.FromMilliseconds(40), RegistryInterval = TimeSpan.FromMilliseconds(100), ControlInterval = TimeSpan.FromMilliseconds(40),
        BotBackoff = TimeSpan.FromHours(1), TransientBackoff = _ => TimeSpan.Zero, Ops = new OpCounter(),
    };

    /// <summary>Segments, audio and marker as the sample stage uploads them (the fake stands for the YouTube download).</summary>
    static async Task PutSample(IStore s, string id, double duration = 6000)
    {
        var w = PreCheck.Windows(duration, 300, S.WindowPositions);
        for (int k = 0; k < w.Count; k++) await s.PutAsync(P.Video(id, $"sample/sample{k}.mp4"), [1, 2, 3], null, null, false, default);
        await s.PutAsync(P.Video(id, "sample/audio.mka"), [1], null, null, false, default);
        await s.PutAsync(P.Video(id, "sample/info.json"), "{}"u8.ToArray(), null, null, false, default);
        var m = new JsonObject { ["v"] = 1, ["duration"] = duration, ["segments"] = new JsonArray(w.Select((x, k) => (JsonNode)new JsonObject { ["file"] = $"sample{k}.mp4", ["start"] = x.Start, ["end"] = x.End, ["position"] = x.Position }).ToArray()), ["audio"] = "audio.mka" };
        await s.PutAsync(P.Video(id, "sample/sample.json"), System.Text.Encoding.UTF8.GetBytes(m.ToJsonString()), null, null, false, default);
        await VideoJsonSeed.PutAsync(s, P, id, duration);
    }

    sealed class Rig
    {
        public FakeStore Store = new(); public List<string> Ran = []; public int PreChecks; public FakeAnalyzer An = null!; public string[] Stages = [];
        public NodeEngine Engine(params string[] stages)
        {
            var pre = new PreCheckStage(new Tools(new HttpClient()), An, S, "t");
            var h = new Dictionary<string, IStageHandler>
            {
                ["sample"] = new Fn(async ctx => { lock (Ran) Ran.Add("sample:" + ctx.Job.JobId); await PutSample(ctx.Store, ctx.Job.JobId); SampleStage.AddCheck(ctx); }),
                ["check"] = new CheckStage(pre),
                ["download"] = new Fn(async ctx => { lock (Ran) Ran.Add("download:" + ctx.Job.JobId); await ctx.Store.PutAsync(P.Video(ctx.Job.JobId, "download.zip"), [1], null, null, false, default); var p = JobDescriptor.NewProcess(ctx.Job.JobId); p.Priority = ctx.Job.Priority; ctx.FollowUps.Add(QLine.Add(ctx.Job.JobId, "process", p, Protocol.NowUtc())); }),
                ["precheck"] = new Fn(_ => { PreChecks++; return Task.CompletedTask; }),
            };
            foreach (var st in Protocol.StepStages) h[st] = new Fn(async ctx => { lock (Ran) Ran.Add(st + ":" + ctx.Job.JobId); if (st is not ("describe" or "translate" or "sync") && await ctx.Store.HeadAsync(P.Video(ctx.Job.JobId, st + ".zip"), default) == null) await ctx.Store.PutAsync(P.Video(ctx.Job.JobId, st + ".zip"), [1], null, null, false, default); });
            h["process"] = h["frames"];
            return new NodeEngine(Store, P, Opts(stages), h);
        }
        public Task Run(params string[] stages) => Engine(stages).RunAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(30));
        public QueueState Q => Store.Replay(P.Prefix);
    }

    // ------------------------------------------------------------------ the queue flows

    [Fact]
    public async Task Sample_then_check_duplicate_ends_metadata_only_with_no_download_entry()
    {
        var r = new Rig { An = new FakeAnalyzer(Dup) }; await r.Store.AddJob(P, "sample", "dupv", T(0), priority: 7);
        await r.Run("download", "process");
        Assert.Equal(["sample:dupv"], r.Ran);
        var q = r.Q;
        Assert.Equal("done", q.Entry("dupv", "sample")!.State);
        var ck = q.Entry("dupv", "check")!; Assert.Equal("duplicate", ck.State); Assert.Equal("m1", ck.Job!.DuplicateOf); Assert.Equal(7, ck.Priority);
        Assert.Null(q.Entry("dupv", "download")); Assert.Null(q.Entry("dupv", "process")); Assert.Null(ck.Trash);   // nothing to delete: no video was stored
        Assert.False(r.Store.Exists(P.Video("dupv", "download.zip"))); Assert.False(r.Store.Exists(P.Video("dupv", "video.mp4")));
        Assert.Equal("duplicate", (string?)(await VideoJson.RequireAsync(r.Store, P, "dupv", default)).Root["precheck"]!["decision"]);   // the record is in video.json
        Assert.Equal(0, r.PreChecks);
    }

    [Fact]
    public async Task Sample_then_check_new_adds_the_download_at_the_same_priority_then_process_without_a_second_precheck()
    {
        var r = new Rig { An = new FakeAnalyzer(New) }; await r.Store.AddJob(P, "sample", "newv", T(0), priority: 9);
        await r.Run("download", "process");
        Assert.Equal(["sample:newv", "download:newv", .. NodeEngine.StandardSteps.Where(x => x != "sync").Select(x => x + ":newv"), "sync:newv"], r.Ran.Where(x => !x.StartsWith("describe")).ToList());
        var q = r.Q;
        Assert.Equal("done", q.Entry("newv", "check")!.State); Assert.Equal("done", q.Entry("newv", "download")!.State); Assert.Equal(9, q.Entry("newv", "download")!.Priority);
        Assert.Equal("done", q.Entry("newv", "process")!.State);
        Assert.Equal(0, r.PreChecks);   // process did not run the pre-check: the check record is in video.json
        Assert.Single(r.An.Seen);       // and the analyzer ran once, on the sample
    }

    [Fact]
    public async Task An_already_downloaded_drama_is_checked_from_the_stored_video_cut_into_two_segments()
    {
        var (ff, probe) = await RealFfmpegAsync(); if (ff == null) return;
        var r = new Rig { An = new FakeAnalyzer(New) }; var id = "storedv";
        var dir = Path.Combine(Path.GetTempPath(), "sc-" + Guid.NewGuid().ToString("N")[..6]); Directory.CreateDirectory(dir);
        try
        {
            var mp4 = Path.Combine(dir, "v.mp4");
            var mk = await Proc.RunAsync(ff, ["-hide_banner", "-loglevel", "error", "-y", "-f", "lavfi", "-i", "testsrc=s=64x48:r=1", "-t", "700", "-c:v", "mpeg4", "-g", "1", mp4], default);
            Assert.Equal(0, mk.ExitCode);
            await r.Store.PutFileAsync(P.Video(id, "video.mp4"), mp4, "video/mp4", null, default);
            await r.Store.PutAsync(P.Video(id, "download.zip"), [1, 2, 3], null, null, false, default); await VideoJsonSeed.PutAsync(r.Store, P, id, 700);
            await r.Store.AddJob(P, "process", id, T(0), priority: 4);
            var q0 = new JobQueue(r.Store, P, "t"); await q0.LoadAsync(default);
            var rep = await SampleFirst.RunAsync(q0, false, default); Assert.Equal((0, 1), (rep.Downloads, rep.Checks));
            await r.Run("process");
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
        var segs = Assert.Single(r.An.Seen); Assert.Equal(2, segs.Count); Assert.Equal(PreCheck.Windows(700, 300, S.WindowPositions).Select(x => x.Start), segs.Select(x => x.Start));
        Assert.Equal("stored-video", (string?)(await VideoJson.RequireAsync(r.Store, P, id, default)).Root["precheck"]!["source"]);
        Assert.Equal("done", r.Q.Entry(id, "check")!.State); Assert.Equal("done", r.Q.Entry(id, "process")!.State); Assert.Equal(0, r.PreChecks);
    }

    [Fact]
    public async Task A_stored_video_that_the_check_finds_a_copy_ends_its_process_entry_metadata_only_and_marks_the_video()
    {
        var (ff, _) = await RealFfmpegAsync(); if (ff == null) return;
        var r = new Rig { An = new FakeAnalyzer(() => An(700, new() { ["m1"] = (0.95, 100, 0.93, 400) })) }; var id = "storedd";
        var dir = Path.Combine(Path.GetTempPath(), "sc-" + Guid.NewGuid().ToString("N")[..6]); Directory.CreateDirectory(dir);
        try
        {
            var mp4 = Path.Combine(dir, "v.mp4");
            Assert.Equal(0, (await Proc.RunAsync(ff, ["-hide_banner", "-loglevel", "error", "-y", "-f", "lavfi", "-i", "testsrc=s=64x48:r=1", "-t", "700", "-c:v", "mpeg4", "-g", "1", mp4], default)).ExitCode);
            await r.Store.PutFileAsync(P.Video(id, "video.mp4"), mp4, "video/mp4", null, default);
            await r.Store.PutAsync(P.Video(id, "download.zip"), [1, 2, 3], null, null, false, default); await VideoJsonSeed.PutAsync(r.Store, P, id, 700);
            await r.Store.AddJob(P, "process", id, T(0));
            var q0 = new JobQueue(r.Store, P, "t"); await q0.LoadAsync(default); await SampleFirst.RunAsync(q0, false, default);
            await r.Run("process");
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
        Assert.Equal("duplicate", r.Q.Entry(id, "process")!.State); Assert.Equal("duplicate", r.Q.Entry(id, "check")!.State);
        Assert.True(r.Store.Exists(P.Video(id, "video.mp4")));   // never deleted by a node
        Assert.DoesNotContain(r.Ran, x => x.StartsWith("frames"));
    }

    static async Task<(string? Ff, string? Probe)> RealFfmpegAsync()
    {
        async Task<string?> Find(string name)
        {
            foreach (var d in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                var f = Path.Combine(d, OperatingSystem.IsWindows() ? name + ".exe" : name);
                if (!File.Exists(f)) continue;
                try { var x = await Proc.RunAsync(f, ["-version"], default); if (x.ExitCode == 0 && x.Stdout.Contains(" version")) return f; } catch { }
            }
            return null;
        }
        return (await Find("ffmpeg"), await Find("ffprobe"));
    }

    // ------------------------------------------------------------------ the migration

    [Fact]
    public async Task The_sample_first_migration_counts_keeps_priority_and_due_and_is_idempotent()
    {
        var s = new FakeStore();
        await s.AddJob(P, "download", "dl1", T(1), due: "2026-11-21T00:00:00Z", priority: 7);
        await s.AddJob(P, "download", "dl2", T(2), priority: 3);
        await s.AddJob(P, "process", "pr1", T(3), priority: 5);
        var q = new JobQueue(s, P, "t"); await q.LoadAsync(default);
        var before = s.AppendText(P.QueueLog);
        var dry = await SampleFirst.RunAsync(q, true, default);
        Assert.Equal((2, 1, 0, 0, 0), (dry.Downloads, dry.Checks, dry.Claimed, dry.AlreadyDone, dry.Batches)); Assert.Equal(before, s.AppendText(P.QueueLog));   // a dry run writes nothing
        var run = await SampleFirst.RunAsync(q, false, default);
        Assert.Equal((2, 1), (run.Downloads, run.Checks)); Assert.True(run.Batches >= 1);
        var st = s.Replay(P.Prefix);
        Assert.Null(st.Entry("dl1", "download")); var d1 = st.Entry("dl1", "sample")!; Assert.Equal(7, d1.Priority); Assert.Equal("2026-11-21T00:00:00Z", d1.Due); Assert.Equal("pending", d1.State);
        Assert.Equal(3, st.Entry("dl2", "sample")!.Priority);
        var c = st.Entry("pr1", "check")!; Assert.Equal(5, c.Priority); Assert.Equal("pending", st.Entry("pr1", "process")!.State);
        var afterFirst = s.AppendText(P.QueueLog);
        var second = await SampleFirst.RunAsync(q, false, default);   // idempotent: nothing left to do (pr1 still waits on its process entry and is counted as already done)
        Assert.Equal((0, 0, 0, 1, 0), (second.Downloads, second.Checks, second.Claimed, second.AlreadyDone, second.Batches));
        Assert.Equal(afterFirst, s.AppendText(P.QueueLog));   // not one line more
    }

    [Fact]
    public async Task A_big_migration_is_appended_in_batches()
    {
        var s = new FakeStore(); var lines = new List<QLine>();
        for (int i = 0; i < 2300; i++) lines.Add(QLine.Add("v" + i.ToString("D5"), "download", new JobDescriptor { Stage = "download", JobId = "v" + i.ToString("D5"), Url = "u", AddedAt = T(0) }, T(0)));
        await s.AddJob(P, "download", "seed", T(0));
        var q = new JobQueue(s, P, "t"); await q.LoadAsync(default); await q.AppendLinesAsync(lines, default);
        var run = await SampleFirst.RunAsync(q, false, default);
        Assert.Equal(2301, run.Downloads); Assert.True(run.Batches >= 5, run.Batches.ToString());
        var st = s.Replay(P.Prefix); Assert.Equal(2301, st.Subjects.Values.Count(x => x.Entries.ContainsKey("sample") && !x.Entries.ContainsKey("download")));
    }

    // ------------------------------------------------------------------ the budget

    [Fact]
    public void The_sample_budget_is_separate_and_unlimited_by_default()
    {
        var unlimited = new LimitState("n", new NodeLimits { Budget = 1 }, "x", null);
        for (int i = 0; i < 20; i++) { Assert.True(unlimited.TryAcquire("sample")); unlimited.Release("sample", true); }
        Assert.Equal(1, unlimited.Remaining);                  // samples never spent the full-download budget
        Assert.True(unlimited.TryAcquire("check")); unlimited.Release("check", true); Assert.Equal(1, unlimited.Remaining);   // nor did checks
        Assert.True(unlimited.TryAcquire("download")); unlimited.Release("download", true); Assert.Equal(0, unlimited.Remaining);
        Assert.False(unlimited.TryAcquire("download"));
        Assert.True(unlimited.TryAcquire("sample"));          // a spent download budget does not stop samples

        var capped = new LimitState("n", new NodeLimits { SampleBudget = 2 }, "x", null);
        Assert.True(capped.TryAcquire("sample")); capped.Release("sample", true);
        Assert.True(capped.TryAcquire("sample")); capped.Release("sample", true);
        Assert.False(capped.TryAcquire("sample")); Assert.Equal(0, capped.SampleRemaining);
        Assert.True(capped.TryAcquire("download")); capped.Release("download", true);   // downloads have no budget here
    }

    [Fact]
    public void The_sample_budget_survives_a_restart_and_the_control_file_can_override_it()
    {
        var state = Path.Combine(Path.GetTempPath(), "sb-" + Guid.NewGuid().ToString("N")[..6], "state.json");
        try
        {
            var a = new LimitState("n", new NodeLimits { SampleBudget = 3 }, "k1", state); a.TryAcquire("sample"); a.Release("sample", true);
            var b = new LimitState("n", new NodeLimits { SampleBudget = 3 }, "k1", state); Assert.Equal(2, b.SampleRemaining);
            b.Apply(new ControlFile { ChangedAt = "t", Nodes = new() { ["n"] = new NodeLimits { SampleBudget = 10 } } }); Assert.Equal(10, b.SampleRemaining);
        }
        finally { try { Directory.Delete(Path.GetDirectoryName(state)!, true); } catch { } }
    }

    // ------------------------------------------------------------------ control and stage routing

    [Fact]
    public void Control_stages_follow_download_and_process_unless_set()
    {
        var c = new ControlFile { Stages = new() { ["download"] = "drain", ["process"] = "run" } };
        Assert.NotEqual(Level.Run, c.Effective("sample")); Assert.Equal(Level.Run, c.Effective("check"));
        var d = new ControlFile { Stages = new() { ["process"] = "drain", ["check"] = "run" } };
        Assert.Equal(Level.Run, d.Effective("check")); Assert.Equal(Level.Run, d.Effective("sample"));
        Assert.NotEqual(Level.Run, new ControlFile { Stages = new() { ["process"] = "drain" } }.Effective("check"));   // the owner's hold (process=drain) holds the check too until: indexnode control run --stage process
    }

    [Fact]
    public async Task A_downloader_runs_sample_and_a_processor_without_youtube_runs_check()
    {
        var r = new Rig { An = new FakeAnalyzer(New) }; await r.Store.AddJob(P, "sample", "sepv", T(0));
        await r.Run("process");                       // a processor: never a sample (it has no YouTube)
        Assert.Empty(r.Ran); Assert.Equal("pending", r.Q.Entry("sepv", "sample")!.State);
        await r.Run("download");                      // a downloader: the sample, then NOT the check
        Assert.Equal(["sample:sepv"], r.Ran); Assert.Equal("pending", r.Q.Entry("sepv", "check")!.State); Assert.Null(r.Q.Entry("sepv", "download"));
        await r.Run("process");                       // the processor checks (the sample files are in the store: no YouTube), the downloader is asked for the full download
        Assert.Equal("done", r.Q.Entry("sepv", "check")!.State); Assert.Equal("pending", r.Q.Entry("sepv", "download")!.State);
        Assert.Equal(["sample:sepv"], r.Ran);         // no download ran on the processor
    }

    // ------------------------------------------------------------------ write-free paths

    sealed class FakeTools(Func<IReadOnlyList<string>, string, CancellationToken, Task> hook) : ITools
    {
        public List<string> Calls = [];
        public Task<IToolWorkspace> CreateWorkspaceAsync(string name, CancellationToken ct) => Task.FromResult<IToolWorkspace>(new LocalWorkspace(name));
        public async Task<ProcResult> RunAsync(IToolWorkspace ws, string tool, IReadOnlyList<string> args, CancellationToken ct, Action<string>? onStdout = null)
        {
            var dir = ((LocalWorkspace)ws).Dir; Calls.Add(tool + " " + string.Join(' ', args));
            if (tool == "yt-dlp" && args.Contains("--skip-download")) { await File.WriteAllTextAsync(Path.Combine(dir, "video.info.json"), "{\"duration\":6000,\"description\":\"d\",\"title\":\"t\"}", ct); await File.WriteAllBytesAsync(Path.Combine(dir, "video.jpg"), [1], ct); }
            else if (tool == "yt-dlp") { await hook(args, dir, ct); foreach (var st in new[] { "1650", "4050" }) await File.WriteAllBytesAsync(Path.Combine(dir, $"sec-{st}.mp4"), [1, 2, 3], ct); }
            else if (tool == "ffmpeg") await File.WriteAllBytesAsync(Path.Combine(dir, "audio.mka"), [1], ct);
            return new ProcResult(0, "", "");
        }
        public Task<ProcResult> RunYtDlpAsync(IEnumerable<string> args, CancellationToken ct, Action<string>? onStdout = null) => throw new NotSupportedException();
    }

    [Fact]
    public async Task The_sample_command_downloads_two_sections_into_a_folder_prints_the_time_split_and_writes_nothing_else()
    {
        var root = Path.Combine(Path.GetTempPath(), "sc-" + Guid.NewGuid().ToString("N")[..6]); var cfg = Path.Combine(root, "cfg"); Directory.CreateDirectory(cfg);
        var old = Environment.GetEnvironmentVariable("INDEXNODE_CONFIG_DIR"); Environment.SetEnvironmentVariable("INDEXNODE_CONFIG_DIR", cfg);
        try
        {
            var tools = new FakeTools((_, _, _) => Task.CompletedTask); var w = new StringWriter();
            var rc = await SampleCommands.SampleAsync(Args.Parse(["abcdefghijk", "--output-local", Path.Combine(root, "out")]), tools, w);
            Assert.Equal(0, rc); var o = w.ToString(); Assert.Contains("time split", o); Assert.Contains("2 segment(s) 1650-1950s, 4050-4350s", o);
            var yts = tools.Calls.Where(c => c.StartsWith("yt-dlp") && c.Contains("--download-sections")).ToList();
            Assert.Equal(2, yts.Count);   // one call per section, run in parallel
            Assert.Contains(yts, c => c.Contains("*1650-1950")); Assert.Contains(yts, c => c.Contains("*4050-4350"));
            Assert.All(yts, c => Assert.Contains("-f 18/b[height<=360][vcodec!=none][acodec!=none]/bv*[height<=360]+ba/b", c));
            Assert.All(yts, c => Assert.DoesNotContain("res:360", c));
            Assert.Contains("time split: format", o);
            foreach (var f in new[] { "sample0.mp4", "sample1.mp4", "audio.mka", "info.json", "description.txt", "thumb.jpg", "sample.json" }) Assert.True(File.Exists(Path.Combine(root, "out", "abcdefghijk", f)), f);
            Assert.Empty(Directory.GetFileSystemEntries(cfg));   // no config, no queue, no state written
        }
        finally { Environment.SetEnvironmentVariable("INDEXNODE_CONFIG_DIR", old); try { Directory.Delete(root, true); } catch { } }
    }

    [Fact]
    public async Task The_sample_command_stops_at_the_timebox_and_says_where_the_time_went()
    {
        var root = Path.Combine(Path.GetTempPath(), "sc-" + Guid.NewGuid().ToString("N")[..6]); Directory.CreateDirectory(Path.Combine(root, "cfg"));
        var old = Environment.GetEnvironmentVariable("INDEXNODE_CONFIG_DIR"); Environment.SetEnvironmentVariable("INDEXNODE_CONFIG_DIR", Path.Combine(root, "cfg"));
        try
        {
            var tools = new FakeTools(async (_, _, ct) => await Task.Delay(TimeSpan.FromSeconds(30), ct)); var w = new StringWriter();
            var rc = await SampleCommands.SampleAsync(Args.Parse(["abcdefghijk", "--output-local", Path.Combine(root, "out"), "--timebox", "1"]), tools, w);
            Assert.Equal(3, rc); Assert.Contains("STOPPED at the 1s timebox", w.ToString()); Assert.Contains("'download' phase", w.ToString());
            Assert.False(File.Exists(Path.Combine(root, "out", "abcdefghijk", "sample.json")));
        }
        finally { Environment.SetEnvironmentVariable("INDEXNODE_CONFIG_DIR", old); try { Directory.Delete(root, true); } catch { } }
    }

    [Fact]
    public async Task The_check_on_local_files_and_on_the_stored_sample_writes_nothing_to_the_store()
    {
        var s = new FakeStore(); await PutSample(s, "wfree"); var puts = s.Puts.Count; var ops = s.Ops.ToDictionary(x => x.Key, x => x.Value);
        var pre = new PreCheckStage(new Tools(new HttpClient()), new FakeAnalyzer(Dup), S, "t");
        var ctx = new JobContext { Job = new JobDescriptor { JobId = "wfree", Stage = "check" }, Stage = "check", Store = s, Paths = P, NodeId = "n", Token = default, ThreadShare = () => 2, Report = (_, _) => { } };
        var run = await pre.AnalyzeSampleAsync(ctx, null, default, strict: true);
        Assert.Equal("duplicate", run!.Decision.Decision); Assert.Equal("sample", (string?)run.Record["source"]);
        var dir = Path.Combine(Path.GetTempPath(), "sc-" + Guid.NewGuid().ToString("N")[..6]); Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllBytes(Path.Combine(dir, "sample0.mp4"), [1, 2]); File.WriteAllBytes(Path.Combine(dir, "sample1.mp4"), [1, 2]); File.WriteAllBytes(Path.Combine(dir, "audio.mka"), [1]);
            var w = PreCheck.Windows(6000, 300, S.WindowPositions);
            File.WriteAllText(Path.Combine(dir, "sample.json"), new JsonObject { ["duration"] = 6000, ["audio"] = "audio.mka", ["segments"] = new JsonArray(w.Select((x, k) => (JsonNode)new JsonObject { ["file"] = $"sample{k}.mp4", ["start"] = x.Start, ["end"] = x.End, ["position"] = x.Position }).ToArray()) }.ToJsonString());
            var local = await pre.AnalyzeLocalSampleAsync(ctx, dir, null, default);
            Assert.Equal("sample-local", (string?)local!.Record["source"]);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
        Assert.Equal(puts, s.Puts.Count);
        foreach (var k in new[] { "put", "append", "delete", "lease" }) Assert.Equal(ops.GetValueOrDefault(k), s.Ops.GetValueOrDefault(k));
        Assert.Null((await VideoJson.RequireAsync(s, P, "wfree", default)).Root["precheck"]);   // the record is for the claim to write, never the one-item path
    }
}
