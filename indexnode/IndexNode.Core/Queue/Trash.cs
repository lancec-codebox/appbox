namespace IndexNode;

/// <summary>
/// 0.13.0: nothing deletes upload data on its own. A pre-check skip only marks the video (`trash: download.zip/video` on the done line); this tool, run by hand, is the one thing that removes it.
/// The video is a pass-through blob (video.mp4, and compact.mp4 when one exists): download.zip never holds it, so removing it leaves download.zip byte-identical (no rewrite is needed). It then appends a "deleted" line.
/// </summary>
public static class Trash
{
    public sealed record Item(string Subject, string Stage, string Mark, string? Why, string? MarkedAt, string? DeletedAt, string? DuplicateOf, double? Share);

    /// <summary>What the queue has marked, oldest first.</summary>
    public static List<Item> List(QueueState s) =>
        s.Subjects.Values.SelectMany(sub => sub.Entries.Values.Where(e => e.Trash != null).Select(e => new Item(sub.Id, e.Stage, e.Trash!, e.TrashWhy, e.FinishedAt, e.TrashDeletedAt, e.Job?.DuplicateOf, e.Job?.DuplicateShare)))
            .OrderBy(i => i.MarkedAt, StringComparer.Ordinal).ThenBy(i => i.Subject, StringComparer.Ordinal).ToList();

    /// <summary>"30d", "12h", "90m", "2w" (a bare number is days).</summary>
    public static TimeSpan ParseAge(string v)
    {
        v = v.Trim(); if (v.Length == 0) throw new ArgumentException("--older-than needs an age such as 30d, 12h or 2w");
        char u = char.ToLowerInvariant(v[^1]); var num = char.IsDigit(u) ? v : v[..^1]; if (char.IsDigit(u)) u = 'd';
        if (!double.TryParse(num, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var n) || n < 0) throw new ArgumentException($"not an age: {v}");
        return u switch { 'd' => TimeSpan.FromDays(n), 'h' => TimeSpan.FromHours(n), 'm' => TimeSpan.FromMinutes(n), 'w' => TimeSpan.FromDays(7 * n), _ => throw new ArgumentException($"unknown age unit in {v} (use m, h, d or w)") };
    }

    /// <summary>The marks older than <paramref name="olderThan"/> that have not been deleted. <paramref name="dryRun"/> writes nothing (no delete, no line). Returns what was (or would be) removed.</summary>
    public static async Task<List<(Item Item, string[] Blobs)>> EmptyAsync(JobQueue queue, IStore store, Paths paths, TimeSpan olderThan, bool dryRun, CancellationToken ct)
    {
        await queue.PollAsync(ct);
        var cut = Protocol.Utc(queue.Now - olderThan);
        var due = queue.Read(List).Where(i => i.DeletedAt == null && string.CompareOrdinal(i.MarkedAt ?? "", cut) <= 0).ToList();
        var res = new List<(Item, string[])>();
        foreach (var it in due)
        {
            var found = new List<string>();
            foreach (var v in new[] { "video.mp4", "compact.mp4" }) if (await store.HeadAsync(paths.Video(it.Subject, v), ct) != null) found.Add(paths.Video(it.Subject, v));
            res.Add((it, found.ToArray()));
            if (dryRun) continue;
            foreach (var b in found) await store.DeleteAsync(b, ct);
            await queue.AppendLinesAsync([new QLine { T = "deleted", S = it.Subject, Stage = it.Stage, Trash = it.Mark }], ct);
        }
        return res;
    }
}
