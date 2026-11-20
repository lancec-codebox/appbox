using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace IndexNode;

/// <summary>Keeps the gap of 20-40 s between YouTube downloads across all download workers of the node.</summary>
public sealed class DownloadGate(Func<TimeSpan, CancellationToken, Task>? delay = null, Func<double>? random = null)
{
    private readonly object gate = new();
    private DateTimeOffset next = DateTimeOffset.MinValue;
    private readonly Func<TimeSpan, CancellationToken, Task> delay = delay ?? ((t, ct) => Task.Delay(t, ct));
    private readonly Func<double> random = random ?? (() => Random.Shared.NextDouble());

    public async Task WaitTurnAsync(CancellationToken ct)
    {
        TimeSpan wait;
        lock (gate)
        {
            var now = DateTimeOffset.UtcNow;
            var start = next > now ? next : now;
            wait = start - now;
            next = start + TimeSpan.FromSeconds(20 + 20 * random());
        }
        if (wait > TimeSpan.Zero) { Log.Info($"download: waiting {wait.TotalSeconds:F0}s (rate limit)"); await delay(wait, ct); }
    }
}

public sealed class DownloadStage(ITools tools, IStore store, DownloadGate gate) : IStageHandler
{
    /// <summary>False (`indexnode download --no-process`): the video is stored but no processing jobs are queued; `indexnode unprocessed` lists it.</summary>
    public bool QueueProcessing { get; init; } = true;

    private static readonly string[] LangOrder = ["zh-Hans", "zh-Hant", "zh-CN", "zh-TW", "zh-HK", "zh", "zh-orig", "en"];

    /// <summary>
    /// Maps yt-dlp's subtitle files to blob names: uploaded ones in manualDir become "lang", automatic captions in autoDir
    /// become "lang.auto"; the blob is subs/NAME.vtt.
    /// </summary>
    public static List<(string Local, string Name)> CollectSubs(string? manualDir, string? autoDir)
    {
        var list = new List<(string Local, string Name)>();
        void Add(string? dir, string suffix)
        {
            if (dir == null || !Directory.Exists(dir)) return;
            foreach (var f in Directory.GetFiles(dir, "video.*.vtt"))
                list.Add((f, Path.GetFileNameWithoutExtension(f)["video.".Length..] + suffix));
        }
        Add(manualDir, ""); Add(autoDir, ".auto");
        return Order(list);
    }

    /// <summary>The same for a tool workspace: the files "video.LANG.vtt" in the two sub folders ("manual" and "auto"; null = not requested).</summary>
    public static async Task<List<(string Local, string Name)>> CollectSubsAsync(IToolWorkspace ws, string? manualDir, string? autoDir, CancellationToken ct)
    {
        var list = new List<(string Local, string Name)>();
        async Task Add(string? dir, string suffix)
        {
            if (dir == null) return;
            foreach (var f in await ws.ListAsync(dir, "video.*.vtt", ct))
                list.Add((f, Path.GetFileNameWithoutExtension(f)["video.".Length..] + suffix));
        }
        await Add(manualDir, ""); await Add(autoDir, ".auto");
        return Order(list);
    }

    private static List<(string Local, string Name)> Order(List<(string Local, string Name)> list)
    {
        static int Rank(string name)
        {
            var b = name.Replace(".auto", "");
            var i = Array.FindIndex(LangOrder, l => b == l);
            return i >= 0 ? i : b.StartsWith("en") ? LangOrder.Length - 1 : 99;
        }
        return list.OrderBy(x => Rank(x.Name)).ThenBy(x => x.Name, StringComparer.Ordinal).ToList();
    }

    /// <summary>One YouTube subtitle file through <see cref="CueShaper"/>; automatic captions are rolling, so they are deduped first.</summary>
    public static (string Vtt, ShapeStats Stats) ShapeSubs(string vtt, bool auto)
    {
        var cues = CueShaper.Shape(CueShaper.ParseVtt(vtt), null, rolling: auto);
        return (CueShaper.ToVtt(cues), CueShaper.Stats(cues));
    }

