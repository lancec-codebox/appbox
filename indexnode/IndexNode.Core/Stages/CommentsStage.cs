using System.Text.Json;
using System.Text.Json.Nodes;

namespace IndexNode;

/// <summary>0.13.0 (owner decision): comments are part of the download, fetched by the download claim itself (<see cref="CommentsStage.FetchAsync"/>); there is no comments stage on the queue, no comments job and no refresh rounds.</summary>
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

/// <summary>Takes a dated comments snapshot (no cookies): top comments, the same limits and blob names as before 0.13.0. Run by the download stage as part of the download.</summary>
public sealed class CommentsStage(ITools tools, IStore store, DownloadGate gate) : IStageHandler
{
    public Task RunAsync(JobContext ctx) => FetchAsync(ctx, true);

    /// <summary>One comments snapshot for the video of <paramref name="ctx"/>. <paramref name="useGate"/> false: the caller (the download) has just waited its turn at YouTube.</summary>
    public async Task FetchAsync(JobContext ctx, bool useGate)
    {
        var ct = ctx.Token; var job = ctx.Job; var id = job.JobId; var paths = ctx.Paths;
        if (string.IsNullOrEmpty(job.Url)) throw new PermanentFailure("job has no url");
        var marker = await ctx.Store.GetAsync(paths.Video(id, "downloaded.json"), ct);
        var downloadedAt = DateTimeOffset.TryParse(marker == null ? null : StoredJson.Doc(marker.Data, $"{id}/downloaded.json").RootElement.TryGetProperty("at", out var at) ? at.GetString() : null, out var d)
            ? d : DateTimeOffset.TryParse(job.AddedAt, out var added) ? added : DateTimeOffset.UtcNow;
        int round = job.Round ?? 0;
        int max = job.MaxComments ?? 1000;

        if (useGate) await gate.WaitTurnAsync(ct);
        await using var work = await tools.CreateWorkspaceAsync("c-" + id, ct);
        {
            var infos = new List<JsonObject?>();
            foreach (var sort in new[] { job.Sort ?? "top", "new" }.Distinct())
            {
                ctx.Report("comments", sort);
                var r = await tools.RunAsync(work, "yt-dlp", CommentsSnapshot.Args(job.Url, sort + ".%(ext)s", max, sort), ct);
                if (r.ExitCode != 0) YtDlp.Throw(r.Stderr);
                var f = sort + ".info.json";
                infos.Add(await work.ExistsAsync(f, ct) ? JsonNode.Parse(await work.ReadTextAsync(f, ct))!.AsObject() : null);
            }
            Traffic.Current.Add(Traffic.YoutubeIn, await work.TotalBytesAsync(ct));
            var snap = CommentsSnapshot.Build(infos.ToArray());
            var name = paths.Video(id, $"comments/{CommentsSnapshot.BlobTimestamp(DateTimeOffset.UtcNow)}.json");
            await ctx.Store.PutAsync(name, JsonSerializer.SerializeToUtf8Bytes(snap), null, null, false, ct);
            await StageCommit.CommitAsync(ctx.Store, id, "comments", ctx.NodeId, true, ct);   // rounds add to comments.zip
            Log.Info($"comments {id}: snapshot with {(int)snap["count"]!} comments");

            // one round, then done: a follow-up is requested only by the AI step (CommentsFollowUp.RequestAsync)
        }
    }
}
