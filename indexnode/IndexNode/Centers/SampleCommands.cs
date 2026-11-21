using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace IndexNode;

/// <summary>
/// 0.13.16 one-item test commands, both WRITE-FREE (no store write, no queue line) and boxed in time (--timebox seconds, default 120: past it the command stops and prints where the time went).
/// `indexnode sample &lt;id&gt; --output-local &lt;dir&gt;`: the sample download only (YouTube: run it on lancecl1, never the hexad host); the files land in &lt;dir&gt;/&lt;id&gt;/.
/// `indexnode check &lt;id&gt; --input-local &lt;dir&gt; [--exclude-self]`: the check on those local files (the index is read from the store, read-only): prints the record and the time split. Runs on the hexad host.
/// </summary>
public static class SampleCommands
{
    public const double DefaultTimebox = 120;

    static double Timebox(Args a) => a.Get("timebox") is { } t ? double.Parse(t, CultureInfo.InvariantCulture) : DefaultTimebox;
    static string Id(Args a, string usage)
    {
        var input = a.Positional.FirstOrDefault() ?? throw new ArgumentException(usage);
        return System.Text.RegularExpressions.Regex.IsMatch(input, @"^[A-Za-z0-9_-]{11}$") ? input : YtDlp.VideoId(input) ?? throw new ArgumentException($"not a video id or url: {input}");
    }
    static string F(double v) => v.ToString("0.0", CultureInfo.InvariantCulture);

    public static async Task<int> SampleAsync(Args a, ITools? toolsOverride = null, TextWriter? output = null)
    {
        var o = output ?? Console.Out;
        const string usage = "sample <id|url> --output-local <dir> [--timebox 120]";
        var id = Id(a, usage); var outDir = Path.GetFullPath(a.Get("output-local") ?? throw new ArgumentException(usage));
        var cfg = NodeConfig.Load(); using var http = Commands.NewHttp();
        var st = cfg.PreCheck ?? new();
        var tools = toolsOverride ?? new Tools(http, cfg);
        var stage = new SampleStage(tools, null!, new DownloadGate(), st);
        double box = Timebox(a);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(box)); Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
        var sw = System.Diagnostics.Stopwatch.StartNew(); string phase = "start";
        try
        {
            await using var res = await stage.FetchAsync(id, "https://www.youtube.com/watch?v=" + id, false, (s, p) => { if (p != null && !p.EndsWith('%')) phase = p; else if (s != null) phase = s; }, cts.Token);
            var dir = Path.Combine(outDir, id); Directory.CreateDirectory(dir);
            long bytes = 0;
            foreach (var f in res.Segments.Concat(res.Audio != null ? [res.Audio] : []).Concat(new[] { "description.txt", "video.jpg" }))
                if (await res.Work.ExistsAsync(f, cts.Token)) { var to = Path.Combine(dir, f == "video.jpg" ? "thumb.jpg" : f); File.Copy(await res.Work.LocalPathAsync(f, cts.Token), to, true); bytes += new FileInfo(to).Length; }
            if (res.Info != null) File.WriteAllText(Path.Combine(dir, "info.json"), res.Info.ToJsonString());
            File.WriteAllText(Path.Combine(dir, "sample.json"), SampleStage.Marker(res, cfg.NodeId ?? "cli").ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            o.WriteLine($"{id}: sample downloaded to {dir}: {res.Segments.Count} segment(s) {string.Join(", ", res.Windows.Select(w => $"{w.Start:0}-{w.End:0}s"))}, audio {(res.Audio ?? "none")}, {bytes / 1048576.0:0.0} MB");
            o.WriteLine($"    time split: format {res.Format ?? "?"}, info {F(res.Seconds.GetValueOrDefault("info"))}s, sections {F(res.Seconds.GetValueOrDefault("sections"))}s, audio {F(res.Seconds.GetValueOrDefault("audio"))}s, total {F(sw.Elapsed.TotalSeconds)}s (timebox {box:0}s; the full-download gate is not applied)");
            return 0;
        }
        catch (OperationCanceledException)
        {
            o.WriteLine($"{id}: STOPPED at the {box:0}s timebox, in the '{phase}' phase after {F(sw.Elapsed.TotalSeconds)}s (nothing was written to the store or the queue)");
            return 3;
        }
        catch (Exception e) { o.WriteLine($"{id}: FAILED in the '{phase}' phase after {F(sw.Elapsed.TotalSeconds)}s: {Redact.Sas(e.Message)}"); return 1; }
    }

