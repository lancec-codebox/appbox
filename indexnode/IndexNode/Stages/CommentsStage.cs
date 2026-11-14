using System.Text.Json;
using System.Text.Json.Nodes;

namespace IndexNode;

/// <summary>
/// No continuous watching: one round at download, then done. At most one follow-up, and only when the name vote leaves a LEAD unconfirmed
/// (the AI step rewrites the job once with round 1, reason "lead-names", due the download + 7 days); after that round the job is done.
/// Supporting-cast names never ask for comments, and sequels come from the followed channel, not from comments.
/// </summary>
public static class CommentsFollowUp
{
    public const string LeadNames = "lead-names";
    public static readonly TimeSpan Delay = TimeSpan.FromDays(7);

    /// <summary>The job for the one follow-up round, or null (none wanted, or this job is already its follow-up).</summary>
    public static (int Round, string Reason, DateTimeOffset Due)? Plan(DateTimeOffset downloadedAt, int completedRound, bool leadNameUnconfirmed) =>
        leadNameUnconfirmed && completedRound == 0 ? (1, LeadNames, downloadedAt + Delay) : null;

    /// <summary>Rewrites the finished comments job as pending with round 1 and reason "lead-names", due download + 7 days; false when not allowed.</summary>
    public static async Task<bool> RequestAsync(IStore store, Paths paths, string jobId, DateTimeOffset downloadedAt, bool leadNameUnconfirmed, CancellationToken ct)
    {
        var name = paths.Job("comments", jobId);
        var blob = await store.GetAsync(name, ct);
        if (blob == null) return false;
        var job = JobDescriptor.Parse(blob.Data);
        if (Plan(downloadedAt, job.Round ?? 0, leadNameUnconfirmed) is not { } p) return false;
        job.Round = p.Round; job.Reason = p.Reason; job.Attempts = 0;
        var added = (await store.GetTagsAsync(name, ct))?.GetValueOrDefault(Tags.Added) ?? job.AddedAt;
        await store.PutAsync(name, job.ToBytes(), Tags.Job("comments", "pending", added, Protocol.Utc(p.Due)), null, false, ct);
        return true;
    }
}

public static class CommentsSnapshot
{
    /// <summary>One snapshot from the two passes (top, then newest): comments are merged by id, top first.</summary>
    public static JsonObject Build(params JsonObject?[] passes)
    {
        var seen = new HashSet<string>();
        var list = new JsonArray();
        long? total = null;
        foreach (var info in passes)
        {
            if (info == null) continue;
            total ??= (long?)info["comment_count"];
            if (info["comments"] is not JsonArray arr) continue;
            foreach (var c in arr.OfType<JsonObject>())
            {
                var id = (string?)c["id"] ?? "";
                if (id.Length == 0 || !seen.Add(id)) continue;
                list.Add(new JsonObject
                {
                    ["id"] = id,
                    ["parent"] = (string?)c["parent"] ?? "root",
                    ["author"] = (string?)c["author"],
                    ["authorId"] = (string?)c["author_id"],
                    ["text"] = (string?)c["text"],
                    ["likes"] = (long?)c["like_count"] ?? 0,
                    ["timestamp"] = (long?)c["timestamp"],
                    ["pinned"] = (bool?)c["is_pinned"] ?? false,
                    ["byUploader"] = (bool?)c["author_is_uploader"] ?? false,
                });
            }
        }
        return new JsonObject { ["v"] = 1, ["count"] = list.Count, ["commentCount"] = total, ["comments"] = list };
    }

    public static string BlobTimestamp(DateTimeOffset t) => t.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'");

    public static string[] Args(string url, string outTemplate, int max, string sort) =>
    [
        "--no-playlist", "--no-warnings", "--skip-download", "--write-comments", "--write-info-json", "--retries", "3",
        "--extractor-args", $"youtube:max_comments={max},all,all,all;comment_sort={sort}",
        "-o", outTemplate, url,
    ];
}

/// <summary>Takes a dated comments snapshot (no cookies) and schedules the next refresh.</summary>
public sealed class CommentsStage(Tools tools, IStore store, DownloadGate gate) : IStageHandler
{
    public async Task RunAsync(JobContext ctx)
    {
        var ct = ctx.Token; var job = ctx.Job; var id = job.JobId; var paths = ctx.Paths;
        if (string.IsNullOrEmpty(job.Url)) throw new PermanentFailure("job has no url");
        var marker = await ctx.Store.GetAsync(paths.Video(id, "downloaded.json"), ct);
        var downloadedAt = DateTimeOffset.TryParse(marker == null ? null : JsonDocument.Parse(marker.Data).RootElement.TryGetProperty("at", out var at) ? at.GetString() : null, out var d)
            ? d : DateTimeOffset.TryParse(job.AddedAt, out var added) ? added : DateTimeOffset.UtcNow;
        int round = job.Round ?? 0;
        int max = job.MaxComments ?? 1000;

        await gate.WaitTurnAsync(ct);
        var work = Path.Combine(Path.GetTempPath(), "indexnode", "c-" + id);
        if (Directory.Exists(work)) Directory.Delete(work, true);
        Directory.CreateDirectory(work);
        try
        {
            var infos = new List<JsonObject?>();
            foreach (var sort in new[] { job.Sort ?? "top", "new" }.Distinct())
            {
                ctx.Report("comments", sort);
                var r = await tools.RunYtDlpAsync(CommentsSnapshot.Args(job.Url, Path.Combine(work, sort + ".%(ext)s"), max, sort), ct);
                if (r.ExitCode != 0) YtDlp.Throw(r.Stderr);
                var f = Path.Combine(work, sort + ".info.json");
                infos.Add(File.Exists(f) ? JsonNode.Parse(File.ReadAllText(f))!.AsObject() : null);
            }
            Traffic.Current.Add(Traffic.YoutubeIn, Directory.EnumerateFiles(work).Sum(x => new FileInfo(x).Length));
            var snap = CommentsSnapshot.Build(infos.ToArray());
            var name = paths.Video(id, $"comments/{CommentsSnapshot.BlobTimestamp(DateTimeOffset.UtcNow)}.json");
            await ctx.Store.PutAsync(name, JsonSerializer.SerializeToUtf8Bytes(snap), null, null, false, ct);
            Log.Info($"comments {id}: snapshot with {(int)snap["count"]!} comments");

            // one round, then done: a follow-up is requested only by the AI step (CommentsFollowUp.RequestAsync)
        }
        finally { try { Directory.Delete(work, true); } catch { } }
    }
}
