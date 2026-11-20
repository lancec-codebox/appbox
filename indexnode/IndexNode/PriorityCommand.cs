using System.Globalization;

namespace IndexNode;

/// <summary>Job priority commands: <c>priority set</c> re-tags pending jobs, <c>priority list</c> shows the claim order.</summary>
public static class PriorityTool
{
    public sealed record SetResult(int Changed, Dictionary<string, int> Skipped);

    /// <summary>The --priority option: an integer in -1000..1000, default 0.</summary>
    public static int ParseOption(string? v) =>
        v == null ? 0 : int.TryParse(v, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var p) ? Tags.CheckPriority(p) : throw new ArgumentException($"--priority needs an integer, got '{v}'");

    /// <summary>A source (channel/playlist id or URL) as a watch job id, or a single video URL as "v-&lt;videoId&gt;".</summary>
    public static string SourceOrVideoId(string what) =>
        what.StartsWith("v-") ? what : YtDlp.IsSingleVideoUrl(what) ? "v-" + Protocol.JobIdFor(what) : SourceId(what);

    /// <summary>The watch job id for a channel id (UC...), a channel/playlist URL or an id that already is one.</summary>
    public static string SourceId(string channel)
    {
        if (channel.StartsWith("ch-") || channel.StartsWith("pl-")) return channel;
        if (Sources.Classify(channel) is { } k) return (k.Kind == "playlist" ? "pl-" : "ch-") + k.Id;
        return "ch-" + channel;
    }

    /// <summary>The still-pending videos a source created: their download entries carry Source = the source id (0.13.0: read from the queue's memory).</summary>
    public static List<string> JobsOfSource(JobQueue queue, string stage, string sourceId) =>
        queue.Read(st => st.Subjects.Values.Where(s => s.Entries.TryGetValue(stage, out var e) && e.State == "pending" && e.Job?.Source == sourceId).Select(s => s.Id).OrderBy(x => x, StringComparer.Ordinal).ToList());

    /// <summary>
    /// Sets the priority of the subjects named: one "pri" line in the log, which changes every entry of the subject that is not finished (download, comments, process, describe, translate), so a video's stages keep one priority.
    /// Finished and unknown subjects are skipped and counted by reason.
    /// </summary>
    public static async Task<SetResult> SetAsync(JobQueue queue, int priority, IEnumerable<string> subjects, CancellationToken ct)
    {
        Tags.CheckPriority(priority);
        await queue.PollAsync(ct);
        int changed = 0; var skipped = new Dictionary<string, int>();
        void Skip(string why) => skipped[why] = skipped.GetValueOrDefault(why) + 1;
        var lines = new List<QLine>();
        foreach (var id in subjects.Distinct())
        {
            var s = queue.Read(st => st.Get(id));
            if (s == null) { Skip("no such job"); continue; }
            if (!s.Entries.Values.Any(e => !e.Final || e.Stage == "refresh")) { Skip(s.Entries.Values.Any(e => e.State == "failed") ? "failed" : "done"); continue; }
            lines.Add(new QLine { T = "pri", S = id, Pri = priority }); changed++;
        }
        if (lines.Count > 0) await queue.AppendLinesAsync(lines, ct);
        return new SetResult(changed, skipped);
    }

    /// <summary>The priority of a video: the highest of its own priority (added directly) and its sources' priorities; 0 when it has none.</summary>
    public static int Effective(JobDescriptor download, IReadOnlyDictionary<string, int> sourcePriority)
    {
        var all = new List<int>();
        if (download.OwnPriority is { } own) all.Add(own);
        foreach (var s in download.Sources ?? (download.Source != null ? [download.Source] : [])) if (sourcePriority.TryGetValue(s, out var p)) all.Add(p);
        return all.Count == 0 ? 0 : all.Max();
    }

    public sealed record SourceResult(int Videos, int Changed, Dictionary<string, int> Skipped, int Matched);

