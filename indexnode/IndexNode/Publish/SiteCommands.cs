using System.Text.Json;

namespace IndexNode;

/// <summary>`publish`, `describe` and `medusa-setup`.</summary>
public static class SiteCommands
{
    public static async Task<int> PublishAsync(Args a)
    {
        var outDir = a.Get("out") ?? Publish.DefaultOut(); Directory.CreateDirectory(outDir);
        var cfg = NodeConfig.Load(); var http = Commands.NewHttp(); var tools = new Tools(http, cfg); var ct = CancellationToken.None;
        var jobs = new List<IJobSource>(); bool force = a.Has("force");
        if (a.Get("local") is { } local)
        {
            // a job folder, or a folder of job folders
            if (File.Exists(Path.Combine(local, "processed.json"))) jobs.Add(new LocalJob(local, a.Get("job") ?? Path.GetFileName(Path.GetFullPath(local))));
            else foreach (var d in Directory.GetDirectories(local).OrderBy(x => x)) if (File.Exists(Path.Combine(d, "processed.json")) && (a.Get("job") is not { } j || j == Path.GetFileName(d))) jobs.Add(new LocalJob(d, Path.GetFileName(d)));
        }
        else
        {
            if (string.IsNullOrEmpty(cfg.Sas)) throw new InvalidOperationException("no SAS: run 'indexnode config set-sas <url>', or publish a local job folder with --local <dir>");
            var store = new RestStore(http, cfg.Sas); var paths = new Paths(a.Get("prefix") ?? "index/");
            var since = a.Get("since") is { } s ? DateTimeOffset.Parse(s, System.Globalization.CultureInfo.InvariantCulture).ToUniversalTime() : (DateTimeOffset?)null;
            foreach (var b in await store.ListAsync(paths.Prefix + "videos/", false, false, ct))
            {
                if (!b.Name.EndsWith("/describe.json")) continue;
                var id = b.Name[(paths.Prefix + "videos/").Length..].Split('/')[0];
                if (a.Get("job") is { } only && only != id) continue;
                var sj = new StoreJob(store, paths, id);
                if (since != null)
                {
                    // BlobEntry has no modified time: describe.json carries its own "at"
                    var at = await sj.ReadAsync("describe.json", ct) is { } db ? System.Text.Json.Nodes.JsonNode.Parse(db)?["at"]?.GetValue<string>() : null;
                    if (at == null || DateTimeOffset.Parse(at, System.Globalization.CultureInfo.InvariantCulture) < since) continue;
                }
                jobs.Add(sj);
            }
        }
        int written = 0, skipped = 0;
        foreach (var j in jobs)
        {
            var r = await Publish.PublishOneAsync(tools, j, outDir, force, ct);
            Console.WriteLine(r.Written ? $"{r.Id}: published" : $"{r.Id}: skipped ({r.Skipped})");
            if (r.Written) written++; else skipped++;
        }
        Publish.WriteIndex(outDir);
        Console.WriteLine($"publish: {written} written, {skipped} skipped -> {outDir}");
        return 0;
    }

    /// <summary>`process --job &lt;id&gt; --steps cast,describe`: redo steps of one already-processed job in the store (needs the SAS; describe needs the AI endpoint). Nothing is queued or leased; processed.json is not rewritten.</summary>
    public static async Task<int> ProcessAsync(Args a)
    {
        var id = a.Get("job") ?? throw new ArgumentException("process --job <id> [--steps cast,describe] [--threads N] [--ai-... ]");
        var cfg = NodeConfig.Load(); if (string.IsNullOrEmpty(cfg.Sas)) throw new InvalidOperationException("no SAS: run 'indexnode config set-sas <url>' or set INDEXNODE_SAS_URL");
        var steps = (a.Get("steps") ?? "cast,describe").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToArray();
        foreach (var s in steps) if (!ProcessStage.KnownSteps.Contains(s)) throw new ArgumentException($"unknown step '{s}' (known: {string.Join(',', ProcessStage.KnownSteps)})");
        ProcessStage.Ai = AiConfig.Resolve(cfg.Ai, k => a.Get(k), Environment.GetEnvironmentVariable);
        if (steps.Contains("describe") && !ProcessStage.Ai.Configured) throw new InvalidOperationException("describe needs an AI endpoint: ai.* in the config, INDEXNODE_AI_* or --ai-base-url/--ai-key/--ai-text-model/--ai-vision-model");
        var http = Commands.NewHttp(); var tools = new Tools(http, cfg); var store = new RestStore(http, cfg.Sas); var paths = new Paths(a.Get("prefix") ?? "index/");
        using var cts = new CancellationTokenSource(); Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
        var threads = a.Get("threads") is { } t ? int.Parse(t) : cfg.EffectiveThreads;
        var ctx = new JobContext
        {
            Job = new JobDescriptor { Stage = "process", JobId = id, Steps = steps }, Stage = "process", BlobName = "", LeaseId = "", Worker = 0, Store = store, Paths = paths, NodeId = cfg.NodeId ?? "cli",
            Token = cts.Token, ThreadShare = () => threads, Report = (step, p) => Console.WriteLine($"  {step} {p}"),
        };
        await new ProcessStage(tools, store, cfg, http) { Redo = true }.RunAsync(ctx);
        Console.WriteLine($"process {id}: {string.Join(',', steps)} done (outputs replaced in the store; run 'indexnode publish --job {id}' next)");
        return 0;
    }

    public static async Task<int> DescribeAsync(Args a)
    {
        var local = a.Get("local") ?? throw new ArgumentException("describe --local <job folder> [--dry] [--out <file>] [--ai-... ]");
        var cfg = NodeConfig.Load(); var ai = AiConfig.Resolve(cfg.Ai, k => a.Get(k), Environment.GetEnvironmentVariable); var ct = CancellationToken.None;
        var work = Path.Combine(Path.GetTempPath(), "indexnode", "describe-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var src = new LocalJob(local, a.Get("job") ?? Path.GetFileName(Path.GetFullPath(local)));
            var d = await DescribeJob.GatherAsync(src, work, ct);
            if (a.Has("dry"))
            {
                var req = DescribeJob.Dry(d, ai); var dest = a.Get("out") ?? Path.Combine(local, "describe.request.json");
                File.WriteAllText(dest, req); Console.WriteLine($"describe (dry): request written to {dest} ({req.Length} chars, 0 AI calls)");
                return 0;
            }
            if (!ai.Configured) { Console.WriteLine("describe: skipped (no ai.* configured)"); return 0; }
            var bytes = await DescribeJob.RunAsync(new AiClient(Commands.NewHttp(), ai), ai, d, ct);
            File.WriteAllBytes(a.Get("out") ?? Path.Combine(local, "describe.json"), bytes!);
            Console.WriteLine("describe: done"); return 0;
        }
        finally { try { Directory.Delete(work, true); } catch { } }
    }

    public static int MedusaSetup(Args a)
    {
        var cfg = NodeConfig.Load(); bool noSas = a.Has("no-sas");
        var o = new MedusaSetup.Options(cfg.Sas, a.Get("name"), a.Get("conditions") ?? "wifi+charging", a.Get("daily-cap") is { } dc ? int.Parse(dc) : 20,
            a.Get("budget") is { } b ? int.Parse(b) : null, a.Get("schedule"), a.Get("window"), a.Get("max-height") is { } mh ? int.Parse(mh) : 480);
        Console.Write(IndexNode.MedusaSetup.Run(o, a.Has("qr"), a.Get("file"), a.Get("dir"), noSas));
        return 0;
    }
}
