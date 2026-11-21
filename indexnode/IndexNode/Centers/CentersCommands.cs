using System.Diagnostics;
using System.Globalization;
using System.Text;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace IndexNode;

/// <summary>`indexnode centers backfill`, `indexnode search-index build|update` and `indexnode search --vector|--image`.</summary>
public static class CentersCommands
{
    /// <summary>
    /// Input and output are separate. --input-local &lt;dir&gt;: read the uploads' sscd.zip from a folder instead of the blob store; --output-local &lt;dir&gt;: write centers.zip and the search index
    /// to a folder instead; --local &lt;dir&gt; sets both. Neither: the blob store for both (SAS). The folder has the same index/... layout as the blob store. With --output-local nothing is written
    /// to the store (a write routed to the input throws); search and search-index need no SAS then. <paramref name="remote"/> stands in for the blob store (tests).
    /// </summary>
    internal static (NodeConfig Cfg, IStore Store, Paths Paths, HttpClient Http) OpenStore(Args a, IStore? remote = null)
    {
        string? both = a.Get("local"), inL = a.Get("input-local") ?? both, outL = a.Get("output-local") ?? both;
        if (inL == null && outL == null && remote == null) return Commands.Open(a);
        var cfg = NodeConfig.Load(); var http = Commands.NewHttp(); var paths = new Paths(a.Get("prefix") ?? "index/");
        IStore? Remote() => remote ?? (string.IsNullOrEmpty(cfg.Sas) ? null : new RestStore(http, cfg.Sas));
        IStore output = outL != null ? new LocalStore(Path.GetFullPath(outL)) : Remote() ?? throw new InvalidOperationException("no SAS: run 'indexnode config set-sas <url>' or use --output-local <dir>");
        IStore input;
        if (inL != null) input = inL == outL ? output : new LocalStore(Path.GetFullPath(inL));
        else if (Remote() is { } r) input = r;
        else { Console.Error.WriteLine("indexnode: no SAS, so the uploads are read from the output folder"); input = output; }
        IStore raw = ReferenceEquals(input, output) ? output : new SplitStore(input, output, paths.Prefix + "videos/");
        var cache = outL ?? inL; 
        return (cfg, StageZipStore.Wrap(raw, cache == null ? null : Path.Combine(Path.GetFullPath(cache), ".indexnode-zips")), paths, http);
    }