    /// <summary>
    /// Sets a source's priority and the priority of every video that came from it, as "priority set" does. A video's priority is the highest of its own and its sources' priorities.
    /// <paramref name="listedIds"/> are the video ids a yt-dlp listing of the source gave: videos queued before download entries recorded their source are matched by them and get the source recorded.
    /// A direct video (id "v-&lt;videoId&gt;") has no source record: its own priority is set.
    /// </summary>
    public static async Task<SourceResult> SetSourceAsync(JobQueue queue, string sourceId, int priority, IReadOnlyCollection<string>? listedIds, CancellationToken ct)
    {
        Tags.CheckPriority(priority);
        var direct = sourceId.StartsWith("v-", StringComparison.Ordinal);
        await queue.PollAsync(ct);
        var prios = queue.Read(st => st.Subjects.Values.Where(s => s.Entries.ContainsKey("refresh")).ToDictionary(s => s.Id, s => s.Entries["refresh"].Priority));
        var skipped = new Dictionary<string, int>(); int changed = 0, matched = 0;
        var lines = new List<QLine>();
        if (!direct)
        {
            if (!prios.ContainsKey(sourceId)) throw new ArgumentException($"no source {sourceId}");
            lines.Add(new QLine { T = "pri", S = sourceId, Pri = priority }); changed++; prios[sourceId] = priority;
        }
        var listed = listedIds?.ToHashSet() ?? [];
        var videos = new List<(string Id, JobDescriptor Dl)>();
        foreach (var (id, e) in queue.Read(st => st.Subjects.Values.Where(s => s.Entries.ContainsKey("download")).Select(s => (s.Id, s.Entries["download"])).ToList()))
        {
            if (direct && id != sourceId[2..]) continue;
            var dl = e.Job ?? new JobDescriptor { JobId = id };
            bool recorded = (dl.Sources ?? []).Contains(sourceId) || dl.Source == sourceId;
            if (!direct && !recorded && !listed.Contains(id)) continue;
            if (direct) dl.OwnPriority = priority;
            else if (!recorded) { dl.Sources = (dl.Sources ?? (dl.Source != null ? [dl.Source] : [])).Append(sourceId).Distinct().ToList(); matched++; }
            if (direct || !recorded) lines.Add(new QLine { T = "upd", S = id, Stage = "download", Job = dl });
            videos.Add((id, dl));
        }
        foreach (var (id, dl) in videos) lines.Add(new QLine { T = "pri", S = id, Pri = Effective(dl, prios) });
        if (lines.Count > 0) await queue.AppendLinesAsync(lines, ct);
        changed += videos.Count;
        return new SourceResult(videos.Count, changed, skipped, matched);
    }

    /// <summary>Video ids of a source's listing (yt-dlp --flat-playlist; needs a machine YouTube allows, e.g. a residential one).</summary>
    public static async Task<List<string>> ListIdsAsync(Tools tools, JobDescriptor watch, CancellationToken ct)
    {
        var ids = new List<string>();
        var urls = watch.Kind == "playlist" ? [$"https://www.youtube.com/playlist?list={watch.Id}"] : (watch.Tabs is { Length: > 0 } t ? t : ["videos"]).Select(x => $"https://www.youtube.com/channel/{watch.Id}/{x}").ToArray();
        foreach (var u in urls)
        {
            var r = await tools.RunYtDlpAsync(["--flat-playlist", "--no-warnings", "--print", "%(id)s", u], ct);
            if (r.ExitCode != 0) throw new InvalidOperationException(YtDlp.LastErrorLine(r.Stderr));
            ids.AddRange(r.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        }
        return ids;
    }

    /// <summary>Pending entries in the order nodes claim them: priority, added, subject.</summary>
    public static List<(string Stage, string Id, int Priority, string Added)> List(JobQueue queue, string? stage, int top) =>
        queue.Read(st => st.Subjects.Values.SelectMany(s => s.Entries.Values.Where(e => e.State == "pending" && (stage == null || e.Stage == stage)).Select(e => (e.Stage, s.Id, e.Priority, e.Added))).ToList())
            .OrderByDescending(x => x.Priority).ThenBy(x => x.Added, StringComparer.Ordinal).ThenBy(x => x.Id, StringComparer.Ordinal).Take(top).ToList();

    public static async Task<int> RunAsync(Args a, JobQueue queue)
    {
        var sub = a.Positional.FirstOrDefault();
        if (sub == "list")
        {
            int top = a.Get("top") is { } t ? int.Parse(t) : 20;
            foreach (var row in List(queue, a.Get("stage"), top)) Console.WriteLine($"{row.Priority,6}  {row.Added}  {row.Stage}/{row.Id}");
            return 0;
        }
        if (sub != "set") throw new ArgumentException("priority set <N> --job <id>... | --from <file> | --channel <id|url> [--stage S]  |  priority list [--stage S] [--top 20]");
        int p = ParseOption(a.Positional.ElementAtOrDefault(1) ?? throw new ArgumentException("priority set <N> ..."));
        var stage = a.Get("stage") ?? "download";
        var ids = new List<string>();
        if (a.Get("job") is { } j) ids.AddRange(new[] { j }.Concat(a.Positional.Skip(2)));
        if (a.Get("from") is { } f) ids.AddRange(File.ReadAllLines(f).Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith('#')));
        if (a.Get("channel") is { } ch) ids.AddRange(JobsOfSource(queue, stage, SourceId(ch)));
        if (ids.Count == 0) throw new ArgumentException("priority set: no jobs (use --job, --from or --channel)");
        var r = await SetAsync(queue, p, ids, default);   // one priority per video: every stage of it (--with-steps is no longer needed)
        Console.WriteLine($"{r.Changed} video(s) changed to priority {p}; skipped {r.Skipped.Values.Sum()}{(r.Skipped.Count > 0 ? " (" + string.Join(", ", r.Skipped.Select(k => $"{k.Value} {k.Key}")) + ")" : "")}");
        return 0;
    }
}