    public async Task RunAsync(JobContext ctx)
    {
        var ct = ctx.Token;
        var job = ctx.Job; var id = job.JobId;
        var paths = ctx.Paths;
        if (await ctx.Store.GetAsync(paths.Video(id, "downloaded.json"), ct) is { } done0)
        {
            await EnsureFollowUpJobs(ctx);
            return;
        }
        if (string.IsNullOrEmpty(job.Url)) throw new PermanentFailure("job has no url");
        await gate.WaitTurnAsync(ct);

        await using var work = await tools.CreateWorkspaceAsync(id, ct);
        {
            // the tool implementation adds what it knows (ffmpeg location, cookies, JS runtime)
            var common = new List<string> { "--no-playlist", "--retries", "3", "--no-warnings", "--newline" };

            ctx.Report("download", "starting");

            var main = new List<string>(common);
            main.AddRange(YtDlp.FormatArgs(job.MaxHeight ?? 480, job.Format));
            main.AddRange(new[] {
                "--merge-output-format", "mp4", "--write-info-json", "--write-thumbnail", "--convert-thumbnails", "jpg",
                "-o", "video.%(ext)s", job.Url,
            });
            var r = await tools.RunAsync(work, "yt-dlp", main, ct, onStdout: line =>
            {
                var m = Regex.Match(line, @"\[download\]\s+(\d+(\.\d+)?)%");
                if (m.Success) ctx.Report("download", m.Groups[1].Value + "%");
            });
            if (r.ExitCode != 0) YtDlp.Throw(r.Stderr);

            var (manualDir, autoDir) = job.Subs != false ? await FetchSubsAsync(ctx, work, common, ct) : (null, null);

            const string videoName = "video.mp4", infoName = "video.info.json", descriptionName = "description.txt";
            if (!await work.ExistsAsync(videoName, ct)) throw new InvalidOperationException("yt-dlp finished but video.mp4 was not produced");
            int height = 0; double duration = 0;
            DownloadedFormats? fmt = null;
            if (await work.ExistsAsync(infoName, ct))
            {
                var node = JsonNode.Parse(await work.ReadTextAsync(infoName, ct))!.AsObject();
                await work.WriteTextAsync(descriptionName, (string?)node["description"] ?? "", ct);
                node.Remove("comments");
                fmt = DownloadedFormats.FromInfo(node); // before the size trim below drops "formats"
                var json = node.ToJsonString();
                height = (int?)node["height"] ?? 0; duration = (double?)node["duration"] ?? 0;
                if (System.Text.Encoding.UTF8.GetByteCount(json) > 1_500_000)
                {
                    foreach (var k in new[] { "formats", "automatic_captions", "thumbnails", "heatmap" }) node.Remove(k);
                    json = node.ToJsonString();
                }
                await work.WriteTextAsync(infoName, json, ct);
            }
            if (duration <= 0) duration = await ToolsProbe.DurationAsync(tools, work, videoName, ct);
            if (height <= 0) height = await ToolsProbe.HeightAsync(tools, work, videoName, ct);

            var bytes = await work.SizeAsync(videoName, ct);
            Traffic.Current.Add(Traffic.YoutubeIn, await work.TotalBytesAsync(ct)); // video, subtitles, info, thumbnail
            var progress = new Progress<long>(n => ctx.Report("upload", $"{n * 100 / Math.Max(1, bytes)}%"));
            ctx.Report("upload", "0%");
            await store.PutFileAsync(paths.Video(id, "video.mp4"), await work.LocalPathAsync(videoName, ct), "video/mp4", progress, ct);
            if (await work.ExistsAsync(infoName, ct)) await store.PutFileAsync(paths.Video(id, "info.json"), await work.LocalPathAsync(infoName, ct), "application/json", null, ct);
            if (await work.ExistsAsync("video.jpg", ct)) await store.PutFileAsync(paths.Video(id, "thumb.jpg"), await work.LocalPathAsync("video.jpg", ct), "image/jpeg", null, ct);
            if (await work.ExistsAsync(descriptionName, ct)) await store.PutFileAsync(paths.Video(id, "description.txt"), await work.LocalPathAsync(descriptionName, ct), "text/plain; charset=utf-8", null, ct);
            var subFiles = await CollectSubsAsync(work, manualDir, autoDir, ct);
            foreach (var (local, name) in subFiles)
            {
                var (vtt, stats) = ShapeSubs(await work.ReadTextAsync(local, ct), name.EndsWith(".auto", StringComparison.Ordinal));
                await store.PutAsync(paths.Video(id, $"subs/{name}.vtt"), System.Text.Encoding.UTF8.GetBytes(vtt), null, null, false, ct, "text/vtt");
                await store.PutAsync(paths.Video(id, $"subs/{name}.json"), JsonSerializer.SerializeToUtf8Bytes(new { v = 1, shape = stats.ToJson() }), null, null, false, ct);
            }

            var marker = JsonSerializer.SerializeToUtf8Bytes(new
            {
                v = 1, bytes, height, durationSeconds = Math.Round(duration, 2),
                vcodec = fmt?.VCodec, acodec = fmt?.ACodec, formatId = fmt?.FormatId,
                audioLanguage = fmt?.AudioLanguage, audioNote = fmt?.AudioNote,
                audioTracks = (fmt?.AudioTracks ?? []).Select(t => new { language = t.Language, note = t.Note }).ToArray(),
                subs = subFiles.Select(s => s.Name).ToArray(), embeddable = fmt?.Embeddable ?? false, nodeId = ctx.NodeId, at = Protocol.NowUtc(),
                network = ctx.Job.Network == null ? null : new { ipv4 = ctx.Job.Network.Ipv4, ipv6Prefix = ctx.Job.Network.Ipv6Prefix, vpn = ctx.Job.Network.Vpn, transport = ctx.Job.Network.Transport },
            }, new JsonSerializerOptions { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull });
            await ctx.Store.PutAsync(paths.Video(id, "downloaded.json"), marker, null, null, false, ct);   // download.zip's summary
            await StageCommit.CommitAsync(ctx.Store, id, "download", ctx.NodeId, false, ct);   // video.mp4 is already up: the zip, last, marks the download complete
            await EnsureFollowUpJobs(ctx);
        }
    }

