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

public sealed class DownloadStage(Tools tools, IStore store, DownloadGate gate) : IStageHandler
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
        if (await ctx.Store.GetAsync(paths.Video(id, "downloaded.json"), ct) != null)
        {
            await EnsureFollowUpJobs(ctx);
            return;
        }
        if (string.IsNullOrEmpty(job.Url)) throw new PermanentFailure("job has no url");
        await gate.WaitTurnAsync(ct);

        var work = Path.Combine(Path.GetTempPath(), "indexnode", id);
        if (Directory.Exists(work)) Directory.Delete(work, true);
        Directory.CreateDirectory(work);
        try
        {
            var ffmpeg = await tools.FfmpegAsync(ct);
            var ffdir = Path.GetDirectoryName(ffmpeg)!;
            var common = new List<string> { "--no-playlist", "--ffmpeg-location", ffdir, "--retries", "3", "--no-warnings", "--newline" };
            var cookies = Environment.GetEnvironmentVariable("INDEXNODE_COOKIES");
            if (!string.IsNullOrEmpty(cookies)) { common.Add("--cookies"); common.Add(cookies); }

            ctx.Report("download", "starting");
            var main = new List<string>(common);
            main.AddRange(YtDlp.FormatArgs(job.MaxHeight ?? 480, job.Format));
            main.AddRange(new[] {
                "--merge-output-format", "mp4", "--write-info-json", "--write-thumbnail", "--convert-thumbnails", "jpg",
                "-o", Path.Combine(work, "video.%(ext)s"), job.Url,
            });
            var r = await tools.RunYtDlpAsync(main, ct, onStdout: line =>
            {
                var m = Regex.Match(line, @"\[download\]\s+(\d+(\.\d+)?)%");
                if (m.Success) ctx.Report("download", m.Groups[1].Value + "%");
            });
            if (r.ExitCode != 0) YtDlp.Throw(r.Stderr);

            string? manualDir = null, autoDir = null;
            if (job.Subs != false)
            {
                // exact tracks from the info JSON (never a regex: it would match every auto-translated track); two passes, because yt-dlp names uploaded and automatic subtitles alike (video.<lang>.vtt)
                var plan = SubLangs.Plan(File.Exists(Path.Combine(work, "video.info.json")) ? File.ReadAllText(Path.Combine(work, "video.info.json")) : null);
                foreach (var auto in new[] { false, true })
                {
                    var wanted = auto ? plan.AutoArg : plan.ManualArg;
                    if (wanted.Length == 0) { Log.Info($"download {id}: no {(auto ? "original-language automatic captions" : "uploaded subtitles")} to request"); continue; }
                    ctx.Report(auto ? "auto-subs" : "subs", null);
                    var dir = Path.Combine(work, auto ? "auto" : "manual");
                    Directory.CreateDirectory(dir);
                    var subs = new List<string>(common)
                    {
                        "--skip-download", auto ? "--write-auto-subs" : "--write-subs", "--sub-langs", wanted, "--sub-format", "vtt", "--convert-subs", "vtt",
                        "-o", Path.Combine(dir, "video.%(ext)s"), job.Url,
                    };
                    var sr = await tools.RunYtDlpAsync(subs, ct);
                    if (sr.ExitCode != 0)
                    {
                        // a missing or rate-limited subtitle track must not lose a downloaded video
                        if (!auto && YtDlp.Classify(sr.Stderr) == FailureKind.BotBlock) YtDlp.Throw(sr.Stderr);
                        Log.Info($"download {id}: {(auto ? "automatic captions" : "subtitles")} unavailable ({YtDlp.LastErrorLine(sr.Stderr)})");
                    }
                    if (auto) autoDir = dir; else manualDir = dir;
                }
            }

            var videoFile = Path.Combine(work, "video.mp4");
            if (!File.Exists(videoFile)) throw new InvalidOperationException("yt-dlp finished but video.mp4 was not produced");
            var infoFile = Path.Combine(work, "video.info.json");
            int height = 0; double duration = 0;
            DownloadedFormats? fmt = null;
            var descriptionFile = Path.Combine(work, "description.txt");
            if (File.Exists(infoFile))
            {
                var node = JsonNode.Parse(File.ReadAllText(infoFile))!.AsObject();
                File.WriteAllText(descriptionFile, (string?)node["description"] ?? "");
                node.Remove("comments");
                fmt = DownloadedFormats.FromInfo(node); // before the size trim below drops "formats"
                File.WriteAllText(infoFile, node.ToJsonString());
                height = (int?)node["height"] ?? 0; duration = (double?)node["duration"] ?? 0;
                if (new FileInfo(infoFile).Length > 1_500_000)
                {
                    foreach (var k in new[] { "formats", "automatic_captions", "thumbnails", "heatmap" }) node.Remove(k);
                    File.WriteAllText(infoFile, node.ToJsonString());
                }
            }
            if (duration <= 0) duration = await Frames.DurationAsync(tools, videoFile, ct);
            if (height <= 0) height = await Frames.HeightAsync(tools, videoFile, ct);

            var bytes = new FileInfo(videoFile).Length;
            Traffic.Current.Add(Traffic.YoutubeIn, Directory.EnumerateFiles(work, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length)); // video, subtitles, info, thumbnail
            var progress = new Progress<long>(n => ctx.Report("upload", $"{n * 100 / Math.Max(1, bytes)}%"));
            ctx.Report("upload", "0%");
            await store.PutFileAsync(paths.Video(id, "video.mp4"), videoFile, "video/mp4", progress, ct);
            if (File.Exists(infoFile)) await store.PutFileAsync(paths.Video(id, "info.json"), infoFile, "application/json", null, ct);
            var thumb = Path.Combine(work, "video.jpg");
            if (File.Exists(thumb)) await store.PutFileAsync(paths.Video(id, "thumb.jpg"), thumb, "image/jpeg", null, ct);
            if (File.Exists(descriptionFile)) await store.PutFileAsync(paths.Video(id, "description.txt"), descriptionFile, "text/plain; charset=utf-8", null, ct);
            var subFiles = CollectSubs(manualDir, autoDir);
            foreach (var (local, name) in subFiles)
            {
                var (vtt, stats) = ShapeSubs(await File.ReadAllTextAsync(local, ct), name.EndsWith(".auto", StringComparison.Ordinal));
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
            await ctx.Store.PutAsync(paths.Video(id, "downloaded.json"), marker, null, null, false, ct);
            await EnsureFollowUpJobs(ctx);
        }
        finally
        {
            try { Directory.Delete(work, true); } catch { }
        }
    }

    /// <summary>The process job and the first comments job (due at once), each only where it does not exist yet.</summary>
    internal async Task EnsureFollowUpJobs(JobContext ctx)
    {
        var c = JobDescriptor.NewComments(ctx.Job.JobId, ctx.Job.Url ?? "https://www.youtube.com/watch?v=" + ctx.Job.JobId);
        c.Priority = ctx.Job.Priority;
        var made = await ctx.Store.PutAsync(ctx.Paths.Job("comments", ctx.Job.JobId), c.ToBytes(), Tags.Job("comments", "pending", c.AddedAt, null, c.Priority), null, true, ctx.Token);
        Log.Info($"download {ctx.Job.JobId}: comments job {(made ? "created" : "already exists")}");
        if (QueueProcessing) await EnsureProcessJob(ctx);
        else Log.Info($"download {ctx.Job.JobId}: processing not queued (--no-process)");
    }

    private static async Task EnsureProcessJob(JobContext ctx)
    {
        // one job per step; the dependent ones (sync, describe) carry "needs" and wait for those outputs
        var made = new List<string>();
        foreach (var step in Protocol.IngestStepStages)
        {
            var p = JobDescriptor.NewStep(step, ctx.Job.JobId); p.Priority = ctx.Job.Priority;
            if (await ctx.Store.PutAsync(ctx.Paths.Job(step, ctx.Job.JobId), p.ToBytes(), Tags.Job(step, "pending", p.AddedAt, null, p.Priority), null, true, ctx.Token)) made.Add(step);
        }
        Log.Info($"download {ctx.Job.JobId}: step jobs created: {(made.Count == 0 ? "none (all exist)" : string.Join(',', made))}");
    }
}
