using System.Diagnostics;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace IndexNode;

/// <summary>
/// The query service (`indexnode serve`): the pre-check analysis and the screenshot search, warm. POST /v1/precheck takes multipart (meta JSON, frames JPEGs in time order, audio clip, optional excludeSelf)
/// and answers with the precheck.json body; POST /v1/search takes an image or a vector and answers with the best frames; GET /v1/health. Reads the store (search index, member blocks, downloaded.json of a match), never writes to it.
/// </summary>
public sealed class PreCheckServer(PreCheckAnalyzer analyzer, SearchIndexCache index, IStore store, Paths paths, BlockCache blocks, PreCheckSettings settings, int threads, string version, int concurrency = 2)
{
    public const int MaxFrames = 200;
    readonly SemaphoreSlim slots = new(Math.Max(1, concurrency));
    readonly DateTimeOffset started = DateTimeOffset.UtcNow;
    int indexVersion = -1; long requests, failures, searches;

    /// <summary>The index through the 5-minute conditional re-check; the block cache is dropped the moment a new version shows up.</summary>
    public async Task<SearchData?> IndexAsync(CancellationToken ct)
    {
        var idx = await index.GetAsync(store, paths, ct);
        NoteVersion(idx?.Version ?? 0);
        return idx;
    }
    void NoteVersion(int v) { var old = Interlocked.Exchange(ref indexVersion, v); if (old != -1 && old != v) { blocks.Clear(); Log.Info($"serve: search index v{old} -> v{v}, block cache cleared"); } }

