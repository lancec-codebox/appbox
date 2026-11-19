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
            // 0.10.0 layout: a drama is its stage zips; the wrapper serves each old per-file name from the right zip (describe.json from describe.zip, info.json and downloaded.json from download.zip's contents and summary)
            var store = StageZipStore.Wrap(new RestStore(http, cfg.Sas)); var paths = new Paths(a.Get("prefix") ?? "index/");
            var since = a.Get("since") is { } s ? DateTimeOffset.Parse(s, System.Globalization.CultureInfo.InvariantCulture).ToUniversalTime() : (DateTimeOffset?)null;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            // dramas are found by describe.zip (an older loose describe.json still counts)
            foreach (var b in await store.ListAsync(paths.Prefix + "videos/", false, false, ct))
            {
                if (!b.Name.EndsWith("/describe.zip") && !b.Name.EndsWith("/describe.json")) continue;
                var id = b.Name[(paths.Prefix + "videos/").Length..].Split('/')[0];
                if (a.Get("job") is { } only && only != id) continue;
                if (!seen.Add(id)) continue;
                var sj = new StoreJob(store, paths, id);
                if (since != null)
                {
                    // --since: describe.zip's idx_at; a loose describe.json carries its own "at"
                    string? at = (await store.HeadAsync(paths.Video(id, "describe.zip"), ct))?.Metadata.GetValueOrDefault("idx_at");
                    at ??= await sj.ReadAsync("describe.json", ct) is { } db ? System.Text.Json.Nodes.JsonNode.Parse(db)?["at"]?.GetValue<string>() : null;
                    if (at == null || DateTimeOffset.Parse(at, System.Globalization.CultureInfo.InvariantCulture) < since) continue;
                }
                jobs.Add(sj);
            }
        }
        if (a.Has("dry-roles"))
        {
            // publish --dry-roles: no packs; counts the roleAtStart lines the spoiler-safe fallback replaces, by what replaced them
            int cast = 0, byDesc = 0, byLabel = 0, byRole = 0;
            foreach (var j in jobs)
            {
                if (await j.ReadAsync("describe.json", ct) is not { } db || System.Text.Json.Nodes.JsonNode.Parse(db) is not System.Text.Json.Nodes.JsonObject dj) continue;
                foreach (var c in (dj["cast"] as System.Text.Json.Nodes.JsonArray) ?? [])
                {
                    if (c is not System.Text.Json.Nodes.JsonObject co) continue; cast++;
                    var v = Publish.SafeRoleAtStart(co, out var rep); if (!rep) continue;
                    var line = v?.ToString();
                    if (line == (co["description"]?.ToString())) byDesc++; else if (line == (co["label"]?["en"]?.ToString())) byLabel++; else byRole++;
                    Console.WriteLine($"{j.JobId} c{co["cluster"]}: \"{co["roleAtStart"]}\" -> \"{line}\"");
                }
            }
            Console.WriteLine($"dry roles: {jobs.Count} describes, {cast} cast entries, replaced {byDesc + byLabel + byRole} (description {byDesc}, label {byLabel}, role line {byRole})");
            return 0;
        }
        int written = 0, skipped = 0;
        foreach (var j in jobs)
        {
            var r = await Publish.PublishOneAsync(tools, j, outDir, force, ct);
            Console.WriteLine(r.Written ? $"{r.Id}: published" : $"{r.Id}: skipped ({r.Skipped})");
            foreach (var w in r.Warnings ?? []) Console.WriteLine(w);
            if (r.Written) written++; else skipped++;
        }
        Publish.WriteIndex(outDir);
        Console.WriteLine($"publish: {written} written, {skipped} skipped -> {outDir}");
        return 0;
    }

    /// <summary>`process --job &lt;id&gt; --steps cast,describe`: redo steps of one already-processed job in the store (needs the SAS; describe needs the AI endpoint). Nothing is queued or leased; processed.json is not rewritten.</summary>
    /// <summary>The store a `process --job` redo works on: the raw store wrapped so downloaded.json, frames/, sscd/ ... are read from (and a step's outputs committed to) the stage zips.</summary>
    public static IStore ProcessStore(IStore raw) => StageZipStore.Wrap(raw);

    public static async Task<int> ProcessAsync(Args a)
    {
        var id = a.Get("job") ?? throw new ArgumentException("process --job <id> [--steps frames,...] [--threads N] [--ai-... ]  (no --steps: the steps whose outputs are missing; --steps: rerun those, replacing their outputs)");
        var cfg = NodeConfig.Load(); if (string.IsNullOrEmpty(cfg.Sas)) throw new InvalidOperationException("no SAS: run 'indexnode config set-sas <url>' or set INDEXNODE_SAS_URL");
        // no --steps: process an unqueued video, running only what is missing; --steps: rerun exactly those steps
        bool redo = a.Get("steps") != null;
        var steps = (a.Get("steps") ?? string.Join(',', ProcessStage.FullSteps)).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToArray();
        foreach (var s in steps) if (!ProcessStage.KnownSteps.Contains(s)) throw new ArgumentException($"unknown step '{s}' (known: {string.Join(',', ProcessStage.KnownSteps)})");
        ProcessStage.Ai = AiConfig.Resolve(cfg.Ai, k => a.Get(k), Environment.GetEnvironmentVariable);
        if ((steps.Contains("describe") || steps.Contains("translate")) && !ProcessStage.Ai.Configured) throw new InvalidOperationException("describe and translate need an AI endpoint: ai.* in the config, INDEXNODE_AI_* or --ai-base-url/--ai-key/--ai-text-model/--ai-vision-model");
        var http = Commands.NewHttp(); var tools = new Tools(http, cfg); var store = ProcessStore(new RestStore(http, cfg.Sas)); var paths = new Paths(a.Get("prefix") ?? "index/");   // 0.10.8: the store holds only stage zips; downloaded.json, frames/ and the rest are served from them (and a step's zip is committed whole)
        using var cts = new CancellationTokenSource(); Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
        var threads = a.Get("threads") is { } t ? int.Parse(t) : cfg.EffectiveThreads;
        var ctx = new JobContext
        {
            Job = new JobDescriptor { Stage = "process", JobId = id, Steps = steps }, Stage = "process", BlobName = "", LeaseId = "", Worker = 0, Store = store, Paths = paths, NodeId = cfg.NodeId ?? "cli",
            Token = cts.Token, ThreadShare = () => threads, Report = (step, p) => Console.WriteLine($"  {step} {p}"),
        };
        await new ProcessStage(tools, store, cfg, http) { Redo = redo }.RunAsync(ctx);
        Console.WriteLine($"process {id}: {string.Join(',', steps)} done ({(redo ? "outputs replaced" : "missing outputs made")} in the store; run 'indexnode publish --job {id}' next)");
        return 0;
    }

    /// <summary>describe ending --backfill [--dry-run N] [--limit N]: adds the 0.10.4 "ending" to existing describe.zip blobs (title, description, last 3 minutes of text and the synopsis only; several dramas per call).</summary>
    static async Task<int> DescribeEndingAsync(Args a)
    {
        const string use = "describe ending --backfill [--dry-run N] [--limit N] [--ai-...]   (--dry-run N: look at N dramas, print the first prompt, no AI call, nothing written)";
        if (!a.Has("backfill")) throw new ArgumentException(use);
        int? dryN = a.Has("dry-run") ? (int.TryParse(a.Get("dry-run"), out var dn) && dn > 0 ? dn : throw new ArgumentException(use)) : null;
        int? limit = dryN ?? (a.Get("limit") is { } ls ? (int.TryParse(ls, out var ln) && ln > 0 ? ln : throw new ArgumentException(use)) : null);
        var (cfg, store, paths, http) = Commands.Open(a); using var _h = http;
        var ai = AiConfig.Resolve(cfg.Ai, k => a.Get(k), Environment.GetEnvironmentVariable);
        if (dryN == null && !ai.Configured) throw new InvalidOperationException("describe ending --backfill needs an AI endpoint (ai.* in the config), or --dry-run N");
        var work = Path.Combine(Path.GetTempPath(), "indexnode", "ending-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var (rep, prompt) = await EndingBackfill.RunAsync(store, paths, dryN == null ? new AiClient(http, ai) : null, ai.TextModel ?? ai.VisionModel, cfg.NodeName ?? Environment.MachineName, work, limit, dryN != null, default);
            if (dryN != null && prompt != null) Console.WriteLine(prompt);
            Console.WriteLine($"describe ending{(dryN != null ? " (dry)" : "")}: {rep.Found} describe.zip, {rep.Skipped} skipped (has an ending or unreadable), {rep.Asked} to do, {rep.Written} written, {rep.Raced} changed meanwhile, {rep.Unanswered} {(dryN != null ? "not asked" : "without an answer")}, {rep.Calls} AI call(s)");
            return 0;
        }
        finally { try { Directory.Delete(work, true); } catch { } }
    }

    public static async Task<int> DescribeAsync(Args a)
    {
        if (a.Positional.FirstOrDefault() == "ending") return await DescribeEndingAsync(a);
        var local = a.Get("local") ?? (a.Get("job") != null ? null : throw new ArgumentException("describe --local <job folder> | --job <id> [--dry] [--out <file>] [--raw-dir <dir>] [--ai-... ]  (--job reads the store, writes only --out)"));
        if (local == null && a.Get("out") == null) throw new ArgumentException("describe --job <id> needs --out <file>: it never writes the store");
        var cfg = NodeConfig.Load(); var ai = AiConfig.Resolve(cfg.Ai, k => a.Get(k), Environment.GetEnvironmentVariable); var ct = CancellationToken.None;
        var work = Path.Combine(Path.GetTempPath(), "indexnode", "describe-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            IJobSource src;
            if (local != null) src = new LocalJob(local, a.Get("job") ?? Path.GetFileName(Path.GetFullPath(local)));
            else
            {
                if (string.IsNullOrEmpty(cfg.Sas)) throw new InvalidOperationException("no SAS: run 'indexnode config set-sas <url>' or set INDEXNODE_SAS_URL");
                src = new StoreJob(new RestStore(Commands.NewHttp(), cfg.Sas), new Paths(a.Get("prefix") ?? "index/"), a.Get("job")!);
            }
            var d = await DescribeJob.GatherAsync(src, work, ct);
            if (a.Has("dry"))
            {
                var req = DescribeJob.Dry(d, ai); var dest = a.Get("out") ?? Path.Combine(local!, "describe.request.json");
                File.WriteAllText(dest, req); Console.WriteLine($"describe (dry): request written to {dest} ({req.Length} chars, 0 AI calls)");
                return 0;
            }
            if (!ai.Configured) { Console.WriteLine("describe: skipped (no ai.* configured)"); return 0; }
            IChat chat = new AiClient(Commands.NewHttp(), ai);
            if (a.Get("raw-dir") is { } rawDir) chat = new RecordingChat(chat, rawDir); // each JSON answer of the model, as received, before the post-processing
            var bytes = await DescribeJob.RunAsync(chat, ai, d, ct);
            File.WriteAllBytes(a.Get("out") ?? Path.Combine(local!, "describe.json"), bytes!);
            Console.WriteLine("describe: done"); return 0;
        }
        finally { try { Directory.Delete(work, true); } catch { } }
    }

    /// <summary>Keeps every JSON answer of the model in a folder (describe --raw-dir), to look at what the post-processing was given.</summary>
    sealed class RecordingChat : IChat
    {
        readonly IChat inner; readonly string dir; int n;
        public RecordingChat(IChat inner, string dir) { this.inner = inner; this.dir = dir; Directory.CreateDirectory(dir); }
        public Task<string> ChatAsync(string model, System.Text.Json.Nodes.JsonNode content, CancellationToken ct) => inner.ChatAsync(model, content, ct);
        public async Task<System.Text.Json.Nodes.JsonObject> JsonAsync(string model, System.Text.Json.Nodes.JsonNode content, string tool, CancellationToken ct)
        {
            var r = await inner.JsonAsync(model, content, tool, ct);
            File.WriteAllText(Path.Combine(dir, $"{Interlocked.Increment(ref n):D2}-{tool}.json"), r.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
            return r;
        }
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
