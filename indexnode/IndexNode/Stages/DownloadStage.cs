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
    /// <summary>English (en, en-US, ...) and Chinese uploaded subtitles; zh-orig is the original-language automatic track.</summary>
    public const string SubLangs = "en.*,zh,zh-Hans,zh-Hant,zh-CN,zh-TW,zh-HK,zh-orig";
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
            var main = new List<string>(common)
            {
                "-f", YtDlp.Selector(job.MaxHeight ?? 480, job.Format), "--merge-output-format", "mp4",
                "--write-info-json", "--write-thumbnail", "--convert-thumbnails", "jpg",
                "-o", Path.Combine(work, "video.%(ext)s"), job.Url,
            };
            var r = await tools.RunYtDlpAsync(main, ct, onStdout: line =>
            {
                var m = Regex.Match(line, @"\[download\]\s+(\d+(\.\d+)?)%");
                if (m.Success) ctx.Report("download", m.Groups[1].Value + "%");
            });
            if (r.ExitCode != 0) YtDlp.Throw(r.Stderr);

            string? manualDir = null, autoDir = null;
            if (job.Subs != false)
            {
                // two passes, because yt-dlp names uploaded and automatic subtitles alike (video.<lang>.vtt)
                foreach (var auto in new[] { false, true })
                {
                    ctx.Report(auto ? "auto-subs" : "subs", null);
                    var dir = Path.Combine(work, auto ? "auto" : "manual");
                    Directory.CreateDirectory(dir);
                    var subs = new List<string>(common)
                    {
                        "--skip-download", auto ? "--write-auto-subs" : "--write-subs", "--sub-langs", SubLangs, "--sub-format", "vtt", "--convert-subs", "vtt",
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
            var descriptionFile = Path.Combine(work, "description.txt");
            if (File.Exists(infoFile))
            {
                var node = JsonNode.Parse(File.ReadAllText(infoFile))!.AsObject();
                File.WriteAllText(descriptionFile, (string?)node["description"] ?? "");
                node.Remove("comments");
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
            var progress = new Progress<long>(n => ctx.Report("upload", $"{n * 100 / Math.Max(1, bytes)}%"));
            ctx.Report("upload", "0%");
            await store.PutFileAsync(paths.Video(id, "video.mp4"), videoFile, "video/mp4", progress, ct);
            if (File.Exists(infoFile)) await store.PutFileAsync(paths.Video(id, "info.json"), infoFile, "application/json", null, ct);
            var thumb = Path.Combine(work, "video.jpg");
            if (File.Exists(thumb)) await store.PutFileAsync(paths.Video(id, "thumb.jpg"), thumb, "image/jpeg", null, ct);
            if (File.Exists(descriptionFile)) await store.PutFileAsync(paths.Video(id, "description.txt"), descriptionFile, "text/plain; charset=utf-8", null, ct);
            var subFiles = CollectSubs(manualDir, autoDir);
            foreach (var (local, name) in subFiles)
                await store.PutFileAsync(paths.Video(id, $"subs/{name}.vtt"), local, "text/vtt", null, ct);

            var marker = JsonSerializer.SerializeToUtf8Bytes(new { v = 1, bytes, height, durationSeconds = Math.Round(duration, 2), subs = subFiles.Select(s => s.Name).ToArray(), nodeId = ctx.NodeId, at = Protocol.NowUtc() });
            await ctx.Store.PutAsync(paths.Video(id, "downloaded.json"), marker, null, null, false, ct);
            await EnsureFollowUpJobs(ctx);
        }
        finally
        {
            try { Directory.Delete(work, true); } catch { }
        }
    }

    /// <summary>The process job and the first comments job (due at once), each only where it does not exist yet.</summary>
    private static async Task EnsureFollowUpJobs(JobContext ctx)
    {
        var c = JobDescriptor.NewComments(ctx.Job.JobId, ctx.Job.Url ?? "https://www.youtube.com/watch?v=" + ctx.Job.JobId);
        var made = await ctx.Store.PutAsync(ctx.Paths.Job("comments", ctx.Job.JobId), c.ToBytes(), Tags.Job("comments", "pending", c.AddedAt), null, true, ctx.Token);
        Log.Info($"download {ctx.Job.JobId}: comments job {(made ? "created" : "already exists")}");
        await EnsureProcessJob(ctx);
    }

    private static async Task EnsureProcessJob(JobContext ctx)
    {
        var p = JobDescriptor.NewProcess(ctx.Job.JobId);
        var created = await ctx.Store.PutAsync(ctx.Paths.Job("process", ctx.Job.JobId), p.ToBytes(), Tags.Job("process", "pending", p.AddedAt), null, true, ctx.Token);
        Log.Info($"download {ctx.Job.JobId}: process job {(created ? "created" : "already exists")}");
    }
}
