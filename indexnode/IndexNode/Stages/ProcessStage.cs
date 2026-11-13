using System.Text.Json;

namespace IndexNode;

public sealed class ProcessStage(Tools tools, RestStore store, NodeConfig config, HttpClient http) : IStageHandler
{
    public async Task RunAsync(JobContext ctx)
    {
        var ct = ctx.Token;
        var job = ctx.Job; var id = job.JobId; var paths = ctx.Paths;
        var steps = job.Steps is { Length: > 0 } ? job.Steps : Protocol.DefaultSteps;
        // steps this node cannot run (transcribe: no speech model chosen yet) leave the job to a node that can
        var unknown = steps.Where(s => s is not ("frames" or "sscd" or "compress")).ToArray();
        if (unknown.Length > 0)
        {
            Log.Info($"process {id}: unknown step(s) {string.Join(',', unknown)}: skipping the job, released without an attempt");
            throw new JobSkipped("unknown step: " + string.Join(',', unknown));
        }
        var compress = job.Compress ?? new CompressSettings();
        var watch = System.Diagnostics.Stopwatch.StartNew();

        var listing = (await store.ListAsync(paths.VideoDir(id), false, false, ct)).ToDictionary(b => b.Name, b => b.Size);
        bool Has(string file) => listing.ContainsKey(paths.Video(id, file));
        if (Has("processed.json")) return;
        var downloaded = await store.GetAsync(paths.Video(id, "downloaded.json"), ct)
            ?? throw new InvalidOperationException($"{id} has no downloaded.json yet");
        double duration = JsonDocument.Parse(downloaded.Data).RootElement.TryGetProperty("durationSeconds", out var dEl) ? dEl.GetDouble() : 0;

        var need = new Dictionary<string, bool>
        {
            ["frames"] = steps.Contains("frames") && !Has("frames.json"),
            ["sscd"] = steps.Contains("sscd") && !Has("sscd.f16"),
            ["compress"] = steps.Contains("compress") && !Has("compact.mp4"),
        };
        // sscd works on frames, so frames are (re)extracted locally when only sscd is missing
        bool extractFrames = need["frames"] || need["sscd"];

        var work = Path.Combine(Path.GetTempPath(), "indexnode", "p-" + id);
        if (Directory.Exists(work)) Directory.Delete(work, true);
        Directory.CreateDirectory(work);
        try
        {
            var videoBlob = paths.Video(id, "video.mp4");
            var video = Path.Combine(work, "video.mp4");
            long originalBytes = 0;
            if (extractFrames || need["compress"])
            {
                ctx.Report("fetch", null);
                await NexisAccess.PrepareAsync(store, videoBlob, ct);
                using var keep = NexisAccess.KeepTouched(store, videoBlob);
                await store.DownloadToFileAsync(videoBlob, video, ct);
                originalBytes = new FileInfo(video).Length;
                if (duration <= 0) duration = await Frames.DurationAsync(tools, video, ct);
            }
            else
            {
                originalBytes = listing.GetValueOrDefault(videoBlob);
            }

            var framesDir = Path.Combine(work, "frames");
            List<FrameEntry>? frames = null;
            if (extractFrames)
            {
                ctx.Report("frames", null);
                int segs = ctx.ThreadShare();
                frames = await Frames.ExtractAsync(tools, video, work, framesDir, segs, duration, p => ctx.Report("frames", p), ct);
                await ctx.Store.PutAsync(paths.Video(id, "frames.json"), Frames.ToJson(frames), null, null, false, ct);
                Log.Info($"process {id}: {frames.Count} frames in {segs} segment(s)");
            }
            if (need["sscd"])
            {
                ctx.Report("sscd", null);
                var model = await Sscd.EnsureModelAsync(config, http, ct);
                var bytes = await Sscd.DescribeAsync(tools, model, framesDir, frames!.Count, ctx.ThreadShare(), p => ctx.Report("sscd", p), ct);
                await ctx.Store.PutAsync(paths.Video(id, "sscd.f16"), bytes, null, null, false, ct, "application/octet-stream");
            }
            long compactBytes = 0;
            if (need["compress"])
            {
                ctx.Report("compress", null);
                var compact = Path.Combine(work, "compact.mp4");
                await Compress.RunAsync(tools, video, compact, compress, ctx.ThreadShare(), duration, p => ctx.Report("compress", p), ct);
                compactBytes = new FileInfo(compact).Length;
                ctx.Report("compress", "uploading");
                await store.PutFileAsync(paths.Video(id, "compact.mp4"), compact, "video/mp4", null, ct);
            }
            else if (steps.Contains("compress"))
            {
                compactBytes = listing.GetValueOrDefault(paths.Video(id, "compact.mp4"));
            }

            int frameCount = frames?.Count ?? 0;
            if (frames == null && steps.Contains("frames"))
            {
                var fj = await store.GetAsync(paths.Video(id, "frames.json"), ct);
                if (fj != null) frameCount = JsonDocument.Parse(fj.Data).RootElement.GetArrayLength();
            }
            var marker = JsonSerializer.SerializeToUtf8Bytes(new
            {
                v = 1, steps, frames = frameCount, model = Sscd.ModelName, originalBytes, compactBytes,
                nodeId = ctx.NodeId, seconds = Math.Round(watch.Elapsed.TotalSeconds, 1), at = Protocol.NowUtc(),
            });
            await ctx.Store.PutAsync(paths.Video(id, "processed.json"), marker, null, null, false, ct);

            if (!compress.KeepOriginal && steps.Contains("compress") && (need["compress"] || compactBytes > 0))
            {
                await store.DeleteAsync(videoBlob, CancellationToken.None);
                Log.Info($"process {id}: deleted video.mp4 (keepOriginal=false)");
            }
        }
        finally
        {
            if (Environment.GetEnvironmentVariable("INDEXNODE_KEEP_WORK") == null)
            {
                try { Directory.Delete(work, true); } catch { }
            }
            else Log.Info($"process {id}: kept work folder {work}");
        }
    }
}
