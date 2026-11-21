namespace IndexNode;

/// <summary>
/// 0.13.16 `indexnode jobs sample-first [--dry-run]`: the queue goes sample-first, from the queue state alone (no store read).
/// every PENDING download entry (not unclassified, no full download yet) with no sample/check entry becomes a `sample` entry (same body, priority, due and added time; the download entry is dropped, the check adds it again when the video is new);
/// every PENDING process entry with no step done and no check entry gets a `check` entry (same priority), which runs before the process step (a stored video is checked by cutting its segments).
/// Create-only and batched, so it is idempotent: a subject that already has a sample or check entry is left alone, and a second run finds nothing to do. A subject under a claim is skipped (counted).
/// </summary>
public static class SampleFirst
{
    public sealed record Report(int Downloads, int Checks, int Claimed, int AlreadyDone, int Batches, int Pubs = 0, string PubSources = "");

    public static List<QLine> Plan(QueueState st, string at, out int downloads, out int checks, out int claimed, out int already, IReadOnlyDictionary<string, (string Pub, string Source)>? pubs = null)
    {
        downloads = checks = claimed = already = 0; var lines = new List<QLine>();
        foreach (var s in st.Subjects.Values.OrderBy(x => x.Id, StringComparer.Ordinal))
        {
            bool dl = s.Entries.TryGetValue("download", out var d) && d.State == "pending";
            bool pr = s.Entries.TryGetValue("process", out var p) && p.State == "pending" && (p.StepsDone == null || p.StepsDone.Count == 0) && p.Hold == null;
            if (pubs != null && pubs.TryGetValue(s.Id, out var pv) && s.Pub == null) lines.Add(new QLine { T = "pub", S = s.Id, PubTime = pv.Pub, PubSrc = pv.Source });
            if (!dl && !pr) continue;
            if (s.Entries.ContainsKey("sample") || s.Entries.ContainsKey("check")) { already++; continue; }
            if (s.ClaimBy != null) { claimed++; continue; }
            if (dl)
            {
                var j = d!.Job ?? new JobDescriptor { JobId = s.Id };
                var body = new JobDescriptor { Stage = "sample", JobId = s.Id, Url = j.Url ?? "https://www.youtube.com/watch?v=" + s.Id, MaxHeight = j.MaxHeight, Format = j.Format, Subs = j.Subs, MaxComments = j.MaxComments, Pub = j.Pub ?? (pubs?.GetValueOrDefault(s.Id).Pub), PubSource = j.Pub != null ? j.PubSource : pubs?.GetValueOrDefault(s.Id).Source, Priority = d.Priority, AddedAt = j.AddedAt.Length > 0 ? j.AddedAt : d.Added, Source = j.Source, Reason = j.Reason };
                lines.Add(QLine.Add(s.Id, "sample", body, d.Added.Length > 0 ? d.Added : at, d.Priority, d.Due));
                lines.Add(new QLine { T = "drop", S = s.Id, Stage = "download" });   // after the add: dropping the last entry would drop the subject
                downloads++;
            }
            else
            {
                var body = new JobDescriptor { Stage = "check", JobId = s.Id, Url = "https://www.youtube.com/watch?v=" + s.Id, Priority = p!.Priority, AddedAt = p.Added, Pub = p.Job?.Pub ?? (pubs?.GetValueOrDefault(s.Id).Pub), PubSource = p.Job?.Pub != null ? p.Job.PubSource : pubs?.GetValueOrDefault(s.Id).Source };
                lines.Add(QLine.Add(s.Id, "check", body, p.Added.Length > 0 ? p.Added : at, p.Priority, p.Due));
                checks++;
            }
        }
        return lines;
    }

