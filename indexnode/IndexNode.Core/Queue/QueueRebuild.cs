using System.Text;
namespace IndexNode;

/// <summary>0.13.35 `queue rebuild-snapshot`: the compaction of 0.13.0-0.13.34 dropped the process, group and step entries of a finished subject from queue.snapshot.json.gz. The sealed segments still hold their lines: this replays them (segments 1..Seg-1 and the snapshot's own segment up to its offset) and puts back every DONE entry the snapshot lacks, then writes the snapshot again with the fixed ToSnapshot, conditional on the ETag of the snapshot it read. Read-only unless <c>write</c>.</summary>
public static class QueueRebuild
{
    public sealed record Report(int Seg, long Off, bool SegmentsOk, string? Missing, int SubjectsRestored, int EntriesRestored, SortedDictionary<string, int> Replay, SortedDictionary<string, int> Current, SortedDictionary<string, int> After, bool Written, string Message, int SnapshotBytesBefore, int SnapshotBytesAfter)
    {
        public string Text(bool dry)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"snapshot is at segment {Seg}, offset {Off}; sealed segments 1..{Seg - 1}: " + (SegmentsOk ? "all present" : "MISSING " + Missing));
            if (!SegmentsOk) { sb.AppendLine("stopped: a segment is missing, nothing was written"); return sb.ToString(); }
            sb.AppendLine("stage/state            replay   snapshot   rebuilt");
            foreach (var k in Replay.Keys.Concat(Current.Keys).Concat(After.Keys).Distinct().OrderBy(x => x, StringComparer.Ordinal)) sb.AppendLine($"  {k,-22}{Replay.GetValueOrDefault(k),6}   {Current.GetValueOrDefault(k),8}   {After.GetValueOrDefault(k),7}");
            sb.AppendLine($"subjects restored: {SubjectsRestored}; entries restored: {EntriesRestored}; snapshot size {SnapshotBytesBefore} -> {SnapshotBytesAfter} bytes (gzip)");
            sb.AppendLine(dry ? "dry run: nothing written" : Message);
            return sb.ToString();
        }
    }

    static SortedDictionary<string, int> Count(QueueState st) { var d = new SortedDictionary<string, int>(StringComparer.Ordinal); foreach (var s in st.Subjects.Values) foreach (var e in s.Entries.Values) { var k = e.Stage + "/" + e.State; d[k] = d.GetValueOrDefault(k) + 1; } return d; }

    public static async Task<Report> RunAsync(IStore store, Paths paths, bool write, CancellationToken ct, Func<DateTimeOffset>? clock = null)
    {
        paths = await JobQueue.ResolveAsync(store, paths, ct);
        var snap = await store.GetAsync(paths.QueueSnapshotGz, ct);
        if (snap == null || snap.Data.Length == 0) throw new InvalidOperationException("there is no queue snapshot (" + paths.QueueSnapshotGz + "): nothing to rebuild");
        var (cur, doc) = QueueState.FromSnapshot(snap.Data);
        var empty = new SortedDictionary<string, int>(StringComparer.Ordinal);
        // every sealed segment before the snapshot's must exist (names only: a 1-byte read)
        for (int n = 1; n < doc.Seg; n++)
            if ((await store.ReadAppendAsync(paths.QueueSegment(n), 0, 1, ct)).NotFound) return new Report(doc.Seg, doc.Off, false, paths.QueueSegment(n), 0, 0, empty, Count(cur), empty, false, "", snap.Data.Length, 0);
        // replay: the segments before Seg whole, Seg up to the snapshot's offset
        var rep = new QueueState();
        for (int n = 1; n <= doc.Seg; n++)
        {
            long off = 0, limit = n < doc.Seg ? long.MaxValue : doc.Off;
            while (off < limit)
            {
                var r = await store.ReadAppendAsync(paths.QueueSegment(n), off, 4 * 1024 * 1024, ct);
                if (r.NotFound || r.Data.Length == 0) break;
                int end = Array.LastIndexOf(r.Data, (byte)'\n') + 1; if (end == 0) break;
                if (off + end > limit) end = (int)(limit - off);
                foreach (var raw in Encoding.UTF8.GetString(r.Data, 0, end).Split('\n', StringSplitOptions.RemoveEmptyEntries)) if (QLine.Parse(raw) is { } l) rep.Apply(l);
                off += end;
            }
        }
        var before0 = QueueState.FromSnapshot(snap.Data).State; int entries = 0;
        foreach (var rs in rep.Subjects.Values)
        {
            bool had = cur.Subjects.TryGetValue(rs.Id, out var cs); int before = entries;
            foreach (var (k, e) in rs.Entries)
            {
                if (e.State != "done") continue;
                if (!had) { cs = cur.Subjects[rs.Id] = new QSubject { Id = rs.Id, Epoch = rs.Epoch, LastAt = rs.LastAt, CentersAt = rs.CentersAt, Seq = rs.Seq, JoinOf = rs.JoinOf, JoinKind = rs.JoinKind, Pub = rs.Pub, Data = rs.Data }; had = true; }
                if (cs!.Entries.ContainsKey(k)) continue;
                cs.Entries[k] = e; entries++;
            }
            if (entries > before && cs != null)
            {   // the subject's facts the snapshot lacks
                cs.Data ??= rs.Data; cs.Seq ??= rs.Seq; cs.JoinOf ??= rs.JoinOf; cs.JoinKind ??= rs.JoinKind; cs.Pub ??= rs.Pub; cs.CentersAt ??= rs.CentersAt;
                if (rs.StepVer != null) { cs.StepVer ??= new(); foreach (var (k, v) in rs.StepVer) if (!cs.StepVer.ContainsKey(k) || cs.StepVer[k] < v) cs.StepVer[k] = v; }
            }
        }
        cur.MaxSeq = Math.Max(cur.MaxSeq, rep.MaxSeq);
        var now = (clock ?? (() => DateTimeOffset.UtcNow))();
        var packed = QueueState.Pack(cur.ToSnapshot(doc.Seg, doc.Off, doc.Blocks, Protocol.Utc(now), now, doc.V));
        var after = QueueState.FromSnapshot(packed).State;
        int restoredSubjects = rep.Subjects.Values.Count(rs => rs.Entries.Values.Any(e => e.State == "done" && (!before0.Subjects.TryGetValue(rs.Id, out var o) || !o.Entries.ContainsKey(e.Stage))));
        bool written = false; string msg = "";
        if (write)
        {
            written = await store.PutIfMatchAsync(paths.QueueSnapshotGz, packed, snap.ETag, ct);
            msg = written ? $"snapshot rewritten ({entries} entries restored); a node or command that loads it now sees them" : "NOT written: the snapshot changed since it was read (a concurrent rollover); run it again";
        }
        return new Report(doc.Seg, doc.Off, true, null, restoredSubjects, entries, Count(rep), Count(before0), Count(after), written, msg, snap.Data.Length, packed.Length);
    }
}