    /// <summary>Re-checks the index every <paramref name="every"/> until cancelled (so a new version is swapped in without waiting for a request).</summary>
    public async Task RefreshLoopAsync(TimeSpan every, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(every, ct); await IndexAsync(ct); }
            catch (OperationCanceledException) { return; }
            catch (Exception e) { Log.Info("serve: index refresh failed: " + e.Message); }
        }
    }

    /// <summary>0.13.21 (POST /v1/check): the bearer token (serve.token / INDEXNODE_SERVE_TOKEN); null or empty: /v1/check answers 401 to everyone.</summary>
    public string? Token { get; init; }
    public Tools? Tools { get; init; }
    /// <summary>The analyzer /v1/check runs (default: the server's own); tests put a fake one.</summary>
    public IPreCheckAnalyzer? CheckAnalyzer { get; init; }
    PreCheckStage? checkStage; long checks;

    bool TokenOk(HttpContext c)
    {
        var t = Token; if (string.IsNullOrEmpty(t)) return false;
        var h = c.Request.Headers.Authorization.ToString();
        if (!h.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return false;
        var a = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(h[7..].Trim())); var b = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(t));
        return System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(a, b);   // constant time (both hashed to the same length)
    }

    /// <summary>The inline sample check: the sample files the sample stage uploaded (index/videos/&lt;id&gt;/sample/) read from the store, the same two-segment decision as the check stage, the full record back. WRITES NOTHING (the caller holds the lease and writes video.json and the queue lines). Runs in a step scope of the storage breaker; the answer carries its op counts.</summary>
    public async Task<JsonObject> CheckAsync(string id, bool excludeSelf, CancellationToken ct)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(id, "^[A-Za-z0-9_-]{6,40}$")) throw new ArgumentException("id is not a video id");
        var sw = Stopwatch.StartNew();
        var meter = StoreMeter.Current;
        if (meter.Tripped) throw new StorageBudgetExceeded(meter.TripMessage ?? "the storage breaker is open");
        await slots.WaitAsync(ct);
        try
        {
            using var scope = meter.BeginStep(0, "serve check " + id);
            var stage = checkStage ??= new PreCheckStage(Tools ?? throw new InvalidOperationException("no tools"), CheckAnalyzer ?? analyzer, settings, version);
            analyzer.ExcludeId = excludeSelf ? id : null;
            var job = JobDescriptor.NewDownload("https://www.youtube.com/watch?v=" + id, 360, null); job.JobId = id;
            var ctx = new JobContext { Job = job, Stage = "check", Store = store, Paths = paths, NodeId = "serve", Token = ct, ThreadShare = () => threads, Report = (_, _) => { } };
            var run = await stage.AnalyzeSampleAsync(ctx, null, ct, strict: true);
            var rec = run!.Record;
            var rows = meter.StepTable();
            var ops = new JsonObject { ["total"] = meter.StepOps, ["failed"] = rows.Sum(r => r.Failed), ["top"] = new JsonArray(rows.OrderByDescending(r => r.Count).Take(5).Select(r => (JsonNode?)new JsonObject { ["site"] = r.Site, ["op"] = r.Op, ["count"] = r.Count, ["failed"] = r.Failed }).ToArray()) };
            Interlocked.Increment(ref checks);
            Log.Info($"serve: check {id} {(string?)rec["decision"]} {sw.Elapsed.TotalSeconds:0.0}s, {meter.StepOps} store ops");
            return new JsonObject { ["decision"] = (string?)rec["decision"], ["record"] = rec, ["ops"] = ops, ["seconds"] = Math.Round(sw.Elapsed.TotalSeconds, 3), ["version"] = version };
        }
        finally { slots.Release(); }
    }

    public JsonObject Health()
    {
        var b = blocks.Stats();
        return new JsonObject
        {
            ["ok"] = true, ["name"] = "indexnode", ["version"] = version, ["pid"] = Environment.ProcessId, ["indexVersion"] = indexVersion < 0 ? null : indexVersion, ["models"] = new JsonArray(analyzer.Loaded.Select(m => (JsonNode?)m).ToArray()),
            ["uptimeSeconds"] = Math.Round((DateTimeOffset.UtcNow - started).TotalSeconds, 1), ["threads"] = threads, ["requests"] = Interlocked.Read(ref requests), ["searches"] = Interlocked.Read(ref searches), ["failures"] = Interlocked.Read(ref failures),
            ["framesCache"] = FramesCache.Current?.Stats(),
            ["workingSetMB"] = Process.GetCurrentProcess().WorkingSet64 / (1024 * 1024),
            ["blockCache"] = new JsonObject { ["entries"] = b.Entries, ["mb"] = b.Bytes / (1024 * 1024), ["maxMb"] = b.MaxBytes / (1024 * 1024), ["hits"] = b.Hits, ["misses"] = b.Misses },
        };
    }

    /// <summary>The whole request: meta + frames (+ audio) in, the precheck.json body out.</summary>
    public async Task<JsonObject> AnalyzeAsync(JsonObject meta, IReadOnlyList<byte[]> frames, byte[]? audio, string audioName, bool excludeSelf, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        string id = (string?)meta["id"] ?? throw new ArgumentException("meta.id is missing");
        double duration = (double?)meta["duration"] ?? 0; var w = meta["window"] as JsonObject;
        var window = ((double?)w?["start"] ?? 0, (double?)w?["end"] ?? duration);
        var info = meta["info"] as JsonObject ?? new JsonObject();
        if (frames.Count == 0) throw new ArgumentException("no frames");
        if (frames.Count > MaxFrames) throw new ArgumentException($"more than {MaxFrames} frames");
        var work = Path.Combine(Path.GetTempPath(), "indexnode-svc-" + Guid.NewGuid().ToString("N")[..10]); Directory.CreateDirectory(work);
        await slots.WaitAsync(ct);
        try
        {
            var secs = new Dictionary<string, double>();
            string fdir = Path.Combine(work, "frames"); Directory.CreateDirectory(fdir);
            for (int i = 0; i < frames.Count; i++) await File.WriteAllBytesAsync(Path.Combine(fdir, $"{i:D6}.jpg"), frames[i], ct);
            string? ap = null;
            if (audio is { Length: > 0 }) { ap = Path.Combine(work, Path.GetFileName(string.IsNullOrEmpty(audioName) ? "clip.wav" : audioName)); await File.WriteAllBytesAsync(ap, audio, ct); }
            secs["queue"] = sw.Elapsed.TotalSeconds;
            await IndexAsync(ct);
            // 0.13.15: meta.segments = [{position,start,end,frames}] in frame order (the client's two segments); without it the frames are one window
            var segInfo = (meta["segments"] as JsonArray)?.OfType<JsonObject>().Select(x => new PreCheckAnalyzer.SegInfo((double?)x["position"] ?? 0.5, (double?)x["start"] ?? 0, (double?)x["end"] ?? 0, (int?)x["frames"] ?? 0)).ToList();
            if (segInfo != null && segInfo.Sum(x => x.Frames) != frames.Count) throw new ArgumentException("meta.segments frames do not add up to the frames sent");
            var a = await analyzer.AnalyzeFramesAsync(fdir, frames.Count, null, segInfo != null ? null : ap, settings, threads, _ => { }, secs, excludeSelf ? id : null, ct, segInfo);
            if (a.IndexVersion > 0) NoteVersion(a.IndexVersion);
            var segs0 = segInfo?.Select(x => new SampleSegment("", x.Start, x.End, x.Position)).ToList();
            a = a with { Group = segs0 is { Count: > 0 } ? PreCheckStage.ProposeGroup(a, segs0, settings) : DramaGroups.Propose(a, window.Item1, window.Item2 - window.Item1, settings) };
            var decision = PreCheck.Decide(a, duration, _ => null, settings);
            if (PreCheck.SpeechNeeded(a, decision) && ap != null) { var sp = await analyzer.SpeechAsync(ap, threads, settings.LanguageSeconds, settings.LanguageMinSeconds, ct); a = a with { Speech = sp }; decision = PreCheck.Decide(a, duration, _ => null, settings); }
            bool ai = a.AiScore >= settings.AiFlag;
            var rec = PreCheck.Record(id, decision, a, info, DownloadedFormats.FromInfo(info), window, duration, settings, "precheck-service", version, ai, (double?)meta["sampleDownloadSeconds"] ?? 0, !PreCheckDecision.IsSkip(decision.Decision));
            rec["matchUpload"] = decision.MatchedMember ?? decision.Match?.Upload; rec["excludedSelfCenters"] = excludeSelf ? a.Excluded : null;
            rec["service"] = new JsonObject { ["version"] = version, ["threads"] = threads, ["framesReceived"] = frames.Count, ["audioBytes"] = audio?.Length ?? 0, ["totalSeconds"] = Math.Round(sw.Elapsed.TotalSeconds, 3) };
            Log.Info($"serve: {id} {decision.Decision} ({frames.Count} frames, {audio?.Length ?? 0} B audio) {sw.Elapsed.TotalSeconds:0.0}s");
            return rec;
        }
        finally { slots.Release(); try { Directory.Delete(work, true); } catch { } }
    }

    /// <summary>The image embedder of /v1/search: the search CLI's own (CentersCommands.EmbedImageDetailAsync); tests replace it.</summary>
    public Func<byte[], bool, CancellationToken, Task<(float[] Vector, BarCrop Crop)>>? ImageEmbedder { get; set; }
    public NodeConfig? ImageConfig { get; init; } public HttpClient? ImageHttp { get; init; }

    /// <summary>The screenshot embedding without a child process: JPEG/PNG decoded in managed code, optional bar trim (<see cref="Bars"/>, the one screenshot is its own second sample), crop + libswscale-bicubic resize (<see cref="Swscale"/>) to short side 288, the loaded int8 SSCD session.</summary>
    async Task<(float[] Vector, BarCrop Crop)> EmbedImageAsync(byte[] image, bool trim, CancellationToken ct)
    {
        var model = await Sscd.EnsureModelAsync(ImageConfig!, ImageHttp!, ct);
        var im = InProcessMedia.DecodeImage(image); var crop = BarCrop.None;
        if (trim)
        {
            var g = new byte[im.W * im.H]; for (int k = 0; k < g.Length; k++) g[k] = (byte)((im.Data[k * 3] * 299 + im.Data[k * 3 + 1] * 587 + im.Data[k * 3 + 2] * 114) / 1000);
            crop = Bars.Detect([g, g], im.W, im.H);
        }
        var (rgb, tw, th) = image.Length > 3 && image[0] == 0xFF && image[1] == 0xD8 ? Sscd.PrepareJpeg(image, crop) : Sscd.Prepare(im, crop);
        return (Sscd.EmbedRgb(OrtOptions.Shared(model, Math.Max(1, threads)), rgb, tw, th), crop);
    }

    /// <summary>The screenshot query: an image (SSCD with the CLI's preprocessing, optional bar trim) or a ready vector, then the CLI's lookup (CenterSearch.QueryAsync). Writes nothing.</summary>
    public async Task<JsonObject> SearchAsync(byte[]? image, string imageName, byte[]? vector, bool trim, int topCenters, int top, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew(); double embedMs = 0; BarCrop crop = BarCrop.None; float[] q; string kind;
        if (vector is { Length: > 0 }) { q = CentersCommands.ReadVector(vector); kind = "vector"; }
        else if (image is { Length: > 0 })
        {
            kind = "image";
            await slots.WaitAsync(ct);
            try
            {
                var es = Stopwatch.StartNew();
                (q, crop) = ImageEmbedder != null ? await ImageEmbedder(image, trim, ct) : await EmbedImageAsync(image, trim, ct);
                embedMs = es.Elapsed.TotalMilliseconds;
            }
            catch (Exception e) when (e is InvalidOperationException or InvalidDataException) { throw new ArgumentException("the image could not be read: " + e.Message); }
            finally { slots.Release(); }
        }
        else throw new ArgumentException("send an image (multipart 'image', JPEG/PNG) or a vector (multipart 'vector', 512 f32/f16)");
        var idx = await IndexAsync(ct) ?? throw new InvalidOperationException("no search index");
        var (hits, tm) = await CenterSearch.QueryAsync(store, paths, idx, q, topCenters, top, threads, ct);
        Interlocked.Increment(ref searches);
        return new JsonObject
        {
            ["indexVersion"] = idx.Version,
            ["query"] = new JsonObject { ["kind"] = kind, ["trim"] = trim, ["crop"] = trim && kind == "image" ? new JsonObject { ["top"] = crop.Top, ["bottom"] = crop.Bottom, ["left"] = crop.Left, ["right"] = crop.Right } : null, ["topCenters"] = topCenters, ["top"] = top },
            ["hits"] = new JsonArray(hits.Select(h => (JsonNode?)new JsonObject { ["upload"] = h.Upload, ["time"] = Math.Round(h.Time, 2), ["timestamp"] = Clip.Stamp(h.Time), ["score"] = Math.Round(h.Score, 4), ["row"] = h.Row, ["center"] = h.Center }).ToArray()),
            ["timings"] = new JsonObject { ["embedMs"] = Math.Round(embedMs, 1), ["scanMs"] = Math.Round(tm.ScanMs, 1), ["readMs"] = Math.Round(tm.ReadMs, 1), ["scoreMs"] = Math.Round(tm.ScoreMs, 1), ["uploads"] = tm.Uploads, ["requests"] = tm.Requests, ["bytes"] = tm.Bytes, ["totalMs"] = Math.Round(sw.Elapsed.TotalMilliseconds, 1) },
        };
    }

    public WebApplication Build(string url, long maxBody = 64L * 1024 * 1024)
    {
        var b = WebApplication.CreateSlimBuilder();
        b.Logging.ClearProviders();
        b.WebHost.UseUrls(url);
        b.WebHost.ConfigureKestrel(k => { k.Limits.MaxRequestBodySize = maxBody; k.AddServerHeader = false; });
        b.Services.Configure<FormOptions>(o => { o.MultipartBodyLengthLimit = maxBody; o.ValueCountLimit = MaxFrames + 20; o.MemoryBufferThreshold = 8 * 1024 * 1024; });
        var app = b.Build();
        app.Use(async (c, next) => { c.Response.Headers.Server = "indexnode"; await next(); });
        app.MapGet("/v1/health", () => Results.Json(Health()));
        app.MapPost("/v1/precheck", async (HttpContext c) =>
        {
            Interlocked.Increment(ref requests);
            try
            {
                if (!c.Request.HasFormContentType) return Results.BadRequest(new { error = "multipart/form-data expected" });
                var form = await c.Request.ReadFormAsync(c.RequestAborted);
                var meta = JsonNode.Parse(form["meta"].ToString()) as JsonObject ?? throw new ArgumentException("meta is not a JSON object");
                var frames = new List<byte[]>();
                foreach (var f in form.Files.Where(f => f.Name == "frames")) { using var ms = new MemoryStream(); await f.CopyToAsync(ms, c.RequestAborted); frames.Add(ms.ToArray()); }
                byte[]? audio = null; string an = "clip.wav";
                if (form.Files.GetFile("audio") is { } af) { using var ms = new MemoryStream(); await af.CopyToAsync(ms, c.RequestAborted); audio = ms.ToArray(); an = af.FileName; }
                bool ex = string.Equals(form["excludeSelf"].ToString(), "true", StringComparison.OrdinalIgnoreCase);
                var rec = await AnalyzeAsync(meta, frames, audio, an, ex, c.RequestAborted);
                return Results.Text(rec.ToJsonString(), "application/json");
            }
            catch (Exception e) when (e is ArgumentException or JsonException or InvalidDataException or BadHttpRequestException) { Interlocked.Increment(ref failures); return Results.BadRequest(new { error = e.Message }); }
            catch (OperationCanceledException) { return Results.StatusCode(499); }
            catch (Exception e) { Interlocked.Increment(ref failures); Log.Info("serve: request failed: " + Redact.Sas(e.ToString())); return Results.Json(new { error = Redact.Sas(e.Message) }, statusCode: 500); }
        });
        app.MapPost("/v1/check", async (HttpContext c) =>
        {
            Interlocked.Increment(ref requests);
            if (!TokenOk(c)) return Results.Json(new { error = "unauthorized" }, statusCode: 401);
            try
            {
                using var sr = new StreamReader(c.Request.Body); var body = JsonNode.Parse(await sr.ReadToEndAsync(c.RequestAborted)) as JsonObject ?? throw new ArgumentException("the body is not a JSON object");
                var id = (string?)body["id"] ?? throw new ArgumentException("id is missing");
                var res = await CheckAsync(id, (bool?)body["excludeSelf"] == true, c.RequestAborted);
                return Results.Text(res.ToJsonString(), "application/json");
            }
            catch (Exception e) when (e is ArgumentException or JsonException or InvalidDataException or BadHttpRequestException or FormatException) { Interlocked.Increment(ref failures); return Results.BadRequest(new { error = e.Message }); }
            catch (OperationCanceledException) { return Results.StatusCode(499); }
            catch (StorageBudgetExceeded e) { Interlocked.Increment(ref failures); return Results.Json(new { error = Redact.Sas(e.Message) }, statusCode: 503); }
            catch (InvalidOperationException e) when (e.Message.StartsWith("pre-check could not run", StringComparison.Ordinal)) { Interlocked.Increment(ref failures); return Results.Json(new { error = Redact.Sas(e.Message) }, statusCode: e.Message.Contains("no sample.json", StringComparison.Ordinal) ? 404 : 422); }
            catch (Exception e) { Interlocked.Increment(ref failures); Log.Info("serve: check failed: " + Redact.Sas(e.ToString())); return Results.Json(new { error = Redact.Sas(e.Message) }, statusCode: 500); }
        });
        app.MapPost("/v1/search", async (HttpContext c) =>
        {
            Interlocked.Increment(ref requests);
            try
            {
                if (!c.Request.HasFormContentType) return Results.BadRequest(new { error = "multipart/form-data expected" });
                var form = await c.Request.ReadFormAsync(c.RequestAborted);
                async Task<byte[]?> Part(string n) { if (form.Files.GetFile(n) is not { } f) return null; using var ms = new MemoryStream(); await f.CopyToAsync(ms, c.RequestAborted); return ms.ToArray(); }
                string Field(string n) => form.TryGetValue(n, out var v) && v.ToString().Length > 0 ? v.ToString() : c.Request.Query[n].ToString();
                int Int(string n, int d, int max) => Field(n) is { Length: > 0 } s ? Math.Clamp(int.Parse(s, System.Globalization.CultureInfo.InvariantCulture), 1, max) : d;
                bool trim = Field("trim") is var tv && (tv.Equals("true", StringComparison.OrdinalIgnoreCase) || tv == "1");
                var res = await SearchAsync(await Part("image"), form.Files.GetFile("image")?.FileName ?? "", await Part("vector"), trim, Int("topCenters", 300, 5000), Int("top", 20, 200), c.RequestAborted);
                return Results.Text(res.ToJsonString(), "application/json");
            }
            catch (Exception e) when (e is ArgumentException or FormatException or OverflowException or InvalidDataException or BadHttpRequestException) { Interlocked.Increment(ref failures); return Results.BadRequest(new { error = e.Message }); }
            catch (OperationCanceledException) { return Results.StatusCode(499); }
            catch (Exception e) { Interlocked.Increment(ref failures); Log.Info("serve: search failed: " + Redact.Sas(e.ToString())); return Results.Json(new { error = Redact.Sas(e.Message) }, statusCode: 500); }
        });
        return app;
    }
}

