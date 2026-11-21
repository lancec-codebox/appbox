using System.Text.Json;
using System.Text.Json.Nodes;

namespace IndexNode;

/// <summary>
/// `indexnode precheck &lt;id&gt;... [--ids file] [--output-local dir] [--window-position 0.5] [--keep-sample] [--exclude-self] [--threads N]`: ONLY the pre-check analysis (0.13.0: on the video already stored in region, the same one a process claim runs first).
/// Reads the store, never writes to it. Each upload's record goes to &lt;dir&gt;/&lt;id&gt;/precheck.json (default ./precheck-out). One line per id.
/// </summary>
public static class PreCheckCommand
{
    public static async Task<int> RunAsync(Args a, IStore? remote = null, Tools? toolsOverride = null, PreCheckAnalyzer? analyzerOverride = null)
    {
        if (a.Positional.FirstOrDefault() == "whatif") return await PreCheckWhatIf.RunAsync(a, remote);
        string outDir = Path.GetFullPath(a.Get("output-local") ?? "precheck-out");
        string? inL = a.Get("input-local") ?? a.Get("local");
        var cfg = NodeConfig.Load(); var http = Commands.NewHttp(); var paths = new Paths(a.Get("prefix") ?? "index/");
        IStore store = remote ?? (inL != null ? new LocalStore(Path.GetFullPath(inL)) : !string.IsNullOrEmpty(cfg.Sas) ? new RestStore(http, cfg.Sas) : throw new InvalidOperationException("no SAS: run 'indexnode config set-sas <url>' or pass --input-local"));
        if (remote == null) store = StageZipStore.Wrap(store, null);
        var inputs = new List<string>(a.Positional);
        if (a.Get("ids") is { } f) inputs.AddRange(File.ReadAllLines(f).Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith('#')));
        if (inputs.Count == 0) throw new ArgumentException("precheck <id>... [--ids <file>] [--output-local <dir>] [--window-position 0.5] [--keep-sample] [--exclude-self] [--threads N]");
        int threads = Math.Max(1, a.Get("threads") is { } t ? int.Parse(t) : cfg.EffectiveThreads);
        var st = cfg.PreCheck ?? new();
        if (a.Get("window-position") is { } wp) st.WindowPosition = double.Parse(wp, System.Globalization.CultureInfo.InvariantCulture);
        var tools = toolsOverride ?? new Tools(http, cfg);
        bool exclude = a.Has("exclude-self");
        var analyzer = analyzerOverride ?? new PreCheckAnalyzer(tools, cfg, http, store, paths, new SearchIndexCache(Path.Combine(NodeConfig.ConfigDir(), "search-cache"), TimeSpan.FromMinutes(st.IndexRecheckMinutes)));
        var stage = new PreCheckStage(tools, analyzer, st, Commands.VersionString);
        using var cts = new CancellationTokenSource(); Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
        if (await analyzer.UnavailableAsync(st, cts.Token) is { } why) { Console.Error.WriteLine("precheck: cannot run here: " + why); return 1; }
        int failed = 0;
        foreach (var input in inputs)
        {
            string id = "?";
            try
            {
                id = System.Text.RegularExpressions.Regex.IsMatch(input, @"^[A-Za-z0-9_-]{11}$") ? input : YtDlp.VideoId(input) ?? throw new ArgumentException($"not a video id or url: {input}");
                if (exclude) analyzer.ExcludeId = id;
                var job = JobDescriptor.NewDownload($"https://www.youtube.com/watch?v={id}", 360, null); job.JobId = id;
                var ctx = new JobContext { Job = job, Stage = "precheck", BlobName = "", LeaseId = "", Worker = 0, Store = store, Paths = paths, NodeId = cfg.NodeId ?? "", Token = cts.Token, ThreadShare = () => threads, Report = (_, _) => { } };
                var dir = Path.Combine(outDir, id); Directory.CreateDirectory(dir);
                var run = await stage.AnalyzeAsync(ctx, a.Has("keep-sample") ? dir : null, cts.Token);
                if (run == null) { Console.WriteLine($"{id}: skipped (no stored video, no index or model, or the analysis failed: see the log)"); continue; }
                var rec = run.Record; rec["threads"] = threads; if (exclude) rec["excludedSelfCenters"] = analyzer.Excluded;
                File.WriteAllText(Path.Combine(dir, "precheck.json"), rec.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                Console.WriteLine(Line(id, run, threads));
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception e) { failed++; Console.WriteLine($"{id}: FAILED {Redact.Sas(e.Message)}"); }
        }
        return failed == 0 ? 0 : 1;
    }

    public static string Line(string id, PreCheckStage.Run r, int threads)
    {
        var d = r.Decision; var s = r.Analysis.Seconds;
        string Sec(string k) => s.TryGetValue(k, out var v) ? v.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) : "-";
        string top = d.Match is { } m ? $"{m.Upload} share {m.Share:0.00} (cos {m.BestCosine:0.00})" : "no match";
        string ai = $"ai {r.Analysis.AiScore:0.000}{(r.Ai ? " FLAG" : "")}";
        string speech = r.Analysis.Speech is { } sp ? (sp.Unavailable == null ? "  speech " + (sp.Language ?? "-") + (sp.Dubbed ? " DUB" : "") : "  speech: metadata fallback") : "";
        return $"{id}: {d.Decision}  {top}  {ai}  threads={threads}  frames {Sec("frames")}s, sscd {Sec("sscd")}s, lookup {Sec("lookup")}s, ai {Sec("ai")}s{speech}";
    }
}
