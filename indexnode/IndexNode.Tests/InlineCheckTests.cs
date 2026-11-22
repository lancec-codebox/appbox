using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Xunit;

namespace IndexNode.Tests;

/// <summary>0.13.21: POST /v1/check on indexnode serve (token, decision, writes nothing) and the sample stage's inline check (duplicate: no download; new: download added; timeout / 500 / 401: the check entry as before).</summary>
[Collection("path-env")]
public class InlineCheckTests
{
    static readonly Paths P = new("index-test/");
    static readonly PreCheckSettings S = new();
    const string Token = "test-token-0123456789";
    static string T(int minute) => $"2026-11-21T08:{minute:D2}:00Z";

    sealed class FakeAnalyzer(Func<PreCheckAnalysis> make) : IPreCheckAnalyzer
    {
        public int Calls;
        public Task<string?> UnavailableAsync(PreCheckSettings s, CancellationToken ct) => Task.FromResult<string?>(null);
        public Task<PreCheckAnalysis> AnalyzeAsync(string samplePath, PreCheckSettings s, int threads, Action<string> progress, CancellationToken ct) => throw new NotSupportedException();
        public Task<PreCheckAnalysis> AnalyzeSegmentsAsync(IReadOnlyList<SampleSegment> segments, PreCheckSettings s, int threads, Action<string> progress, CancellationToken ct) { Interlocked.Increment(ref Calls); return Task.FromResult(make()); }
        public Task<SpeechResult> SpeechAsync(string samplePath, PreCheckSettings s, int threads, CancellationToken ct) => Task.FromResult(new SpeechResult(null, false, 0, 0, 0, null));
    }

    static PreCheckAnalysis An(Dictionary<string, (double S1, double T1, double S2, double T2)> m)
    {
        double n = 6000; var w = PreCheck.Windows(n, 300, S.WindowPositions); var seg = new List<PreCheckSegment>();
        for (int k = 0; k < w.Count; k++)
            seg.Add(new PreCheckSegment(w[k].Position, w[k].Start, w[k].End, 30, m.Where(x => (k == 0 ? x.Value.S1 : x.Value.S2) > 0).ToDictionary(x => x.Key, x => new SegmentShare(k == 0 ? x.Value.S1 : x.Value.S2, k == 0 ? x.Value.T1 : x.Value.T2, 28), StringComparer.Ordinal)));
        var ug = m.Keys.ToDictionary(u => u, u => (string?)"g1", StringComparer.Ordinal);
        var copies = m.Keys.Select(u => new GroupCopy(u, "zh", null, n)).ToList();
        return new PreCheckAnalysis(null, 1, 60, 0, [], 0.1, new Dictionary<string, double>()) { Segments = seg, UploadGroups = ug, GroupMembers = m.Count == 0 ? new Dictionary<string, IReadOnlyList<GroupCopy>>() : new Dictionary<string, IReadOnlyList<GroupCopy>> { ["g1"] = copies } };
    }
    static PreCheckAnalysis Dup() => An(new() { ["m1"] = (0.95, 1800, 0.93, 4200) });
    static PreCheckAnalysis New() => An(new());

    static async Task PutSample(IStore s, string id, double duration = 6000)
    {
        var w = PreCheck.Windows(duration, 300, S.WindowPositions);
        for (int k = 0; k < w.Count; k++) await s.PutAsync(P.Video(id, $"sample/sample{k}.mp4"), [1, 2, 3], null, null, false, default);
        await s.PutAsync(P.Video(id, "sample/audio.mka"), [1], null, null, false, default);
        await s.PutAsync(P.Video(id, "sample/info.json"), "{}"u8.ToArray(), null, null, false, default);
        var m = new JsonObject { ["v"] = 1, ["duration"] = duration, ["segments"] = new JsonArray(w.Select((x, k) => (JsonNode)new JsonObject { ["file"] = $"sample{k}.mp4", ["start"] = x.Start, ["end"] = x.End, ["position"] = x.Position }).ToArray()), ["audio"] = "audio.mka" };
        await s.PutAsync(P.Video(id, "sample/sample.json"), Encoding.UTF8.GetBytes(m.ToJsonString()), null, null, false, default);
        await VideoJsonSeed.PutAsync(s, P, id, duration);
    }

