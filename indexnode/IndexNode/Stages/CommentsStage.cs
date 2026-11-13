using System.Text.Json;
using System.Text.Json.Nodes;

namespace IndexNode;

/// <summary>Refresh schedule of a video's comments: 1 d, 3 d, 7 d and 30 d after the download, then every 30 d until a year has passed.</summary>
public static class CommentsSchedule
{
    public static readonly TimeSpan[] First = [TimeSpan.FromDays(1), TimeSpan.FromDays(3), TimeSpan.FromDays(7), TimeSpan.FromDays(30)];
    public static readonly TimeSpan Horizon = TimeSpan.FromDays(365);

    /// <summary>Offset after the download at which round <paramref name="round"/> is due (round 0 is at once, after the download).</summary>
    public static TimeSpan Offset(int round) => round <= 0 ? TimeSpan.Zero : round <= First.Length ? First[round - 1] : TimeSpan.FromDays(30 * (round - First.Length + 1));

    /// <summary>
    /// The round that follows <paramref name="completedRound"/> and when it is due; null when the year is over. Rounds whose time
    /// has already passed (a node that was away) are skipped, so a late node does not run them back to back.
    /// </summary>
    public static (int Round, DateTimeOffset Due)? Next(DateTimeOffset downloadedAt, int completedRound, DateTimeOffset now)
    {
        var round = completedRound + 1;
        while (true)
        {
            var off = Offset(round);
            if (off > Horizon) return null;
            var due = downloadedAt + off;
            if (due > now) return (round, due);
            round++;
        }
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
            var snap = CommentsSnapshot.Build(infos.ToArray());
            var name = paths.Video(id, $"comments/{CommentsSnapshot.BlobTimestamp(DateTimeOffset.UtcNow)}.json");
            await ctx.Store.PutAsync(name, JsonSerializer.SerializeToUtf8Bytes(snap), null, null, false, ct);
            Log.Info($"comments {id}: snapshot with {(int)snap["count"]!} comments");

            var next = CommentsSchedule.Next(downloadedAt, round, DateTimeOffset.UtcNow);
            if (next is { } n) { job.Round = n.Round; ctx.NextDue = Protocol.Utc(n.Due); }
        }
        finally { try { Directory.Delete(work, true); } catch { } }
    }
}
