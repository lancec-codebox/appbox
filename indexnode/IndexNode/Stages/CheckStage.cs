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
        // 0.13.18: one LIST of the video's folder answers every "is it stored" below (sample.json, video.mp4, downloaded.json, download.zip were a HEAD each)
        var have = (await store.ListAsync(paths.VideoDir(id), false, false, ct)).Select(b => b.Name[(b.Name.LastIndexOf('/') + 1)..]).ToHashSet(StringComparer.Ordinal);
        JsonObject? rec = vj?.Root["precheck"] as JsonObject;
        if (rec == null)
        {
            PreCheckStage.Run? run = null;
            bool sample = await Exists(store, paths, id, have, SampleStage.Dir + "sample.json", ct);
            if (sample) run = await pre.AnalyzeSampleAsync(ctx, null, ct, strict: true);
            else if (have.Contains("video.mp4"))
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
        bool stored = have.Contains("downloaded.json") || have.Contains("download.zip");
        if (!stored)
        {
            var d = new JobDescriptor { Stage = "download", JobId = id, Url = ctx.Job.Url ?? "https://www.youtube.com/watch?v=" + id, MaxHeight = ctx.Job.MaxHeight, Format = ctx.Job.Format, Subs = ctx.Job.Subs, MaxComments = ctx.Job.MaxComments, Priority = ctx.Job.Priority, AddedAt = Protocol.NowUtc() };
            await PubTime.StampAsync(store, paths, id, ctx.Job, d, ct);
            if (!ctx.FollowUps.Any(l => l.T == "add" && l.Stage == "download")) { var dl = QLine.Add(id, "download", d, Protocol.NowUtc()); AiHold.Apply(ctx, rec, dl); ctx.FollowUps.Add(dl); }
        }
        else if (AiHold.Flagged(rec) && !ctx.AiDramasOn()) { var sc = (double?)(rec["ai"] as JsonObject)?["score"]; ctx.FollowUps.Add(new QLine { T = "hold", S = id, Stage = "process", Hold = "ai", AiScore = sc, At = Protocol.NowUtc() }); Log.Info($"sample {id}: check {(string?)rec["decision"]}, AI (score {sc:0.000}): held, aiDramas is off"); }
        Log.Info($"check {id}: {(string?)rec["decision"]}; {(stored ? "the stored video goes on to process" : "the full download is queued")}");
    }

    /// <summary>A file of the video's folder (the sample folder is a sub-folder: one HEAD for it, only when the listing does not show it).</summary>
    static async Task<bool> Exists(IStore store, Paths paths, string id, HashSet<string> have, string rel, CancellationToken ct) =>
        have.Contains(rel[(rel.LastIndexOf('/') + 1)..]) || rel.Contains('/') && await store.HeadAsync(paths.Video(id, rel), ct) != null;

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
