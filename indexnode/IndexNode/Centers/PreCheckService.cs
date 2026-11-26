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
    /// <summary>0.13.55: when the published search index this serve looks things up in was built (its own cache: head.yaml is looked at every few minutes).</summary>
    public async Task<DateTimeOffset?> IndexBuiltAtAsync(CancellationToken ct) => (await index.GetAsync(store, paths, ct))?.BuiltAt;

    /// <summary>The index through the 5-minute conditional re-check; the block cache is dropped the moment a new version shows up.</summary>
    public async Task<SearchData?> IndexAsync(CancellationToken ct)
    {
        var idx = await index.GetAsync(store, paths, ct);
        NoteVersion(idx?.Version ?? 0);
        return idx;
    }
    void NoteVersion(int v) { var old = Interlocked.Exchange(ref indexVersion, v); if (old != -1 && old != v) { blocks.Clear(); Log.Info($"serve: search index v{old} -> v{v}, block cache cleared"); } }

    /// <summary>0.13.31 (serve --warm): fetches every frames file of the loaded index into the local cache, whole, one GET each, paced by <paramref name="pacer"/> (one-off at start; files already cached cost nothing). Returns (fetched, alreadyCached, failed).</summary>
    public static async Task<(int Fetched, int Cached, int Failed)> WarmFramesAsync(IStore store, SearchData? idx, OpPacer? pacer, Action<string>? progress, CancellationToken ct)
    {
        var fc = FramesCache.Current;
        if (fc == null || idx == null) return (0, 0, 0);
        int fetched = 0, cached = 0, failed = 0, done = 0;
        foreach (var u in idx.Uploads)
        {
            ct.ThrowIfCancellationRequested();
            if (u.File == null) continue;
            if (File.Exists(fc.PathOf(u.File, u.Sha, u.ZipSize))) { cached++; continue; }
            if (pacer != null) await pacer.WaitAsync(ct);
            try { var (p, f) = await fc.LocalOrFetchAsync(store, u.File, u.Sha, u.ZipSize, ct); if (p != null && f) fetched++; else if (p != null) cached++; else failed++; }
            catch (Exception e) when (e is not OperationCanceledException) { failed++; Log.Info($"serve: warm {u.File}: {e.Message}"); if (StoreMeter.Current.Tripped) { await Task.Delay(TimeSpan.FromSeconds(5), ct); } }
            if (++done % 50 == 0) progress?.Invoke($"serve: warm {fetched} fetched, {cached} cached, {failed} failed of {idx.Uploads.Count}");
        }
        return (fetched, cached, failed);
    }

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
    /// <summary>0.13.58: when set (env INDEXNODE_SEARCH_TOKEN), POST /v1/search needs `Authorization: Bearer <it>`: the site's Worker is the only caller. Not set: /v1/search is open, as before.</summary>
    public string? SearchToken { get; init; }
    /// <summary>0.13.62: frames around a moment and the actor lookup (GET /v1/frames, POST /v1/actors/search, GET /v1/actors/...), for a person's search page. They need the serve token or the search token; with neither set they answer 401.</summary>
    public LookServer? Look { get; init; }
    bool SearchTokenOk(HttpContext c) => !string.IsNullOrEmpty(SearchToken) && c.Request.Headers.Authorization.ToString() is { } auth && auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
        && System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(System.Text.Encoding.UTF8.GetBytes(auth[7..].Trim()), System.Text.Encoding.UTF8.GetBytes(SearchToken));
    public Tools? Tools { get; init; }
    /// <summary>The analyzer /v1/check runs (default: the server's own); tests put a fake one.</summary>
    public IPreCheckAnalyzer? CheckAnalyzer { get; init; }
    PreCheckStage? checkStage; long checks;
    /// <summary>0.13.26: the download service (serve --download-service): set, the two /v1/download endpoints answer; otherwise 404.</summary>
    public DownloadService? Downloads { get; set; }
    /// <summary>Where the downloaders' work queue lives: the container URL (no SAS, no credential) and the path.</summary>
    public string? ContainerUrl { get; set; }

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
            var ops = new JsonObject { ["total"] = meter.StepOps, ["failed"] = rows.Sum(r => r.Failed), ["notModified"] = rows.Sum(r => r.NotModified), ["top"] = new JsonArray(rows.OrderByDescending(r => r.Count).Take(5).Select(r => (JsonNode?)new JsonObject { ["site"] = r.Site, ["op"] = r.Op, ["count"] = r.Count, ["failed"] = r.Failed }).ToArray()) };
            Interlocked.Increment(ref checks);
            Log.Info($"serve: check {id} {(string?)rec["decision"]} {sw.Elapsed.TotalSeconds:0.0}s, {meter.StepOps} store ops");
            return new JsonObject { ["decision"] = (string?)rec["decision"], ["record"] = rec, ["ops"] = ops, ["seconds"] = Math.Round(sw.Elapsed.TotalSeconds, 3), ["version"] = version };
        }
        finally { slots.Release(); }
    }

    /// <summary>0.13.59: false while `serve` is still loading its models and the index. /v1/health then says ready false at once, and every other route answers 503 with Retry-After,
    /// so a caller that woke a scaled-to-zero service can show "waking" and try again instead of waiting on a closed port. True for a server built directly (tests, tools).</summary>
    public volatile bool Ready = true;
    /// <summary>How long the load took, once ready (the cold start a caller waits for, without the container's own start).</summary>
    public double? ReadyAfterSeconds;
    public const int StartingRetryAfterSeconds = 5;

    public JsonObject Health()
    {
        var b = blocks.Stats();
        return new JsonObject
        {
            ["ready"] = Ready, ["readyAfterSeconds"] = ReadyAfterSeconds == null ? null : Math.Round(ReadyAfterSeconds.Value, 1),
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
        // 0.13.46: a WebP screenshot becomes a PNG first (ffmpeg, one child process); JPEG and PNG stay in-process as before
        if (InProcessMedia.IsWebp(image)) image = await InProcessMedia.WebpToPngAsync(await (Tools ?? throw new InvalidOperationException("no tools: WebP needs ffmpeg")).FfmpegAsync(ct), image, ct);
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
    public async Task<JsonObject> SearchAsync(byte[]? image, string imageName, byte[]? vector, bool trim, int topCenters, int top, CancellationToken ct, int maxUploads = -1, bool? ranged = null)
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
        else throw new ArgumentException("send an image (multipart 'image', JPEG/PNG/WebP) or a vector (multipart 'vector', 512 f32/f16)");
        // 0.13.31: a search makes no head.yaml / uploads.yaml / catalog read (the loaded version; the refresh loop moves it) and is ONE step of the storage budget: a warm search is 0 ops, a cold file is one whole GET
        var idx = index.Loaded ?? await IndexAsync(ct) ?? throw new InvalidOperationException("no search index");
        var meter = StoreMeter.Current;
        if (meter.Tripped) throw new StorageBudgetExceeded(meter.TripMessage ?? "the storage breaker is open");
        List<Hit> hits; QueryTimings tm; long ops;
        if (maxUploads < 0) maxUploads = settings.SearchUploads;
        if (topCenters <= 0) topCenters = Math.Clamp((int)Math.Round(idx.Bits.Length / SearchData.CodeBytes * settings.SearchCentersShare), 300, 5000);   // 0.13.62: grows with the index
        using (var scope = meter.BeginStep(meter.Limits.StepLimitFor("search"), null)) { (hits, tm) = await CenterSearch.QueryAsync(store, paths, idx, q, topCenters, top, threads, ct, maxUploads, ranged ?? settings.SearchRanged); ops = meter.StepOps; }
        Interlocked.Increment(ref searches);
        return new JsonObject
        {
            ["indexVersion"] = idx.Version,
            ["query"] = new JsonObject { ["kind"] = kind, ["trim"] = trim, ["crop"] = trim && kind == "image" ? new JsonObject { ["top"] = crop.Top, ["bottom"] = crop.Bottom, ["left"] = crop.Left, ["right"] = crop.Right } : null, ["topCenters"] = topCenters, ["top"] = top, ["maxUploads"] = maxUploads, ["ranged"] = ranged ?? settings.SearchRanged },
            ["hits"] = new JsonArray(hits.Select(h => (JsonNode?)new JsonObject { ["upload"] = h.Upload, ["time"] = Math.Round(h.Time, 2), ["timestamp"] = Clip.Stamp(h.Time), ["score"] = Math.Round(h.Score, 4), ["row"] = h.Row, ["center"] = h.Center, ["voteRank"] = h.Vote, ["voteCenters"] = h.VoteCenters }).ToArray()),
            ["timings"] = new JsonObject { ["embedMs"] = Math.Round(embedMs, 1), ["scanMs"] = Math.Round(tm.ScanMs, 1), ["readMs"] = Math.Round(tm.ReadMs, 1), ["scoreMs"] = Math.Round(tm.ScoreMs, 1), ["uploads"] = tm.Uploads, ["uploadsOpened"] = tm.Opened, ["storeOps"] = ops, ["requests"] = tm.Requests, ["bytes"] = tm.Bytes, ["totalMs"] = Math.Round(sw.Elapsed.TotalMilliseconds, 1) },
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
        app.Use(async (c, next) =>
        {   // 0.13.59: still loading: only /v1/health answers; the rest says when to come back
            if (Ready || c.Request.Path.StartsWithSegments("/v1/health")) { await next(); return; }
            c.Response.StatusCode = 503; c.Response.Headers.RetryAfter = StartingRetryAfterSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
            await c.Response.WriteAsJsonAsync(new { error = "starting: the models and the search index are loading", ready = false, retryAfterSeconds = StartingRetryAfterSeconds });
        });
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
        app.MapGet("/v1/download/queue", (HttpContext c) =>
        {
            if (!TokenOk(c)) return Results.Json(new { error = "unauthorized" }, statusCode: 401);
            if (Downloads == null || ContainerUrl == null) return Results.Json(new { error = "this serve does not run the download service" }, statusCode: 404);
            return Results.Json(new { container = ContainerUrl, path = paths.DownloadWork.QueueDir, max = Downloads.WorkMax });   // no new credential: a downloader keeps its own SAS
        });
        app.MapPost("/v1/download/next", async (HttpContext c) =>
        {
            if (!TokenOk(c)) return Results.Json(new { error = "unauthorized" }, statusCode: 401);
            if (Downloads == null) return Results.Json(new { error = "this serve does not run the download service" }, statusCode: 404);
            try
            {
                using var sr = new StreamReader(c.Request.Body); var body = JsonNode.Parse(await sr.ReadToEndAsync(c.RequestAborted)) as JsonObject ?? throw new ArgumentException("the body is not a JSON object");
                var node = (string?)body["node"] ?? throw new ArgumentException("node is missing");
                var stages = (body["stages"] as JsonArray)?.Select(x => x?.ToString() ?? "").Where(x => x.Length > 0).ToArray() ?? [];
                var got = await Downloads.NextAsync(node, stages, c.RequestAborted);
                return got == null ? Results.Json(new { none = true, retryAfterSeconds = 60 }) : Results.Json(new { subject = got.Subject, stage = got.Stage, epoch = got.Epoch, lease = got.Lease, active = got.Active });
            }
            catch (Exception e) when (e is ArgumentException or JsonException) { return Results.BadRequest(new { error = e.Message }); }
            catch (OperationCanceledException) { return Results.StatusCode(499); }
            catch (StorageBudgetExceeded e) { return Results.Json(new { error = Redact.Sas(e.Message) }, statusCode: 503); }
            catch (Exception e) { Log.Info("serve: download/next failed: " + Redact.Sas(e.ToString())); return Results.Json(new { error = Redact.Sas(e.Message) }, statusCode: 500); }
        });
        app.MapPost("/v1/download/sampled", async (HttpContext c) =>
        {   // 0.13.49: a node just sampled an upload: check it now and hand that node its download
            if (!TokenOk(c)) return Results.Json(new { error = "unauthorized" }, statusCode: 401);
            if (Downloads == null) return Results.Json(new { error = "this serve does not run the download service" }, statusCode: 404);
            try
            {
                using var sr = new StreamReader(c.Request.Body); var body = JsonNode.Parse(await sr.ReadToEndAsync(c.RequestAborted)) as JsonObject ?? throw new ArgumentException("the body is not a JSON object");
                var node = (string?)body["node"] ?? throw new ArgumentException("node is missing"); var id = (string?)body["id"] ?? throw new ArgumentException("id is missing");
                var got = await Downloads.SampledAsync(node, id, c.RequestAborted);
                return got.Claim is { } k ? Results.Json(new { subject = k.Subject, stage = k.Stage, epoch = k.Epoch, lease = k.Lease, active = k.Active, outcome = got.Outcome }) : Results.Json(new { none = true, outcome = got.Outcome });
            }
            catch (Exception e) when (e is ArgumentException or JsonException) { return Results.BadRequest(new { error = e.Message }); }
            catch (OperationCanceledException) { return Results.StatusCode(499); }
            catch (StorageBudgetExceeded e) { return Results.Json(new { error = Redact.Sas(e.Message) }, statusCode: 503); }
            catch (Exception e) { Log.Info("serve: download/sampled failed: " + Redact.Sas(e.ToString())); return Results.Json(new { error = Redact.Sas(e.Message) }, statusCode: 500); }
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
            if (!string.IsNullOrEmpty(SearchToken) && !(c.Request.Headers.Authorization.ToString() is { } auth && auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
                && System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(System.Text.Encoding.UTF8.GetBytes(auth[7..].Trim()), System.Text.Encoding.UTF8.GetBytes(SearchToken)))) return Results.Json(new { error = "unauthorized" }, statusCode: 401);
            try
            {
                if (!c.Request.HasFormContentType) return Results.BadRequest(new { error = "multipart/form-data expected" });
                var form = await c.Request.ReadFormAsync(c.RequestAborted);
                async Task<byte[]?> Part(string n) { if (form.Files.GetFile(n) is not { } f) return null; using var ms = new MemoryStream(); await f.CopyToAsync(ms, c.RequestAborted); return ms.ToArray(); }
                string Field(string n) => form.TryGetValue(n, out var v) && v.ToString().Length > 0 ? v.ToString() : c.Request.Query[n].ToString();
                int Int(string n, int d, int max) => Field(n) is { Length: > 0 } s ? Math.Clamp(int.Parse(s, System.Globalization.CultureInfo.InvariantCulture), 1, max) : d;
                bool trim = Field("trim") is var tv && (tv.Equals("true", StringComparison.OrdinalIgnoreCase) || tv == "1");
                var res = await SearchAsync(await Part("image"), form.Files.GetFile("image")?.FileName ?? "", await Part("vector"), trim, Field("topCenters").Length > 0 ? Int("topCenters", 300, 5000) : 0, Int("top", 20, 200), c.RequestAborted, Field("maxUploads") is { Length: > 0 } mu ? Math.Clamp(int.Parse(mu, System.Globalization.CultureInfo.InvariantCulture), 0, 5000) : -1, Field("ranged") is { Length: > 0 } rg ? rg.Equals("true", StringComparison.OrdinalIgnoreCase) || rg == "1" : null);
                return Results.Text(res.ToJsonString(), "application/json");
            }
            catch (Exception e) when (e is ArgumentException or FormatException or OverflowException or InvalidDataException or BadHttpRequestException) { Interlocked.Increment(ref failures); return Results.BadRequest(new { error = e.Message }); }
            catch (OperationCanceledException) { return Results.StatusCode(499); }
            catch (StorageBudgetExceeded e) { Interlocked.Increment(ref failures); return Results.Json(new { error = Redact.Sas(e.Message) }, statusCode: 503); }
            catch (Exception e) { Interlocked.Increment(ref failures); Log.Info("serve: search failed: " + Redact.Sas(e.ToString())); return Results.Json(new { error = Redact.Sas(e.Message) }, statusCode: 500); }
        });
        Look?.Map(app, c => { Interlocked.Increment(ref requests); return TokenOk(c) || SearchTokenOk(c); });
        return app;
    }
}

/// <summary>`indexnode serve [--port 8080] [--bind 0.0.0.0] [--threads N] [--block-cache-mb 512] [--concurrency 2] [--input-local dir] [--shadow --out-local dir]`. 0.13.55: --shadow reads the real store and writes only under the folder (a dry run of the download service on the live queue).</summary>
public static class ServeCommand
{
    public static async Task<int> RunAsync(Args a)
    {
        var cfg = NodeConfig.Load(); var http = Commands.NewHttp(); var paths = new Paths(a.Get("prefix") ?? "index/");
        string? inL = a.Get("input-local") ?? a.Get("local");
        IStore store = inL != null ? new LocalStore(Path.GetFullPath(inL)) : !string.IsNullOrEmpty(cfg.Sas) ? new RestStore(http, cfg.Sas) : throw new InvalidOperationException("no SAS: set INDEXNODE_SAS_URL (a read-only container SAS) or use --input-local <dir>");
        // 0.13.55: `serve --shadow --out-local <dir>` reads the real store and writes nothing to it (the queue lines, the leases and every record go under <dir>), as `run --shadow` does.
        // Before it the option was ignored here, and a serve started "in shadow" wrote to the real queue.
        string? zips = null;
        if (a.Has("shadow"))
        {
            if (inL != null) throw new ArgumentException("--shadow reads the real store: it does not go with --input-local");
            var od = a.Get("out-local") ?? throw new ArgumentException("--shadow needs --out-local <dir>: the serve reads the real store and writes only there");
            store = Commands.Shadow = new ShadowStore(store, od); zips = Path.Combine(od, "zips");
            Console.WriteLine($"serve: SHADOW: reads the real store, writes nothing to it; every write goes under {od}");
        }
        else if (a.Get("out-local") != null) throw new ArgumentException("--out-local goes with --shadow (serve writes to the store it reads unless it runs in shadow)");
        store = StageZipStore.Wrap(store, zips);
        if (inL != null) store = new MeteredStore(store);   // a RestStore meters itself; a local folder is counted here, so /v1/check reports its ops either way
        int threads = Math.Max(1, a.Get("threads") is { } t ? int.Parse(t) : cfg.EffectiveThreads); int port = a.Get("port") is { } p ? int.Parse(p) : 8080;
        var st = cfg.PreCheck ?? new();
        var tools = new Tools(http, cfg);
        var cache = new SearchIndexCache(Path.Combine(NodeConfig.ConfigDir(), "search-cache"), TimeSpan.FromMinutes(5));
        var blocks = new BlockCache((a.Get("block-cache-mb") is { } m ? long.Parse(m) : 512) * 1024 * 1024);
        var analyzer = new PreCheckAnalyzer(tools, cfg, http, store, paths, cache) { Blocks = blocks, InProcess = true };
        var server = new PreCheckServer(analyzer, cache, store, paths, blocks, st, threads, Commands.VersionString, a.Get("concurrency") is { } c ? int.Parse(c) : 2) { ImageConfig = cfg, ImageHttp = http, Tools = tools, Look = new LookServer(store, paths, tools, http, cache, threads), SearchToken = Environment.GetEnvironmentVariable("INDEXNODE_SEARCH_TOKEN") is { Length: > 0 } stok ? stok.Trim() : null, Token = Environment.GetEnvironmentVariable("INDEXNODE_SERVE_TOKEN") is { Length: > 0 } et ? et.Trim() : cfg.Serve?.Token };
        if (string.IsNullOrEmpty(server.Token)) Console.WriteLine("serve: no serve.token / INDEXNODE_SERVE_TOKEN: POST /v1/check answers 401 to everyone");
        using var cts = new CancellationTokenSource();
        // 0.13.21: SIGINT and SIGTERM stop serve gracefully; if the stop has not finished in 8 s the process exits anyway (a hung shutdown must not keep the port or the old binary alive)
        void Stop(string why) { if (cts.IsCancellationRequested) return; Console.WriteLine($"serve: {why}: stopping"); cts.Cancel(); _ = Task.Run(async () => { await Task.Delay(TimeSpan.FromSeconds(8)); Console.WriteLine("serve: shutdown did not finish in 8 s: exiting"); Environment.Exit(1); }); }
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; Stop("SIGINT"); };
        using var sigterm = System.Runtime.InteropServices.PosixSignalRegistration.Create(System.Runtime.InteropServices.PosixSignal.SIGTERM, c => { c.Cancel = true; Stop("SIGTERM"); });
        // 0.13.59: the port opens BEFORE the load, answering "not ready" (see PreCheckServer.Ready), so a caller is told to wait instead of meeting a closed port
        var bind = a.Get("bind") ?? "0.0.0.0";
        var app = server.Build($"http://{bind}:{port}"); server.Ready = false;
        await app.StartAsync(cts.Token);
        Console.WriteLine($"serve: listening on {string.Join(", ", app.Urls)}  (POST /v1/check [bearer token], POST /v1/precheck, POST /v1/search, GET /v1/frames + /v1/actors [bearer token], GET /v1/health); answering 503 until the load is done");
        Console.WriteLine($"serve: loading models and the search index ({threads} threads)...");
        var sw = Stopwatch.StartNew();
        await analyzer.WarmAsync(threads, cts.Token);
        Console.WriteLine($"serve: warm in {sw.Elapsed.TotalSeconds:0.0}s: models [{string.Join(", ", analyzer.Loaded)}], index v{(await server.IndexAsync(cts.Token))?.Version.ToString() ?? "none"}");
        try { await FramesListing.For(store).EnsureAsync(store, paths, cts.Token); } catch (Exception e) when (e is not OperationCanceledException) { Console.WriteLine("serve: frames listing not loaded: " + e.Message); }   // warm: the check's lookup needs no LIST of its own
        _ = server.RefreshLoopAsync(TimeSpan.FromMinutes(5), cts.Token);
        if (a.Has("warm"))   // 0.13.31: every frames file into the local cache once (paced at 80% of the per-minute cap); from then on a search reads nothing from the store
            _ = Task.Run(async () => { var w = Stopwatch.StartNew(); var r = await PreCheckServer.WarmFramesAsync(store, await server.IndexAsync(cts.Token), new OpPacer(() => StoreMeter.Current.Total, () => StoreMeter.Current.Limits.MaxPerMin), Console.WriteLine, cts.Token); Console.WriteLine($"serve: warm done in {w.Elapsed.TotalMinutes:0.0} min: {r.Fetched} fetched, {r.Cached} already cached, {r.Failed} failed"); });
        if (a.Has("download-service")) await DownloadServiceHost.StartAsync(server, store, paths, cfg, a, cts.Token);
        server.ReadyAfterSeconds = sw.Elapsed.TotalSeconds; server.Ready = true;
        Console.WriteLine($"serve: ready after {sw.Elapsed.TotalSeconds:0.0}s");
        try { await Task.Delay(Timeout.Infinite, cts.Token); } catch (OperationCanceledException) { }
        using (var stopTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5))) { try { await app.StopAsync(stopTimeout.Token); } catch (OperationCanceledException) { } }
        return 0;
    }
}