    /// <summary>
    /// The publish time of every subject with pending sample/check/download/process work and none yet: info.json for a downloaded drama (video info, then sample info), else the listing data of its source (members.json, which holds the listing's time or an estimate).
    /// A subject whose time cannot be found is left alone (it goes after all dated ones).
    /// </summary>
    public static async Task<Dictionary<string, (string Pub, string Source)>> FindPubsAsync(JobQueue q, IStore store, Paths paths, CancellationToken ct)
    {
        var need = q.Read(st => st.Subjects.Values.Where(s => s.Pub == null && s.Entries.Values.Any(e => e.State == "pending" && e.Stage is "sample" or "check" or "download" or "process" && e.Job?.Pub == null))
            .Select(s => (s.Id, Src: s.Entries.Values.Select(e => e.Job?.Source).FirstOrDefault(x => x != null), Dl: s.Entries.ContainsKey("process") && !(s.Entries.TryGetValue("download", out var de) && de.State == "pending"))).OrderBy(x => x.Id, StringComparer.Ordinal).ToList());
        var res = new Dictionary<string, (string, string)>(StringComparer.Ordinal);
        var ests = new Dictionary<string, Dictionary<string, (string Pub, string Source)>>(StringComparer.Ordinal); int orphan = 0;
        foreach (var n in need)
        {
            ct.ThrowIfCancellationRequested();
            // 0.13.19: the source comes from the queue state, never from probing. Only a downloaded drama (download done, process pending) has an info.json: read it ONCE, from download.zip through the step-zip store.
            if (n.Dl)
            {
                try
                {
                    if (await store.GetAsync(paths.Video(n.Id, "info.json"), ct) is { } b && PubTime.FromInfo(System.Text.Json.Nodes.JsonNode.Parse(b.Data)) is { } ip) { res[n.Id] = (ip, PubTime.Info); continue; }
                }
                catch (System.Text.Json.JsonException) { }
            }
            if (n.Src == null) { res[n.Id] = (Protocol.Utc(DateTimeOffset.UtcNow.AddMinutes(-(++orphan))), PubTime.Estimate); continue; }   // no source known: still gets an estimate, in id order
            if (!ests.TryGetValue(n.Src, out var m))
            {
                var doc = await SourceMembers.ReadAsync(store, paths, n.Src, ct);
                var list = doc.Members.GroupBy(x => x.Id, StringComparer.Ordinal).Select(g => g.First()).ToList();
                // listing order is newest first: a member's own time, else its firstSeen, else an estimate that keeps the order
                var filled = PubTime.Fill(list.Select(x => x.Pub != null ? (x.Pub, x.PubSource ?? PubTime.Listing) : x.FirstSeen != null ? (x.FirstSeen, PubTime.Estimate) : ((string?)null, (string?)null)).ToList(), DateTimeOffset.UtcNow);
                ests[n.Src] = m = list.Select((x, i) => (x.Id, filled[i])).ToDictionary(x => x.Id, x => x.Item2, StringComparer.Ordinal);
            }
            res[n.Id] = m.TryGetValue(n.Id, out var f) ? f : (Protocol.Utc(DateTimeOffset.UtcNow.AddMinutes(-(++orphan))), PubTime.Estimate);
        }
        return res;
    }

    public static async Task<Report> RunAsync(JobQueue q, bool dry, CancellationToken ct, IStore? store = null, Paths? paths = null)
    {
        await q.PollAsync(ct);
        var at = Protocol.NowUtc();
        var pubs = store != null && paths != null ? await FindPubsAsync(q, store, paths, ct) : null;
        var lines = q.Read(st => Plan(st, at, out _, out _, out _, out _, pubs));
        var (d, c, cl, al) = q.Read(st => { Plan(st, at, out var dd, out var cc, out var x, out var y, pubs); return (dd, cc, x, y); });
        int nPubs = lines.Count(l => l.T == "pub");
        int batches = 0;
        if (!dry)
            for (int i = 0; i < lines.Count; i += JobQueue.BatchChunk)
            {   // keep an add and its drop in one append: a chunk never ends between them
                int n = Math.Min(JobQueue.BatchChunk, lines.Count - i); if (i + n < lines.Count && lines[i + n - 1].T == "add") n++;
                await q.AppendLinesAsync(lines.GetRange(i, n), ct); batches++; i += n - JobQueue.BatchChunk;
            }
        var bySrc = string.Join(", ", lines.Where(l => l.T == "pub").GroupBy(l => l.PubSrc).OrderBy(g => g.Key).Select(g => g.Key + " " + g.Count()));
        return new Report(d, c, cl, al, batches, nPubs, bySrc);
    }
}
