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

    /// <summary>Pending jobs of a stage that a watch job (id "ch-UC..." / "pl-...") created: their download jobs carry Source = the watch job id.</summary>
    public static async Task<List<string>> JobsOfSourceAsync(IStore store, Paths paths, string stage, string sourceId, CancellationToken ct)
    {
        var ids = new List<string>();
        foreach (var b in await store.ListAsync(paths.JobsDir(stage), true, false, ct))
        {
            if (b.Tags.GetValueOrDefault(Tags.State) != "pending") continue;
            if (await store.GetAsync(b.Name, ct) is { } c && JobDescriptor.Parse(c.Data).Source == sourceId) ids.Add(Path.GetFileNameWithoutExtension(b.Name));
        }
        return ids;
    }

    /// <summary>The watch job id for a channel id (UC...), a channel/playlist URL or an id that already is one.</summary>
    public static string SourceId(string channel)
    {
        if (channel.StartsWith("ch-") || channel.StartsWith("pl-")) return channel;
        if (Sources.Classify(channel) is { } k) return (k.Kind == "playlist" ? "pl-" : "ch-") + k.Id;
        return "ch-" + channel;
    }

    /// <summary>
    /// Sets idx_priority (and the job body's priority, which its follow-on jobs inherit) on the pending jobs named by (stage, id). Only pending jobs change: the lease is taken for the re-tag so a claim cannot race it,
    /// and leased, done, failed and missing jobs are skipped and counted by reason.
    /// </summary>
    public static async Task<SetResult> SetAsync(IStore store, Paths paths, int priority, IEnumerable<(string Stage, string Id)> targets, CancellationToken ct, bool explicitSet = true)
    {
        Tags.CheckPriority(priority);
        int changed = 0; var skipped = new Dictionary<string, int>();
        void Skip(string why) => skipped[why] = skipped.GetValueOrDefault(why) + 1;
        foreach (var (stage, id) in targets.Distinct())
        {
            var name = paths.Job(stage, id);
            var tags = await store.GetTagsAsync(name, ct);
            if (tags == null) { Skip("no such job"); continue; }
            var state = tags.GetValueOrDefault(Tags.State);
            if (state != "pending") { Skip(state == "done" ? "done" : state == "failed" ? "failed" : $"state {state}"); continue; }
            var lease = await store.AcquireLeaseAsync(name, Protocol.LeaseSeconds, ct);
            if (lease == null) { Skip("leased"); continue; }
            try
            {
                tags = await store.GetTagsAsync(name, ct); // confirm under the lease
                var body = await store.GetAsync(name, ct);
                if (tags == null || body == null || tags.GetValueOrDefault(Tags.State) != "pending") { Skip("no longer pending"); continue; }
                var job = JobDescriptor.Parse(body.Data); job.Priority = priority; job.Explicit = explicitSet ? true : null;
                var next = new Dictionary<string, string>(tags);
                if (priority == 0) next.Remove(Tags.Priority); else next[Tags.Priority] = priority.ToString(CultureInfo.InvariantCulture);
                await store.PutAsync(name, job.ToBytes(), next, lease, false, ct);
                changed++;
            }
            finally { try { await store.ReleaseLeaseAsync(name, lease, CancellationToken.None); } catch { } }
        }
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
    /// Sets a source's priority and re-tags the still-pending jobs (download, comments, step jobs) of every video that came from it, as "priority set --with-steps" does. A video's priority is the highest of its own and its
    /// sources' priorities; this replaces an explicit "priority set" on its jobs (the explicit value wins only until a source priority of the video is changed). <paramref name="listedIds"/> are the video ids a yt-dlp listing of
    /// the source gave: videos queued before download jobs recorded their source are matched by them and get the source recorded.
    /// A direct video (id "v-&lt;videoId&gt;") has no source record: its own priority is set.
    /// </summary>
    public static async Task<SourceResult> SetSourceAsync(IStore store, Paths paths, string sourceId, int priority, IReadOnlyCollection<string>? listedIds, CancellationToken ct)
    {
        Tags.CheckPriority(priority);
        var direct = sourceId.StartsWith("v-", StringComparison.Ordinal);
        var prios = new Dictionary<string, int>();
        foreach (var b in await store.ListAsync(paths.JobsDir("watch"), true, false, ct))
            if (await store.GetAsync(b.Name, ct) is { } c) { var w = JobDescriptor.Parse(c.Data); prios[w.JobId] = w.Priority; }
        var skipped = new Dictionary<string, int>(); int changed = 0, matched = 0;
        if (!direct)
        {
            if (!prios.ContainsKey(sourceId)) throw new ArgumentException($"no source {sourceId}");
            var r = await SetAsync(store, paths, priority, [("watch", sourceId)], ct, explicitSet: false);
            if (r.Changed == 0) throw new InvalidOperationException($"source {sourceId} could not be updated ({string.Join(", ", r.Skipped.Select(k => k.Key))}); try again");
            prios[sourceId] = priority;
        }
        var listed = listedIds?.ToHashSet() ?? [];
        var videos = new List<(string Id, JobDescriptor Dl)>();
        foreach (var b in await store.ListAsync(paths.JobsDir("download"), true, false, ct))
        {
            var id = Path.GetFileNameWithoutExtension(b.Name);
            if (direct && id != sourceId[2..]) continue;
            if (await store.GetAsync(b.Name, ct) is not { } c) continue;
            var dl = JobDescriptor.Parse(c.Data);
            bool recorded = (dl.Sources ?? []).Contains(sourceId) || dl.Source == sourceId;
            if (!direct && !recorded && !listed.Contains(id)) continue;
            if (direct) dl.OwnPriority = priority;
            else if (!recorded)
            {   // queued before the source was recorded: record it now (a leased job is left alone)
                dl.Sources = (dl.Sources ?? (dl.Source != null ? [dl.Source] : [])).Append(sourceId).Distinct().ToList(); matched++;
            }
            if (direct || !recorded)
            {
                var lease = await store.AcquireLeaseAsync(b.Name, Protocol.LeaseSeconds, ct);
                if (lease != null) { try { await store.PutAsync(b.Name, dl.ToBytes(), new Dictionary<string, string>(b.Tags), lease, false, ct); } finally { await store.ReleaseLeaseAsync(b.Name, lease, CancellationToken.None); } }
                else skipped["leased (source not recorded)"] = skipped.GetValueOrDefault("leased (source not recorded)") + 1;
            }
            videos.Add((id, dl));
        }
        foreach (var (id, dl) in videos)
        {
            int p = Effective(dl, prios);
            var targets = new[] { "download", "comments" }.Concat(Protocol.StepStages).Select(s => (s, id));
            var r = await SetAsync(store, paths, p, targets, ct, explicitSet: false);
            changed += r.Changed;
            foreach (var (k, v) in r.Skipped) if (k != "no such job") skipped[k] = skipped.GetValueOrDefault(k) + v;
        }
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

    /// <summary>Pending jobs in the order nodes claim them: priority, added, name.</summary>
    public static async Task<List<(string Stage, string Id, int Priority, string Added)>> ListAsync(IStore store, Paths paths, string? stage, int top, CancellationToken ct)
    {
        var all = new List<BlobEntry>();
        foreach (var s in stage != null ? [stage] : Protocol.Stages) all.AddRange((await store.ListAsync(paths.JobsDir(s), true, false, ct)).Where(b => b.Tags.GetValueOrDefault(Tags.State) == "pending"));
        return Tags.ClaimOrder(all).Take(top).Select(b => (b.Tags.GetValueOrDefault(Tags.Stage) ?? "", Path.GetFileNameWithoutExtension(b.Name), Tags.PriorityOf(b.Tags), b.Tags.GetValueOrDefault(Tags.Added) ?? "")).ToList();
    }

    public static async Task<int> RunAsync(Args a, IStore store, Paths paths)
    {
        var sub = a.Positional.FirstOrDefault();
        if (sub == "list")
        {
            int top = a.Get("top") is { } t ? int.Parse(t) : 20;
            foreach (var row in await ListAsync(store, paths, a.Get("stage"), top, default)) Console.WriteLine($"{row.Priority,6}  {row.Added}  {row.Stage}/{row.Id}");
            return 0;
        }
        if (sub != "set") throw new ArgumentException("priority set <N> --job <id>... | --from <file> | --channel <id|url> [--stage S] [--with-steps]  |  priority list [--stage S] [--top 20]");
        int p = ParseOption(a.Positional.ElementAtOrDefault(1) ?? throw new ArgumentException("priority set <N> ..."));
        var stage = a.Get("stage") ?? "download";
        var ids = new List<string>();
        if (a.Get("job") is { } j) ids.AddRange(new[] { j }.Concat(a.Positional.Skip(2)));
        if (a.Get("from") is { } f) ids.AddRange(File.ReadAllLines(f).Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith('#')));
        if (a.Get("channel") is { } ch) ids.AddRange(await JobsOfSourceAsync(store, paths, stage, SourceId(ch), default));
        if (ids.Count == 0) throw new ArgumentException("priority set: no jobs (use --job, --from or --channel)");
        var targets = ids.Select(i => (stage, i)).ToList();
        if (a.Has("with-steps")) targets.AddRange(ids.SelectMany(i => Protocol.StepStages.Select(s => (s, i))));
        var r = await SetAsync(store, paths, p, targets, default);
        // a step the video never had is not interesting when --with-steps tried them all
        var skipped = a.Has("with-steps") ? r.Skipped.Where(k => k.Key != "no such job").ToDictionary(k => k.Key, k => k.Value) : r.Skipped;
        Console.WriteLine($"{r.Changed} job(s) changed to priority {p}; skipped {skipped.Values.Sum()}{(skipped.Count > 0 ? " (" + string.Join(", ", skipped.Select(k => $"{k.Value} {k.Key}")) + ")" : "")}");
        return 0;
    }
}