    public static async Task<int> CheckAsync(Args a, IStore? remote = null, Tools? toolsOverride = null, PreCheckAnalyzer? analyzerOverride = null, TextWriter? output = null)
    {
        var o = output ?? Console.Out;
        const string usage = "check <id|url> --input-local <dir> [--exclude-self] [--output-local <dir>] [--timebox 120] [--threads N]";
        var id = Id(a, usage); var inRoot = Path.GetFullPath(a.Get("input-local") ?? throw new ArgumentException(usage));
        var dir = File.Exists(Path.Combine(inRoot, id, "sample.json")) ? Path.Combine(inRoot, id) : inRoot;
        if (!File.Exists(Path.Combine(dir, "sample.json"))) throw new ArgumentException($"no sample.json in {dir}: run the sample command first (indexnode sample {id} --output-local ...)");
        var cfg = NodeConfig.Load(); var http = Commands.NewHttp(); var paths = new Paths(a.Get("prefix") ?? "index/");
        IStore store = remote ?? (!string.IsNullOrEmpty(cfg.Sas) ? StageZipStore.Wrap(new RestStore(http, cfg.Sas), null) : throw new InvalidOperationException("no SAS: the check reads the search index from the store (read-only): run 'indexnode config set-sas <url>'"));
        int threads = Math.Max(1, a.Get("threads") is { } t ? int.Parse(t) : cfg.EffectiveThreads);
        var st = cfg.PreCheck ?? new();
        var tools = toolsOverride ?? new Tools(http, cfg);
        var analyzer = analyzerOverride ?? new PreCheckAnalyzer(tools, cfg, http, store, paths, new SearchIndexCache(Path.Combine(NodeConfig.ConfigDir(), "search-cache"), TimeSpan.FromMinutes(st.IndexRecheckMinutes)));
        if (a.Has("exclude-self")) analyzer.ExcludeId = id;
        var stage = new PreCheckStage(tools, analyzer, st, Commands.VersionString);
        double box = Timebox(a);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(box)); Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
        string phase = "start"; var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            if (await analyzer.UnavailableAsync(st, cts.Token) is { } why) { o.WriteLine($"{id}: cannot run here: {why}"); return 1; }
            var job = JobDescriptor.NewDownload("https://www.youtube.com/watch?v=" + id, 360, null); job.JobId = id;
            var ctx = new JobContext { Job = job, Stage = "check", BlobName = "", LeaseId = "", Worker = 0, Store = store, Paths = paths, NodeId = cfg.NodeId ?? "", Token = cts.Token, ThreadShare = () => threads, Report = (s, p) => { if (p != null) phase = s + " " + p; else if (s != null) phase = s; } };
            var run = await stage.AnalyzeLocalSampleAsync(ctx, dir, null, cts.Token);
            if (run == null) { o.WriteLine($"{id}: skipped (no index or model, or the analysis failed: see the log)"); return 1; }
            var rec = run.Record; rec["threads"] = threads; if (a.Has("exclude-self")) rec["excludedSelfCenters"] = analyzer.Excluded;
            if (a.Get("output-local") is { } od) { Directory.CreateDirectory(Path.Combine(od, id)); File.WriteAllText(Path.Combine(od, id, "check.json"), rec.ToJsonString(new JsonSerializerOptions { WriteIndented = true })); }
            o.WriteLine(PreCheckCommand.Line(id, run, threads));
            o.WriteLine(rec.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            o.WriteLine($"    wall time {F(sw.Elapsed.TotalSeconds)}s of the {box:0}s timebox; nothing was written to the store or the queue");
            return 0;
        }
        catch (OperationCanceledException)
        {
            o.WriteLine($"{id}: STOPPED at the {box:0}s timebox, in '{phase}' after {F(sw.Elapsed.TotalSeconds)}s (nothing was written to the store or the queue)");
            return 3;
        }
        catch (Exception e) { o.WriteLine($"{id}: FAILED after {F(sw.Elapsed.TotalSeconds)}s: {Redact.Sas(e.Message)}"); return 1; }
        finally { if (remote == null) http.Dispose(); }
    }
}