/// <summary>`indexnode serve [--port 8080] [--bind 0.0.0.0] [--threads N] [--block-cache-mb 512] [--concurrency 2] [--input-local dir]`.</summary>
public static class ServeCommand
{
    public static async Task<int> RunAsync(Args a)
    {
        var cfg = NodeConfig.Load(); var http = Commands.NewHttp(); var paths = new Paths(a.Get("prefix") ?? "index/");
        string? inL = a.Get("input-local") ?? a.Get("local");
        IStore store = inL != null ? new LocalStore(Path.GetFullPath(inL)) : !string.IsNullOrEmpty(cfg.Sas) ? new RestStore(http, cfg.Sas) : throw new InvalidOperationException("no SAS: set INDEXNODE_SAS_URL (a read-only container SAS) or use --input-local <dir>");
        store = StageZipStore.Wrap(store, null);
        if (inL != null) store = new MeteredStore(store);   // a RestStore meters itself; a local folder is counted here, so /v1/check reports its ops either way
        int threads = Math.Max(1, a.Get("threads") is { } t ? int.Parse(t) : cfg.EffectiveThreads); int port = a.Get("port") is { } p ? int.Parse(p) : 8080;
        var st = cfg.PreCheck ?? new();
        var tools = new Tools(http, cfg);
        var cache = new SearchIndexCache(Path.Combine(NodeConfig.ConfigDir(), "search-cache"), TimeSpan.FromMinutes(5));
        var blocks = new BlockCache((a.Get("block-cache-mb") is { } m ? long.Parse(m) : 512) * 1024 * 1024);
        var analyzer = new PreCheckAnalyzer(tools, cfg, http, store, paths, cache) { Blocks = blocks, InProcess = true };
        var server = new PreCheckServer(analyzer, cache, store, paths, blocks, st, threads, Commands.VersionString, a.Get("concurrency") is { } c ? int.Parse(c) : 2) { ImageConfig = cfg, ImageHttp = http, Tools = tools, Token = Environment.GetEnvironmentVariable("INDEXNODE_SERVE_TOKEN") is { Length: > 0 } et ? et.Trim() : cfg.Serve?.Token };
        if (string.IsNullOrEmpty(server.Token)) Console.WriteLine("serve: no serve.token / INDEXNODE_SERVE_TOKEN: POST /v1/check answers 401 to everyone");
        using var cts = new CancellationTokenSource();
        // 0.13.21: SIGINT and SIGTERM stop serve gracefully; if the stop has not finished in 8 s the process exits anyway (a hung shutdown must not keep the port or the old binary alive)
        void Stop(string why) { if (cts.IsCancellationRequested) return; Console.WriteLine($"serve: {why}: stopping"); cts.Cancel(); _ = Task.Run(async () => { await Task.Delay(TimeSpan.FromSeconds(8)); Console.WriteLine("serve: shutdown did not finish in 8 s: exiting"); Environment.Exit(1); }); }
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; Stop("SIGINT"); };
        using var sigterm = System.Runtime.InteropServices.PosixSignalRegistration.Create(System.Runtime.InteropServices.PosixSignal.SIGTERM, c => { c.Cancel = true; Stop("SIGTERM"); });
        Console.WriteLine($"serve: loading models and the search index ({threads} threads)...");
        var sw = Stopwatch.StartNew();
        await analyzer.WarmAsync(threads, cts.Token);
        Console.WriteLine($"serve: warm in {sw.Elapsed.TotalSeconds:0.0}s: models [{string.Join(", ", analyzer.Loaded)}], index v{(await server.IndexAsync(cts.Token))?.Version.ToString() ?? "none"}");
        try { await FramesListing.For(store).EnsureAsync(store, paths, cts.Token); } catch (Exception e) when (e is not OperationCanceledException) { Console.WriteLine("serve: frames listing not loaded: " + e.Message); }   // warm: the check's lookup needs no LIST of its own
        var bind = a.Get("bind") ?? "0.0.0.0";
        var app = server.Build($"http://{bind}:{port}");
        _ = server.RefreshLoopAsync(TimeSpan.FromMinutes(5), cts.Token);
        await app.StartAsync(cts.Token);
        Console.WriteLine($"serve: listening on {string.Join(", ", app.Urls)}  (POST /v1/check [bearer token], POST /v1/precheck, POST /v1/search, GET /v1/health)");
        try { await Task.Delay(Timeout.Infinite, cts.Token); } catch (OperationCanceledException) { }
        using (var stopTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5))) { try { await app.StopAsync(stopTimeout.Token); } catch (OperationCanceledException) { } }
        return 0;
    }
}