    sealed class Svc : IAsyncDisposable
    {
        public Microsoft.AspNetCore.Builder.WebApplication App = null!; public string Url = ""; public PreCheckServer Server = null!; public FakeStore Store = new(); public FakeAnalyzer An = null!; public string Dir = "";
        public async ValueTask DisposeAsync() { await App.StopAsync(); await App.DisposeAsync(); try { Directory.Delete(Dir, true); } catch { } }
    }

    static async Task<Svc> StartAsync(Func<PreCheckAnalysis> make, string? token = Token)
    {
        var s = new Svc { An = new FakeAnalyzer(make), Dir = Path.Combine(Path.GetTempPath(), "ic-" + Guid.NewGuid().ToString("N")[..8]) };
        var http = new HttpClient(); var cfg = new NodeConfig(); var tools = new Tools(http, cfg);
        var real = new PreCheckAnalyzer(tools, cfg, http, s.Store, P, new SearchIndexCache(Path.Combine(s.Dir, "cache"), TimeSpan.FromMinutes(5)));
        var server = new PreCheckServer(real, new SearchIndexCache(Path.Combine(s.Dir, "cache"), TimeSpan.FromMinutes(5)), s.Store, P, new BlockCache(1 << 20), S, 2, "0.13.21-test") { Tools = tools, Token = token, CheckAnalyzer = s.An };
        s.Server = server;
        s.App = server.Build("http://127.0.0.1:0"); await s.App.StartAsync(); s.Url = s.App.Urls.First().Replace("[::]", "127.0.0.1");
        return s;
    }