    /// <summary>The subtitle passes of a download (uploaded, then automatic original-language captions) into manual/ and auto/.</summary>
    private async Task<(string? Manual, string? Auto)> FetchSubsAsync(JobContext ctx, IToolWorkspace work, List<string> common, CancellationToken ct)
    {
        var job = ctx.Job; var id = job.JobId; var infoName = "video.info.json";
        var infoText = await work.ExistsAsync(infoName, ct) ? await work.ReadTextAsync(infoName, ct) : null;
        string? manualDir = null, autoDir = null;
        {
                // exact tracks from the info JSON (never a regex: it would match every auto-translated track); two passes, because yt-dlp names uploaded and automatic subtitles alike (video.<lang>.vtt)
                var plan = SubLangs.Plan(infoText);
                foreach (var auto in new[] { false, true })
                {
                    var wanted = auto ? plan.AutoArg : plan.ManualArg;
                    if (wanted.Length == 0) { Log.Info($"download {id}: no {(auto ? "original-language automatic captions" : "uploaded subtitles")} to request"); continue; }
                    ctx.Report(auto ? "auto-subs" : "subs", null);
                    var dir = auto ? "auto" : "manual";
                    var subs = new List<string>(common)
                    {
                        "--skip-download", auto ? "--write-auto-subs" : "--write-subs", "--sub-langs", wanted, "--sub-format", "vtt", "--convert-subs", "vtt",
                        "-o", dir + "/video.%(ext)s", job.Url,
                    };
                    var sr = await tools.RunAsync(work, "yt-dlp", subs, ct);
                    if (sr.ExitCode != 0)
                    {
                        // a missing or rate-limited subtitle track must not lose a downloaded video
                        if (!auto && YtDlp.Classify(sr.Stderr) == FailureKind.BotBlock) YtDlp.Throw(sr.Stderr);
                        Log.Info($"download {id}: {(auto ? "automatic captions" : "subtitles")} unavailable ({YtDlp.LastErrorLine(sr.Stderr)})");
                    }
                    if (auto) autoDir = dir; else manualDir = dir;
                }
            }

        return (manualDir, autoDir);
    }

    /// <summary>0.13.0: the download fetches the upload's comments itself, and its done adds the `process` entry; its done always adds the `process` entry (the pre-check is the first step of processing, not of the download).</summary>
    internal async Task EnsureFollowUpJobs(JobContext ctx)
    {
        var id = ctx.Job.JobId;
        // comments are part of the download (owner decision, 0.13.0), a duplicate's metadata entry included; a repeat run does not fetch them again
        if (await ctx.Store.HeadAsync(ctx.Paths.Video(id, "comments.zip"), ctx.Token) == null)
        {
            var cj = new JobDescriptor { Stage = "download", JobId = id, Url = ctx.Job.Url ?? "https://www.youtube.com/watch?v=" + id, MaxComments = 1000, Sort = "top", Round = 0, AddedAt = Protocol.NowUtc(), Priority = ctx.Job.Priority };
            var cctx = new JobContext { Job = cj, Stage = "comments", BlobName = ctx.BlobName, LeaseId = ctx.LeaseId, Worker = ctx.Worker, Store = ctx.Store, Paths = ctx.Paths, NodeId = ctx.NodeId, Token = ctx.Token, ThreadShare = ctx.ThreadShare, Report = ctx.Report, Queue = ctx.Queue };
            try { await new CommentsStage(tools, store, gate).FetchAsync(cctx, false); }
            catch (Exception e) when (e is not OperationCanceledException and not BotBlocked) { Log.Info($"download {id}: comments not fetched ({Redact.Sas(e.Message)}); the video is stored without them"); }   // a missing comments snapshot never loses a download
        }
        if (QueueProcessing)
        {
            var p = JobDescriptor.NewProcess(id); p.Priority = ctx.Job.Priority; p.Steps = null;
            if (!ctx.FollowUps.Any(l => l.T == "add" && l.Stage == "process")) ctx.FollowUps.Add(QLine.Add(id, "process", p, Protocol.NowUtc()));
            Log.Info($"download {id}: process entry added to the queue");
        }
        else Log.Info($"download {id}: processing not queued (--no-process)");
    }
}