    // ------------------------------------------------------------------ centers backfill
    public static async Task<int> CentersAsync(Args a, IStore? remote = null)
    {
        var sub = a.Positional.FirstOrDefault();
        if (sub != "backfill") throw new ArgumentException("centers backfill [--ids <file>] [--since <time>] [--threads N] [--dry] [--limit N]");
        var (cfg, store, paths, _) = OpenStore(a, remote);
        int threads = a.Get("threads") is { } t ? int.Parse(t) : cfg.EffectiveThreads;
        using var cts = new CancellationTokenSource(); Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
        HashSet<string>? only = a.Get("ids") is { } f ? File.ReadAllLines(f).Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith('#')).ToHashSet(StringComparer.Ordinal) : null;
        DateTimeOffset? since = a.Get("since") is { } s ? ParseSince(s) : null;
        int limit = a.Get("limit") is { } lm ? int.Parse(lm) : int.MaxValue;
        var todo = await PendingAsync(store, paths, only, since, cts.Token);
        Console.WriteLine($"centers backfill: {todo.Count} upload(s) to build{(a.Has("dry") ? " (dry run)" : "")}");
        if (a.Has("dry")) { foreach (var id in todo.Take(50)) Console.WriteLine("  " + id); return 0; }
        int ok = 0, failed = 0; var sw = Stopwatch.StartNew();
        foreach (var id in todo.Take(limit))
        {
            cts.Token.ThrowIfCancellationRequested();
            try
            {
                var msg = await CentersStep.RunAsync(store, paths, id, threads, null, cts.Token);
                if (!await StageCommit.CommitAsync(store, id, "centers", cfg.NodeId ?? "cli", false, cts.Token)) throw new InvalidOperationException("nothing was staged");
                ok++; Console.WriteLine($"  {id}: {msg}");
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception e) { failed++; StageZipStore.Stage(store)?.DiscardStaged(id, "centers"); Console.WriteLine($"  {id}: FAILED {e.Message}"); }
        }
        Console.WriteLine($"centers backfill: {ok} built, {failed} failed, {sw.Elapsed.TotalMinutes:F1} min");
        return failed == 0 ? 0 : 1;
    }

    static DateTimeOffset ParseSince(string s)
    {
        if (s.Length > 1 && char.IsLetter(s[^1]) && double.TryParse(s[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out var n))
            return DateTimeOffset.UtcNow - s[^1] switch { 'h' => TimeSpan.FromHours(n), 'd' => TimeSpan.FromDays(n), 'm' => TimeSpan.FromMinutes(n), _ => throw new ArgumentException("--since: 6h, 2d, 30m or a UTC time") };
        return DateTimeOffset.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
    }

    /// <summary>Uploads with sscd.zip and no centers.zip, or a centers.zip built from another sscd.zip (idx_inputs differs). --since looks at sscd.zip's idx_at.</summary>
    public static async Task<List<string>> PendingAsync(IStore store, Paths paths, HashSet<string>? only, DateTimeOffset? since, CancellationToken ct)
    {
        var pre = paths.Prefix + "videos/"; var sscd = new Dictionary<string, BlobEntry>(StringComparer.Ordinal); var cen = new Dictionary<string, BlobEntry>(StringComparer.Ordinal);
        foreach (var b in await store.ListAsync(pre, false, true, ct))
        {
            var rest = b.Name[pre.Length..]; int sl = rest.IndexOf('/'); if (sl < 0) continue;
            var id = rest[..sl]; var m = rest[(sl + 1)..];
            if (m == "sscd.zip") sscd[id] = b; else if (m == "centers.zip") cen[id] = b;
        }
        var res = new List<string>();
        foreach (var (id, s) in sscd.OrderBy(k => k.Key, StringComparer.Ordinal))
        {
            if (only != null && !only.Contains(id)) continue;
            if (since != null && !(DateTimeOffset.TryParse(s.Metadata.GetValueOrDefault("idx_at"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at) && at >= since)) continue;
            if (cen.TryGetValue(id, out var c) && c.Metadata.GetValueOrDefault("idx_inputs") == StageLayout.Inputs([s.Metadata.GetValueOrDefault("idx_sha256")])) continue;
            res.Add(id);
        }
        return res;
    }

    // ------------------------------------------------------------------ ai score
    /// <summary>The frames that are scored for an upload: all of them, or a random sample of <paramref name="n"/> seeded by the id (stable between runs).</summary>
    public static int[] AiSample(string id, int frames, int n, bool all)
    {
        if (all || frames <= n) return Enumerable.Range(0, frames).ToArray();
        var seed = BitConverter.ToInt32(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(id)), 0);
        var rng = new Random(seed); var idx = Enumerable.Range(0, frames).ToArray();
        for (int i = 0; i < n; i++) { int j = rng.Next(i, frames); (idx[i], idx[j]) = (idx[j], idx[i]); }
        return idx.Take(n).OrderBy(x => x).ToArray();
    }

    public static byte[] AiJson(string id, double score, int frames, int sampled, bool all, PreCheckSettings s) => System.Text.Encoding.UTF8.GetBytes(new System.Text.Json.Nodes.JsonObject
    {
        ["v"] = 1, ["id"] = id, ["model"] = AiProbe.ModelId, ["score"] = Math.Round(score, 4), ["flag"] = score >= s.AiFlag, ["flagStrict"] = score >= s.AiStrict, ["threshold"] = s.AiFlag, ["flagMax0"] = s.AiStrict, ["flagMax1"] = s.AiFlag,
        ["frames"] = frames, ["sampled"] = sampled, ["sample"] = all ? "all" : "random230:seed=sha256(id)", ["at"] = Protocol.NowUtc(),
    }.ToJsonString());

    /// <summary>`indexnode ai score [--ids &lt;file&gt;] [--all-frames] [--redo]`: the AI probe on every upload that has an sscd.zip (a random 230-frame sample seeded by the id, or all frames), written to videos/&lt;id&gt;/ai.json.</summary>
    public static async Task<int> AiScoreAsync(Args a, IStore? remote = null)
    {
        var (cfg, store, paths, _) = OpenStore(a, remote);
        using var cts = new CancellationTokenSource(); Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
        HashSet<string>? only = a.Get("ids") is { } f ? File.ReadAllLines(f).Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith('#')).ToHashSet(StringComparer.Ordinal) : null;
        bool all = a.Has("all-frames"), redo = a.Has("redo"); var st = cfg.PreCheck ?? new();
        var pre = paths.Prefix + "videos/"; var ids = new List<string>(); var have = new HashSet<string>(StringComparer.Ordinal);
        foreach (var b in await store.ListAsync(pre, false, false, cts.Token))
        {
            var rest = b.Name[pre.Length..]; int sl = rest.IndexOf('/'); if (sl < 0) continue; var id = rest[..sl]; var m = rest[(sl + 1)..];
            if (m == "sscd.zip" && (only == null || only.Contains(id))) ids.Add(id); else if (m == "ai.json") have.Add(id);
        }
        ids.Sort(StringComparer.Ordinal); int ok = 0, skipped = 0, failed = 0;
        foreach (var id in ids)
        {
            cts.Token.ThrowIfCancellationRequested();
            if (!redo && have.Contains(id)) { skipped++; continue; }
            try
            {
                var f16 = await FrameChunks.VectorsAsync(new StoreJob(store, paths, id), "sscd", cts.Token) ?? throw new InvalidOperationException("no sscd vectors");
                var rows = AiProbe.RowsFromF16(f16); var pick = AiSample(id, rows.Length, 230, all);
                double score = AiProbe.Default.Score(pick.Select(i => rows[i]).ToList());
                await store.PutAsync(paths.Video(id, "ai.json"), AiJson(id, score, rows.Length, pick.Length, all, st), null, null, false, cts.Token);
                ok++; Console.WriteLine($"  {id}: score {score:0.000}{(score >= st.AiFlag ? "  AI" : "")}  ({pick.Length} of {rows.Length} frames)");
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception e) { failed++; Console.WriteLine($"  {id}: FAILED {e.Message}"); }
        }
        Console.WriteLine($"ai score: {ok} scored, {skipped} already scored (--redo), {failed} failed");
        return failed == 0 ? 0 : 1;
    }

    // ------------------------------------------------------------------ search-index
    public static async Task<int> SearchIndexAsync(Args a, IStore? remote = null)
    {
        var sub = a.Positional.FirstOrDefault();
        if (sub is not ("build" or "update")) throw new ArgumentException("search-index build [--new-mean] | update   [--threads N]");
        var (cfg, store, paths, _) = OpenStore(a, remote);
        int threads = a.Get("threads") is { } t ? int.Parse(t) : cfg.EffectiveThreads;
        using var cts = new CancellationTokenSource(); Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
        var sw = Stopwatch.StartNew(); var startedAt = DateTimeOffset.UtcNow;
        var head = await SearchData.LoadAsync(store, paths, null, cts.Token);

        var ups = await SearchIndexBuild.ListCentersAsync(store, paths, cts.Token);
        Console.WriteLine($"search-index {sub}: {ups.Count} centers.zip, head {(head == null ? "none" : "v" + head.Version)}");
        SearchData next;
        if (sub == "update")
        {
            if (head == null) throw new InvalidOperationException("no search index yet: run search-index build first");
            next = await SearchIndexBuild.BuildAsync(store, paths, ups, head.Mean, head, head.Version + 1, head.MeanVersion, threads, Console.WriteLine, cts.Token);
        }
        else
        {
            bool newMean = a.Has("new-mean") || head == null;
            next = await SearchIndexBuild.BuildAsync(store, paths, ups, newMean ? null : head!.Mean, null, (head?.Version ?? 0) + 1, newMean ? (head?.MeanVersion ?? 0) + 1 : head!.MeanVersion, threads, Console.WriteLine, cts.Token);
            if (!newMean) next.MeanFrames = head!.MeanFrames;
        }
        next.BuiltAt = startedAt;
        var groups = await GroupBuild.ApplyAsync(store, paths, head, next, cts.Token, Console.WriteLine, threads);
        next.Catalog = await CatalogBuild.BuildAsync(store, paths, next.Version, next.Uploads, head?.Catalog, new PreCheckSettings().AiFlag, Console.WriteLine, cts.Token);
        Console.WriteLine($"catalog: {next.Catalog.Uploads.Count} uploads, {next.Catalog.ToGzip().Length / 1024.0:F1} KB gzipped");
        next.Catalog.ApplyGroups(groups);
        await next.PublishAsync(store, paths, cts.Token);
        var pruned = await SearchData.PruneAsync(store, paths, cfg.Search?.KeepVersions ?? 3, cts.Token);
        if (pruned.Count > 0) Console.WriteLine($"search-index: removed old versions {string.Join(", ", pruned.Select(g => "v" + g))}");

        Console.WriteLine($"search-index: published v{next.Version} (mean v{next.MeanVersion}): {next.Uploads.Count} uploads, {next.Count} centers, bits.bin {next.Bits.Length / 1048576.0:F1} MB, {sw.Elapsed.TotalSeconds:F1}s; head.yaml -> {SearchData.Dir(paths, next.Version)}");
        return 0;
    }

    // ------------------------------------------------------------------ search
    /// <summary>`indexnode search --vector <file> | --image <file> [--top-centers 300] [--top 20]`: the screenshot search over the global table.</summary>
    public static async Task<int> SearchAsync(Args a, IStore? remote = null)
    {
        var (cfg, store, paths, http) = OpenStore(a, remote);
        int topC = a.Get("top-centers") is { } tc ? int.Parse(tc) : 300, top = a.Get("top") is { } tp ? int.Parse(tp) : 20;
        int threads = a.Get("threads") is { } t ? int.Parse(t) : cfg.EffectiveThreads;
        using var cts = new CancellationTokenSource(); Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
        float[] q;
        if (a.Get("vector") is { } vf) q = ReadVector(File.ReadAllBytes(vf));
        else if (a.Get("image") is { } img) q = await EmbedImageAsync(new Tools(http, cfg), cfg, http, img, cts.Token, a.Has("trim"));
        else throw new ArgumentException("search --vector <f32 or f16 file of 512 values> | --image <file> [--top-centers 300] [--top 20]");
        var sw = Stopwatch.StartNew();
        var idx = await SearchData.LoadAsync(store, paths, a.Get("version") is { } v ? int.Parse(v) : null, cts.Token) ?? throw new InvalidOperationException("no search index: run search-index build");
        Console.WriteLine($"index v{idx.Version}: {idx.Uploads.Count} uploads, {idx.Count} centers (loaded in {sw.Elapsed.TotalSeconds:F1}s)");
        var (hits, tm) = await CenterSearch.QueryAsync(store, paths, idx, q, topC, top, threads, cts.Token);
        foreach (var h in hits) Console.WriteLine($"{h.Upload}  {Clip.Stamp(h.Time)}  {h.Time:F1}s  {h.Score:F4}  (row {h.Row}, center {h.Center})");
        Console.WriteLine($"timings: scan {tm.ScanMs:F1} ms, reads {tm.ReadMs:F1} ms ({tm.Requests} requests, {tm.Bytes / 1048576.0:F1} MB, {tm.Uploads} uploads), scoring {tm.ScoreMs:F1} ms");
        return 0;
    }

    /// <summary>512 values as f32 (2048 bytes) or f16 (1024 bytes).</summary>
    public static float[] ReadVector(byte[] b)
    {
        var v = new float[CentersFile.Dim];
        if (b.Length == CentersFile.Dim * 4) for (int i = 0; i < v.Length; i++) v[i] = BitConverter.ToSingle(b, i * 4);
        else if (b.Length == CentersFile.Dim * 2) Vec.FromHalf(b, v);
        else throw new ArgumentException($"--vector: {b.Length} bytes; expected 2048 (f32) or 1024 (f16)");
        return v;
    }

    /// <summary>The node's own SSCD (the int8 model, session.x64quantprecision=1 through OrtOptions) on one image: short side 288, ImageNet normalisation, L2-normalised.</summary>
    public static async Task<float[]> EmbedImageAsync(Tools tools, NodeConfig cfg, HttpClient http, string image, CancellationToken ct, bool trim = false) => (await EmbedImageDetailAsync(tools, cfg, http, image, ct, trim)).Vector;

    /// <summary>As <see cref="EmbedImageAsync"/>; <paramref name="trim"/>: black bars at the edges of the screenshot (the frames step's bar rule, <see cref="Bars"/>) are cropped off first. Also returns the crop that was applied.</summary>
    public static async Task<(float[] Vector, BarCrop Crop)> EmbedImageDetailAsync(Tools tools, NodeConfig cfg, HttpClient http, string image, CancellationToken ct, bool trim = false)
    {
        var model = await Sscd.EnsureModelAsync(cfg, http, ct); var ffmpeg = await tools.FfmpegAsync(ct); var probe = await tools.FfprobeAsync(ct);
        var pr = await Proc.RunAsync(probe, ["-v", "error", "-select_streams", "v:0", "-show_entries", "stream=width,height", "-of", "csv=p=0:s=x", image], ct);
        var wh = pr.Stdout.Trim().Split('x'); int w = int.Parse(wh[0]), h = int.Parse(wh[1]);
        var crop = BarCrop.None;
        if (trim)
        {
            // the bar rule needs two frames to call a bar "fixed"; one screenshot is its own second sample
            var graw = Path.Combine(Path.GetTempPath(), "indexnode-q-" + Guid.NewGuid().ToString("N")[..8] + ".gray");
            try
            {
                var g = await Proc.RunAsync(ffmpeg, ["-hide_banner", "-loglevel", "error", "-nostdin", "-y", "-i", image, "-frames:v", "1", "-f", "rawvideo", "-pix_fmt", "gray", graw], ct);
                if (g.ExitCode == 0 && new FileInfo(graw).Length == (long)w * h) { var lum = await File.ReadAllBytesAsync(graw, ct); crop = Bars.Detect([lum, lum], w, h); }
            }
            finally { try { File.Delete(graw); } catch { } }
        }
        var (tw, th) = Sscd.TargetSize(crop.Width(w), crop.Height(h));
        var raw = Path.Combine(Path.GetTempPath(), "indexnode-q-" + Guid.NewGuid().ToString("N")[..8] + ".rgb");
        try
        {
            var r = await Proc.RunAsync(ffmpeg, ["-hide_banner", "-loglevel", "error", "-nostdin", "-y", "-i", image, "-frames:v", "1", "-vf", (crop.IsNone ? "" : crop.Filter(w, h) + ",") + $"scale={tw}:{th}:flags=bicubic", "-f", "rawvideo", "-pix_fmt", "rgb24", raw], ct);
            if (r.ExitCode != 0) throw new InvalidOperationException("ffmpeg could not read the image: " + r.Stderr.Trim());
            var rgb = await File.ReadAllBytesAsync(raw, ct); if (rgb.Length != tw * th * 3) throw new InvalidOperationException($"image decoded to {rgb.Length} bytes, expected {tw * th * 3}");
            var session = OrtOptions.Shared(model, Math.Max(1, cfg.EffectiveThreads));
            var tensor = new DenseTensor<float>([1, 3, th, tw]); Sscd.Fill(tensor, 0, rgb, th * tw);
            using var res = session.Run([NamedOnnxValue.CreateFromTensor(session.InputMetadata.Keys.First(), tensor)]);
            var o = res.First().AsTensor<float>(); var v = new float[Sscd.Dim]; for (int d = 0; d < v.Length; d++) v[d] = o[0, d];
            Sscd.L2Normalize(v); return (v, crop);
        }
        finally { try { File.Delete(raw); } catch { } }
    }
}