    static List<string> Snap(FakeStore st) => st.Names("").OrderBy(x => x, StringComparer.Ordinal).Select(n => n + ":" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(st[n].Data))).ToList();
    static void Copy(FakeStore from, FakeStore to) { foreach (var n in from.Names("")) { var b = from[n]; to.Blobs[n] = new FakeStore.Blob { Data = b.Data.ToArray(), Tags = new(b.Tags), Meta = new(b.Meta) }; } }

    static async Task<HttpResponseMessage> Post(Svc s, string json, string? bearer)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, s.Url + "/v1/check") { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        if (bearer != null) req.Headers.Authorization = new("Bearer", bearer);
        return await new HttpClient().SendAsync(req);
    }

    [Fact]
    public async Task Check_needs_the_token_and_with_it_answers_the_decision_and_record_and_writes_nothing()
    {
        await using var s = await StartAsync(Dup);
        await PutSample(s.Store, "dupvideo1");
        var before = Snap(s.Store);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Post(s, "{\"id\":\"dupvideo1\"}", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Post(s, "{\"id\":\"dupvideo1\"}", "wrong")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Post(s, "{\"id\":\"dupvideo1\"}", Token + "x")).StatusCode);
        Assert.Equal(0, s.An.Calls);   // no analysis without the token
        Assert.Equal(HttpStatusCode.OK, (await new HttpClient().GetAsync(s.Url + "/v1/health")).StatusCode);   // health stays open
        var ok = await Post(s, "{\"id\":\"dupvideo1\"}", Token); Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        var o = JsonNode.Parse(await ok.Content.ReadAsStringAsync())!.AsObject();
        Assert.Equal("duplicate", (string?)o["decision"]); Assert.Equal("duplicate", (string?)o["record"]!["decision"]); Assert.NotNull(o["ops"]); Assert.Equal(1, s.An.Calls);
        Assert.Equal(before, Snap(s.Store));   // the sandbox wrote NOTHING (no video.json change, no queue line)
        Assert.Null((await VideoJson.RequireAsync(s.Store, P, "dupvideo1", default)).Root["precheck"]);
    }

    [Fact]
    public async Task Check_without_a_configured_token_answers_401_to_everyone_and_a_missing_sample_is_404()
    {
        await using (var s = await StartAsync(Dup, token: null)) Assert.Equal(HttpStatusCode.Unauthorized, (await Post(s, "{\"id\":\"dupvideo1\"}", "anything")).StatusCode);
        await using var t = await StartAsync(Dup);
        Assert.Equal(HttpStatusCode.NotFound, (await Post(t, "{\"id\":\"nosample1\"}", Token)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Post(t, "{\"id\":\"../x\"}", Token)).StatusCode);
    }

    // ----------------------------------------------------------------------------------------- the downloader's inline path
    static EngineOptions Opts(params string[] stages) => new()
    {
        NodeId = "test-node", Name = "test", Stages = stages, Workers = 1, Threads = 8, Once = true, SearchIndexCatchUp = false,
        PollInterval = TimeSpan.FromMilliseconds(30), RenewInterval = TimeSpan.FromMilliseconds(40), RegistryInterval = TimeSpan.FromMilliseconds(100), ControlInterval = TimeSpan.FromMilliseconds(40),
        BotBackoff = TimeSpan.FromHours(1), TransientBackoff = _ => TimeSpan.Zero, Ops = new OpCounter(),
    };
    sealed class Fn(Func<JobContext, Task> f) : IStageHandler { public Task RunAsync(JobContext ctx) => f(ctx); }

    /// <summary>The real sample stage (its files are already stored, so no YouTube) with the given inline client, the real check stage behind it.</summary>
    static async Task<QueueState> RunSample(FakeStore store, string id, InlineCheckClient? inline, FakeAnalyzer an, int priority = 7, params string[] stages)
    {
        await PutSample(store, id);
        await store.AddJob(P, "sample", id, T(0), priority: priority);
        var tools = new Tools(new HttpClient());
        var pre = new PreCheckStage(tools, an, S, "t");
        var h = new Dictionary<string, IStageHandler> { ["sample"] = new SampleStage(tools, store, new DownloadGate(), S) { Inline = inline }, ["check"] = new CheckStage(pre), ["download"] = new Fn(_ => Task.CompletedTask) };
        await new NodeEngine(store, P, Opts(stages.Length > 0 ? stages : ["sample", "check"]), h).RunAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(30));
        return store.Replay(P.Prefix);
    }

    static InlineCheckClient Client(Svc s, double timeout = 30, string? token = Token) => new(new HttpClient(), s.Url, token, TimeSpan.FromSeconds(timeout));

    [Fact]
    public async Task Inline_duplicate_closes_sample_and_check_in_one_go_and_adds_no_download()
    {
        await using var s = await StartAsync(Dup); var node = new FakeStore(); var local = new FakeAnalyzer(New);
        await PutSample(s.Store, "dupvideo1");   // the serve instance reads the sample from ITS store (the same container in production): copy to the node's
        Copy(s.Store, node);
        var q = await RunSample(node, "dupvideo1", Client(s), local);
        Assert.Equal("done", q.Entry("dupvideo1", "sample")!.State);
        var ck = q.Entry("dupvideo1", "check")!; Assert.Equal("duplicate", ck.State); Assert.Equal("m1", ck.Job!.DuplicateOf); Assert.Equal(7, ck.Priority);
        Assert.Null(q.Entry("dupvideo1", "download")); Assert.Null(ck.Trash);
        Assert.Equal("duplicate", (string?)(await VideoJson.RequireAsync(node, P, "dupvideo1", default)).Root["precheck"]!["decision"]);   // the record, written by the lease holder
        Assert.Equal(0, local.Calls);   // the node ran no analysis itself
        Assert.Equal(1, s.An.Calls);
    }

    [Fact]
    public async Task Inline_new_writes_the_record_finishes_the_check_and_adds_the_full_download()
    {
        await using var s = await StartAsync(New); var node = new FakeStore(); var local = new FakeAnalyzer(New);
        await PutSample(s.Store, "newvideo1"); Copy(s.Store, node);
        var q = await RunSample(node, "newvideo1", Client(s), local, priority: 9);
        Assert.Equal("done", q.Entry("newvideo1", "sample")!.State); Assert.Equal("done", q.Entry("newvideo1", "check")!.State);
        var dl = q.Entry("newvideo1", "download")!; Assert.Equal("pending", dl.State); Assert.Equal(9, dl.Priority);
        Assert.NotNull((await VideoJson.RequireAsync(node, P, "newvideo1", default)).Root["precheck"]); Assert.Equal(0, local.Calls);
    }

    sealed class Stub(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> f) : HttpMessageHandler { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct) => f(r, ct); }

    [Theory]
    [InlineData("timeout")] [InlineData("500")] [InlineData("401")] [InlineData("network")]
    public async Task Inline_failure_falls_back_to_the_check_entry_a_processor_runs(string how)
    {
        var http = new HttpClient(new Stub(async (r, ct) => how switch
        {
            "timeout" => await Never(ct),
            "500" => new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = new StringContent("{\"error\":\"boom\"}") },
            "401" => new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent("{\"error\":\"unauthorized\"}") },
            _ => throw new HttpRequestException("no route to host"),
        }));
        var inline = new InlineCheckClient(http, "https://x.invalid", Token, TimeSpan.FromMilliseconds(300));
        var store = new FakeStore(); var local = new FakeAnalyzer(New);
        var q = await RunSample(store, "fbvideo01", inline, local, 7, "sample");   // only the downloader runs: the check entry stays pending for a processor
        Assert.Equal("done", q.Entry("fbvideo01", "sample")!.State);
        var ck = q.Entry("fbvideo01", "check")!; Assert.Equal("pending", ck.State);   // today's path: the check entry waits for a processor
        Assert.Null(q.Entry("fbvideo01", "download")); Assert.Null((await VideoJson.RequireAsync(store, P, "fbvideo01", default)).Root["precheck"]);
    }
    static async Task<HttpResponseMessage> Never(CancellationToken ct) { await Task.Delay(Timeout.Infinite, ct); return null!; }

    [Fact]
    public async Task Inline_call_logs_a_wake_only_for_the_first_call_after_more_than_two_minutes_idle()
    {
        await using var s = await StartAsync(New); await PutSample(s.Store, "wakevideo1");
        var t = DateTimeOffset.Parse("2026-11-21T10:00:00Z"); InlineCheckClient.Now = () => t;
        try
        {
            var c = Client(s);
            var a1 = await c.TryAsync("wakevideo1", default); Assert.True(a1!.Wake);
            t = t.AddSeconds(30); Assert.False((await c.TryAsync("wakevideo1", default))!.Wake);
            t = t.AddMinutes(3); Assert.True((await c.TryAsync("wakevideo1", default))!.Wake);
        }
        finally { InlineCheckClient.Now = () => DateTimeOffset.UtcNow; }
    }

    /// <summary>
    /// The script goes to <c>bash -n</c> (and <c>shellcheck -</c>) on STDIN, LF line endings: no path form (a Windows path, a WSL /mnt path) matters, and nothing passes through Windows command-line quoting (the script has embedded double quotes).
    /// On the Windows runner "bash" is often WSL's System32\bash.exe with no distribution installed (it prints its complaint as UTF-16 on stdout and exits non-zero): a bash is used only when it runs a probe and answers; with none usable the test returns early (nothing to check with), it does not fail.
    /// </summary>
    [Fact]
    public async Task The_bootstrap_script_parses_with_bash_n_and_shellcheck_when_present()
    {
        var f = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../sandbox/bootstrap.sh"));
        Assert.True(File.Exists(f), f);
        var text = File.ReadAllText(f).Replace("\r\n", "\n");
        string? bash = null;
        foreach (var cand in BashCandidates())
        {
            try { var probe = await RunWithStdinAsync(cand, [], "echo bootstrap-probe-ok\n"); if (probe.ExitCode == 0 && probe.Stdout.Contains("bootstrap-probe-ok")) { bash = cand; break; } }
            catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or IOException) { }
        }
        if (bash == null) { Console.WriteLine("bootstrap.sh parse test: no usable bash on this machine, nothing checked"); Assert.True(OperatingSystem.IsWindows(), "no usable bash on a non-Windows machine"); return; }
        var r = await RunWithStdinAsync(bash, ["-n"], text);
        Assert.True(r.ExitCode == 0, $"bash -n ({bash}) exit {r.ExitCode}: {r.Stderr}{r.Stdout}");
        ProcResult? sc = null; try { sc = await RunWithStdinAsync("shellcheck", ["-S", "warning", "-"], text); } catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { }
        if (sc != null) Assert.True(sc.ExitCode == 0, sc.Stdout + sc.Stderr);
    }

    /// <summary>Runs a program with the given text on its stdin (then closes it); 60 s limit.</summary>
    static async Task<ProcResult> RunWithStdinAsync(string file, string[] args, string stdin)
    {
        var psi = new System.Diagnostics.ProcessStartInfo(file) { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = System.Diagnostics.Process.Start(psi)!;
        var o = p.StandardOutput.ReadToEndAsync(); var e = p.StandardError.ReadToEndAsync();
        try { await p.StandardInput.WriteAsync(stdin); p.StandardInput.Close(); } catch (IOException) { }   // the program may exit before reading it all
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        try { await p.WaitForExitAsync(cts.Token); } catch (OperationCanceledException) { try { p.Kill(true); } catch { } throw new TimeoutException(file + " did not finish in 60 s"); }
        return new ProcResult(p.ExitCode, await o, await e);
    }

    /// <summary>Bash programs to try, best first: on Windows Git Bash (not WSL's System32 stub) before whatever "bash" resolves to.</summary>
    static IEnumerable<string> BashCandidates()
    {
        if (OperatingSystem.IsWindows())
        {
            foreach (var env in new[] { "ProgramFiles", "ProgramFiles(x86)", "LocalAppData" })
            {
                var root = Environment.GetEnvironmentVariable(env); if (string.IsNullOrEmpty(root)) continue;
                foreach (var rel in new[] { @"Git\bin\bash.exe", @"Git\usr\bin\bash.exe", @"Programs\Git\bin\bash.exe" }) { var c = Path.Combine(root, rel); if (File.Exists(c)) yield return c; }
            }
        }
        yield return "bash";
    }

    [Fact]
    public void Tools_install_models_leaves_siglip_out_and_models_serve_installs_only_what_serve_loads()
    {
        Assert.DoesNotContain(Models.Default, m => Models.Clip.Contains(m) || m == Models.ClipVisionFp16);
        Assert.Contains(Models.All, m => Models.Clip.Contains(m));   // --all still has it
        Assert.Equal(["sscd-int8", "asr-model", "asr-tokens"], Models.Serve.Select(m => m.Id).ToArray());
        Assert.True(Models.Serve.Sum(m => m.Size) < 300_000_000);
        Assert.Contains(Models.Default, m => m == Models.SscdInt8);
    }

    [Fact]
    public async Task Serve_binds_the_address_it_is_given()
    {
        var s = new Svc { An = new FakeAnalyzer(New), Dir = Path.Combine(Path.GetTempPath(), "ic-" + Guid.NewGuid().ToString("N")[..8]) };
        var http = new HttpClient(); var cfg = new NodeConfig(); var tools = new Tools(http, cfg);
        var cache = new SearchIndexCache(Path.Combine(s.Dir, "cache"), TimeSpan.FromMinutes(5));
        var server = new PreCheckServer(new PreCheckAnalyzer(tools, cfg, http, s.Store, P, cache), cache, s.Store, P, new BlockCache(1 << 20), S, 2, "t") { Tools = tools, Token = Token };
        s.App = server.Build("http://127.0.0.1:0"); await s.App.StartAsync();
        try { Assert.All(s.App.Urls, u => Assert.StartsWith("http://127.0.0.1:", u)); } finally { await s.App.StopAsync(); await s.App.DisposeAsync(); }
        Assert.Contains("--bind", read_usage());
        static string read_usage() => File.ReadAllText(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../IndexNode/Program.cs")));
    }

    // ----------------------------------------------------------------------------------------- 0.13.23: the AI hold in the sample flow
    static PreCheckAnalysis NewAi() => New() with { AiScore = 0.99 };
    static string HeldLog(Action a) { var w = new StringWriter(); var old = Log.Out; Log.Out = w; try { a(); } finally { Log.Out = old; } return w.ToString(); }

    [Fact]
    public async Task Inline_check_of_an_AI_drama_adds_the_download_held_and_a_duplicate_stays_metadata_only()
    {
        await using var s = await StartAsync(NewAi); var node = new FakeStore(); var local = new FakeAnalyzer(New);
        await PutSample(s.Store, "aivideo001"); Copy(s.Store, node);
        var w = new StringWriter(); var old = Log.Out; Log.Out = w; QueueState q;
        try { q = await RunSample(node, "aivideo001", Client(s), local); } finally { Log.Out = old; }
        var d = q.Entry("aivideo001", "download")!; Assert.Equal("ai", d.Hold); Assert.Equal("pending", d.State); Assert.Equal(7, d.Priority); Assert.True(d.HoldAiScore > 0.9);
        Assert.Contains("sample aivideo001: check ", w.ToString()); Assert.Contains("AI (score 0.990): held, aiDramas is off", w.ToString());
        Assert.Equal("done", q.Entry("aivideo001", "check")!.State);
        // not AI: claimable as before
        await using var n = await StartAsync(New); var node2 = new FakeStore(); await PutSample(n.Store, "plainvid01"); Copy(n.Store, node2);
        var q2 = await RunSample(node2, "plainvid01", Client(n), local); Assert.Null(q2.Entry("plainvid01", "download")!.Hold);
        // a duplicate that is AI-flagged stays metadata-only (no download, no hold)
        await using var dd = await StartAsync(() => Dup() with { AiScore = 0.99 }); var node3 = new FakeStore(); await PutSample(dd.Store, "dupai00001"); Copy(dd.Store, node3);
        var q3 = await RunSample(node3, "dupai00001", Client(dd), local); Assert.Equal("duplicate", q3.Entry("dupai00001", "check")!.State); Assert.Null(q3.Entry("dupai00001", "download"));
    }

    [Fact]
    public async Task Check_stage_of_an_AI_drama_adds_the_download_held()
    {
        var node = new FakeStore(); var an = new FakeAnalyzer(NewAi);
        var q = await RunSample(node, "aicheck001", null, an);   // no inline client: the sample adds the check entry, the check stage runs it
        Assert.Equal(1, an.Calls); var d = q.Entry("aicheck001", "download")!; Assert.Equal("ai", d.Hold); Assert.Equal(7, d.Priority);
        var node2 = new FakeStore(); var q2 = await RunSample(node2, "plaincheck1", null, new FakeAnalyzer(New)); Assert.Null(q2.Entry("plaincheck1", "download")!.Hold);
    }

    [Fact]
    public async Task A_held_download_is_not_claimed_until_aiDramas_is_on()
    {
        var store = new FakeStore(); int ran = 0;
        await store.AddJob(P, "download", "heldvideo01", T(0), priority: 5);
        var q0 = new JobQueue(store, P, "t"); await q0.LoadAsync(default);
        await q0.AppendLinesAsync([new QLine { T = "hold", S = "heldvideo01", Stage = "download", Hold = "ai", AiScore = 0.9, At = T(1) }], default);
        var h = new Dictionary<string, IStageHandler> { ["download"] = new Fn(_ => { Interlocked.Increment(ref ran); return Task.CompletedTask; }) };
        await new NodeEngine(store, P, Opts("download"), h).RunAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(0, ran); Assert.Equal("pending", store.Replay(P.Prefix).Entry("heldvideo01", "download")!.State);
        await store.PutAsync(P.Control, Encoding.UTF8.GetBytes("{\"v\":1,\"aiDramas\":\"on\"}"), null, null, false, default, "application/json");
        await new NodeEngine(store, P, Opts("download"), h).RunAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(1, ran);
    }

    [Fact]
    public async Task Jobs_hold_ai_holds_pending_flagged_entries_with_and_without_dry_run_and_is_idempotent()
    {
        var store = new FakeStore();
        foreach (var (id, stage) in new[] { ("aidl000001", "download"), ("aipr000001", "process"), ("plain00001", "download"), ("descr00001", "download"), ("nojson0001", "download") }) await store.AddJob(P, stage, id, T(0), priority: 3);
        async Task Ai(string id, bool flag, string src) => await VideoJson.UpdateAsync(store, P, id, r => VideoJson.SetAi(r, 0.97, flag, src), default);
        await Ai("aidl000001", true, "precheck"); await Ai("aipr000001", true, "precheck"); await Ai("plain00001", false, "precheck"); await Ai("descr00001", true, "describe");
        var q = new JobQueue(store, P, "t"); await q.LoadAsync(default);
        var dry = await AiHold.HoldPendingAsync(q, store, P, true, default);
        Assert.Equal(1, dry.Download); Assert.Equal(1, dry.Process); Assert.Equal(2, dry.Skipped); Assert.Equal(1, dry.Unreadable); Assert.Equal(0, dry.Batches);
        Assert.All(store.Replay(P.Prefix).Subjects.Values.SelectMany(s => s.Entries.Values), e => Assert.Null(e.Hold));   // dry run wrote nothing
        var real = await AiHold.HoldPendingAsync(q, store, P, false, default);
        Assert.Equal(1, real.Download); Assert.Equal(1, real.Process); Assert.Equal(1, real.Batches);
        var st = store.Replay(P.Prefix); Assert.Equal("ai", st.Entry("aidl000001", "download")!.Hold); Assert.Equal("ai", st.Entry("aipr000001", "process")!.Hold); Assert.Equal(3, st.Entry("aidl000001", "download")!.Priority);
        Assert.Null(st.Entry("plain00001", "download")!.Hold); Assert.Null(st.Entry("descr00001", "download")!.Hold);
        var again = await AiHold.HoldPendingAsync(q, store, P, false, default); Assert.Equal(0, again.Download + again.Process); Assert.Equal(0, again.Batches);
    }
}
