using System.Text.Json.Nodes;

namespace IndexNode;

/// <summary>
/// 0.13.16: the `check` queue stage (any processor node, no YouTube). The 0.13.15 two-segment decision on the sample files the `sample` stage uploaded, or, for a drama whose full video is already stored
/// (process pending, no check record), on two segments cut out of the stored video.mp4 with stream copy. The record goes into video.json.
/// duplicate / subset: throws <see cref="DuplicateFound"/> (the engine ends the subject metadata-only; nothing is deleted, no full download is added).
/// anything else: a same-priority full `download` entry is added (for a sample), or the already waiting process entry goes on; process then runs without a second pre-check.
/// </summary>
public sealed class CheckStage(PreCheckStage pre) : IStageHandler
{
    public async Task RunAsync(JobContext ctx)
    {
        var ct = ctx.Token; var store = ctx.Store; var paths = ctx.Paths; var id = ctx.Job.JobId;
        var vj = await VideoJson.ReadAsync(store, paths, id, ct);
        JsonObject? rec = vj?.Root["precheck"] as JsonObject;
        if (rec == null)
        {
            PreCheckStage.Run? run = null;
            bool sample = await store.HeadAsync(paths.Video(id, SampleStage.Dir + "sample.json"), ct) != null;
            if (sample) run = await pre.AnalyzeSampleAsync(ctx, null, ct, strict: true);
            else if (await store.HeadAsync(paths.Video(id, "video.mp4"), ct) != null)
            {
                if (vj == null) throw new VideoJsonMissingException(id);
                run = await pre.AnalyzeAsync(ctx, null, ct, strict: true);
            }
            else throw new PermanentFailure($"check: neither {paths.Video(id, SampleStage.Dir + "sample.json")} nor a stored video.mp4 exists for {id}");
            rec = run!.Record;
            await PreCheckStage.RecordAsync(store, paths, id, rec, ct);
        }
        if (PreCheckDecision.IsSkip((string?)rec["decision"])) throw Duplicate(ctx, rec);
        // not a copy: the full download follows (a stored video goes straight to its process entry)
        bool stored = await store.HeadAsync(paths.Video(id, "downloaded.json"), ct) != null || await store.HeadAsync(paths.Video(id, "download.zip"), ct) != null;
        if (!stored)
        {
            var d = new JobDescriptor { Stage = "download", JobId = id, Url = ctx.Job.Url ?? "https://www.youtube.com/watch?v=" + id, MaxHeight = ctx.Job.MaxHeight, Format = ctx.Job.Format, Subs = ctx.Job.Subs, MaxComments = ctx.Job.MaxComments, Priority = ctx.Job.Priority, AddedAt = Protocol.NowUtc() };
            await PubTime.StampAsync(store, paths, id, ctx.Job, d, ct);
            if (!ctx.FollowUps.Any(l => l.T == "add" && l.Stage == "download")) ctx.FollowUps.Add(QLine.Add(id, "download", d, Protocol.NowUtc()));
        }
        Log.Info($"check {id}: {(string?)rec["decision"]}; {(stored ? "the stored video goes on to process" : "the full download is queued")}");
    }

    static DuplicateFound Duplicate(JobContext ctx, JsonObject rec)
    {
        var top = (rec["top"] as JsonArray)?.OfType<JsonObject>().FirstOrDefault();
        var upload = (string?)rec["matchUpload"] ?? (string?)top?["upload"] ?? "?"; var share = (double?)top?["share"] ?? 0;
        var sp = rec["speech"] as JsonObject;
        var why = string.Join("; ", (rec["reasons"] as JsonArray)?.Select(r => r?.ToString()) ?? []);
        Log.Info($"check {ctx.Job.JobId}: skip ({(string?)rec["decision"]} of {upload}): nothing is deleted or downloaded");
        return new DuplicateFound(upload, share, (string?)sp?["speechLanguage"], (bool?)sp?["dubbed"], (string?)sp?["languageSource"], (string?)sp?["metadataLanguage"], ctx.Job.Url) { Why = why, Group = (string?)rec["groupId"], GroupOffset = (double?)rec["groupOffset"], MatchShare = (double?)rec["matchShare"], DupShare = (double?)rec["dupShare"], Mixed = (bool?)(rec["group"] as JsonObject)?["mixed"] is true ? true : null, AiScore = (double?)(rec["ai"] as JsonObject)?["score"] };
    }
}
