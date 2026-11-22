using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace IndexNode;

/// <summary>
/// The aggregate video.json: ONE block blob, index/data/videos.agg, in which every upload owns one block holding its video.json without grouping.stretches (those live in index/videos/&lt;id&gt;/stretches.json) and with a title.
/// A block id is 2-digit id length + the id ('-'→'+', '_'→'/') right-padded with 'A' to 32 + a zero-padded 10-digit rev, written directly in base64 characters (44, no '='): every id has the same length, as Azure needs (an id longer than 32 is not put in the aggregate; its per-upload video.json is the record). The rev is the upload's own video.json "rev", raised by every write inside its If-Match read-modify-write.
/// Writers: Put Block (no lease, no ETag), then Get Block List; done when the committed list has this id or a higher rev; else Put Block List (every upload's highest rev, committed or not, as Latest) If-Match on the list's ETag; a 412 backs off and looks again.
/// A crash leaves at most an uncommitted block that the next writer's commit takes. Readers never commit, lease or snapshot: Get Block List (the sizes are the offsets), then a GET If-Match on its ETag (whole blob for bulk, a range for one upload); a 412 repeats.
/// The blob is never cached locally. 50,000 committed blocks is the limit (warning from 40,000); sharding by id prefix is not built.
/// </summary>
public static class VideoAggregate
{
    public const int IdLen = 32, RevDigits = 10, WarnBlocks = 40000, MaxBlocks = 50000, Attempts = 60;
    public static string Name(Paths p) => p.Prefix + "data/videos.agg";
    /// <summary>Off switch for tests and emergencies (the per-upload video.json is always written either way).</summary>
    public static bool Enabled { get; set; } = true;
    /// <summary>Jittered, capped backoff before try n (1-based). Tests set this to zero.</summary>
    public static Func<int, TimeSpan> Backoff { get; set; } = n => TimeSpan.FromMilliseconds(Math.Min(2000, 25 * Math.Pow(2, n)) * (0.5 + Random.Shared.NextDouble()));

    /// <summary>What the writers did, for the store-op accounting (per process).</summary>
    public static class Stats
    {
        public static long BlocksPut, ListGets, Commits, CommitConflicts, Reputs, AlreadyCommitted, Writes, BulkReads, RangeReads, ReadRetries;
        public static void Reset() { BlocksPut = ListGets = Commits = CommitConflicts = Reputs = AlreadyCommitted = Writes = BulkReads = RangeReads = ReadRetries = 0; }
        public static string Describe() => $"aggregate: writes={Writes} blockPuts={BlocksPut} listGets={ListGets} commits={Commits} commitRetries(412)={CommitConflicts} reputs={Reputs} skippedCommit(already in)={AlreadyCommitted} bulkReads={BulkReads} rangeReads={RangeReads} readRetries(412)={ReadRetries}";
    }

    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<IStore, object> unsupported = new();
    static IStore Identity(IStore s) { for (int i = 0; i < 8; i++) s = s switch { MeteredStore m => m.Inner, RecordingStore r => r.Inner, LocalCacheStore l => l.Inner, _ => s }; return s; }
    static bool Off(IStore s) => !Enabled || unsupported.TryGetValue(Identity(s), out _);

    public static bool Supports(string id) => id.Length is >= 1 and <= IdLen && id.All(c => c < 128 && (char.IsLetterOrDigit(c) || c is '_' or '-'));
    /// <summary>The block id, written directly in base64 characters (readable in the block list): 2 digits of id length + the id with '-' mapped to '+' and '_' to '/' (ids never contain those), right-padded with 'A' to 32, + the 10-digit rev = 44 characters (a multiple of 4: valid base64, no '=').</summary>
    public static string BlockId(string id, long rev) => id.Length.ToString("D2", System.Globalization.CultureInfo.InvariantCulture) + id.Replace('-', '+').Replace('_', '/').PadRight(IdLen, 'A') + rev.ToString("D" + RevDigits, System.Globalization.CultureInfo.InvariantCulture);
    public static (string Id, long Rev)? Decode(string blockId)
    {
        if (blockId.Length != 2 + IdLen + RevDigits || !int.TryParse(blockId.AsSpan(0, 2), out var n) || n < 1 || n > IdLen || !long.TryParse(blockId.AsSpan(2 + IdLen), out var rev)) return null;
        return (blockId.Substring(2, n).Replace('+', '-').Replace('/', '_'), rev);
    }

    /// <summary>The block's document: the video.json without grouping.stretches, with a title (youtube.title) when it has none.</summary>
    public static byte[] BlockBytes(JsonObject root)
    {
        var c = (JsonObject)root.DeepClone();
        if (c["grouping"] is JsonObject g) g.Remove("stretches");
        if (c["title"] == null && (c["youtube"] as JsonObject)?["title"] is JsonNode t) c["title"] = t.DeepClone();
        return JsonSerializer.SerializeToUtf8Bytes(c);
    }

    public static long RevOf(JsonObject? root) => root?["rev"] is JsonNode n && long.TryParse(n.ToString(), out var r) ? r : 0;

    sealed record Parsed(string Id, long Rev, string BlockId);
    static List<Parsed> ParseAll(IEnumerable<BlockItem> items, string what)
    {
        var l = new List<Parsed>();
        foreach (var b in items) l.Add(Decode(b.Id) is { } d ? new Parsed(d.Id, d.Rev, b.Id) : throw new InvalidDataException($"videos.agg has a {what} block that is not <id,rev>: {b.Id}"));
        return l;
    }

    /// <summary>True when the aggregate took the block (or the store has no block blobs, or the id cannot be a block: the per-upload file is still written).</summary>
    /// <summary>
    /// 0.13.24 mixed-version safety: an old node's put replaces the per-upload file WITHOUT x-ms-meta-rev and leaves the aggregate alone, so a new write can compute a rev at or below the committed one. After the list, a committed rev &gt;= mine whose
    /// block is not my content means: <paramref name="rebase"/>(committed + 1) checks that my per-upload put is still the current one (ETag unchanged) and sets the metadata to the new rev; my block is then put again at that rev. "Done" is my content in the committed block, not just a high enough number.
    /// </summary>
    public static async Task<bool> WriteAsync(IStore store, Paths paths, string id, long rev, JsonObject root, CancellationToken ct, Func<long, CancellationToken, Task<bool>>? rebase = null)
    {
        if (Off(store) || !Supports(id) || rev <= 0 || rev > 9_999_999_999) return false;
        var name = Name(paths); var mine = BlockId(id, rev); var bytes = BlockBytes(root);
        try
        {
            Interlocked.Increment(ref Stats.Writes);
            await store.PutBlockAsync(name, mine, bytes, ct); Interlocked.Increment(ref Stats.BlocksPut);
            for (int attempt = 1; attempt <= Attempts; attempt++)
            {
                var bl = await store.GetBlockListAsync(name, ct); Interlocked.Increment(ref Stats.ListGets);
                var com = ParseAll(bl?.Committed ?? [], "committed"); var unc = ParseAll(bl?.Uncommitted ?? [], "uncommitted");
                var comRev = com.Where(p => p.Id == id).Select(p => p.Rev).DefaultIfEmpty(0).Max();
                if (comRev >= rev)
                {
                    // done only when MY content is the committed block: the same rev with the same bytes (another writer's commit took my block) or a newer new-node write (my per-upload put is not current any more)
                    if (comRev == rev && await CommittedEqualsAsync(store, name, id, bytes, ct)) { Interlocked.Increment(ref Stats.AlreadyCommitted); return true; }
                    if (rebase == null) { Interlocked.Increment(ref Stats.AlreadyCommitted); return comRev == rev ? false : true; }
                    var nr = comRev + 1;
                    if (!await rebase(nr, ct)) { Interlocked.Increment(ref Stats.AlreadyCommitted); return true; }   // my put is no longer current: a newer write owns the content
                    rev = nr; mine = BlockId(id, rev); root["rev"] = rev; bytes = BlockBytes(root);
                    await store.PutBlockAsync(name, mine, bytes, ct); Interlocked.Increment(ref Stats.BlocksPut); Interlocked.Increment(ref Stats.Reputs); continue;
                }
                if (!unc.Any(p => p.Id == id && p.Rev >= rev))
                {   // neither mine nor a higher rev is anywhere: the block was discarded (a commit that did not name it): put it again
                    await store.PutBlockAsync(name, mine, bytes, ct); Interlocked.Increment(ref Stats.BlocksPut); Interlocked.Increment(ref Stats.Reputs); continue;
                }
                // one block per upload, the highest rev, committed order kept, new uploads appended
                var best = new Dictionary<string, Parsed>(StringComparer.Ordinal); var order = new List<string>();
                foreach (var p in com.Concat(unc)) { if (!best.TryGetValue(p.Id, out var o)) { best[p.Id] = p; order.Add(p.Id); } else if (p.Rev > o.Rev) best[p.Id] = p; }
                if (order.Count > MaxBlocks) throw new IOException($"videos.agg would hold {order.Count} blocks (limit {MaxBlocks}): shard by id prefix");
                if (order.Count >= WarnBlocks && attempt == 1) Log.Info($"warning: videos.agg holds {order.Count} of {MaxBlocks} blocks (sharding by id prefix is due)");
                if (await store.PutBlockListAsync(name, order.Select(i => best[i].BlockId).ToList(), bl?.ETag, ct)) { Interlocked.Increment(ref Stats.Commits); return true; }   // the list I committed holds my id (or a higher rev): nothing can take it out but a higher rev
                Interlocked.Increment(ref Stats.CommitConflicts);
                await Task.Delay(Backoff(attempt), ct);
            }
            throw new IOException($"{id}: videos.agg commit kept losing (412) after {Attempts} tries");
        }
        catch (NotSupportedException) { unsupported.AddOrUpdate(Identity(store), new object()); return false; }
    }

    static async Task<bool> CommittedEqualsAsync(IStore store, string name, string id, byte[] bytes, CancellationToken ct)
    {
        var got = await ReadManyAsync(store, new Paths(name[..^"data/videos.agg".Length]), [id], ct);
        return got != null && got.TryGetValue(id, out var d) && RevFree(BlockBytes(d.Root)).AsSpan().SequenceEqual(RevFree(bytes));
    }
    /// <summary>Whether the block of <paramref name="id"/> holds the same document as <paramref name="root"/> (rev ignored).</summary>
    public static bool SameContent(JsonObject block, JsonObject root) => RevFree(BlockBytes(block)).AsSpan().SequenceEqual(RevFree(BlockBytes(root)));
    static byte[] RevFree(byte[] b) { var o = (JsonObject)JsonNode.Parse(b)!; o.Remove("rev"); return JsonSerializer.SerializeToUtf8Bytes(o); }

    /// <summary>The committed documents of the aggregate (the given ids, or every upload): Get Block List, then ONE GET If-Match on its ETag for the whole blob (or the covering range); a 412 repeats. Null when the store has no aggregate (or it does not exist yet).</summary>
    public static async Task<Dictionary<string, VideoJson.Doc>?> ReadManyAsync(IStore store, Paths paths, IReadOnlyCollection<string>? ids, CancellationToken ct)
    {
        if (Off(store)) return null;
        var name = Name(paths); var want = ids == null ? null : new HashSet<string>(ids, StringComparer.Ordinal);
        try
        {
            for (int attempt = 1; attempt <= Attempts; attempt++)
            {
                var bl = await store.GetBlockListAsync(name, ct); Interlocked.Increment(ref Stats.ListGets);
                if (bl == null || bl.Committed.Count == 0 || bl.ETag == null) return null;
                var items = new List<(Parsed P, long Off, long Len)>(); long at = 0;
                foreach (var b in bl.Committed) { var p = Decode(b.Id) ?? throw new InvalidDataException($"videos.agg has a committed block that is not <id,rev>: {b.Id}"); items.Add((new Parsed(p.Id, p.Rev, b.Id), at, b.Size)); at += b.Size; }
                var pick = want == null ? items : items.Where(i => want.Contains(i.P.Id)).ToList();
                if (pick.Count == 0) return [];
                long from = pick.Min(i => i.Off), to = pick.Max(i => i.Off + i.Len);
                var rr = await store.GetRangeAsync(name, from, (int)(to - from), bl.ETag, ct);
                if (rr.Data == null && !rr.Mismatch) return null;   // gone
                if (rr.Mismatch || rr.Data == null || rr.Data.Length < to - from) { Interlocked.Increment(ref Stats.ReadRetries); await Task.Delay(Backoff(attempt), ct); continue; }   // committed meanwhile: look again
                if (pick.Count == items.Count) Interlocked.Increment(ref Stats.BulkReads); else Interlocked.Increment(ref Stats.RangeReads);
                var res = new Dictionary<string, VideoJson.Doc>(StringComparer.Ordinal);
                foreach (var i in pick)
                {
                    var slice = rr.Data.AsSpan((int)(i.Off - from), (int)i.Len).ToArray();
                    res[i.P.Id] = JsonNode.Parse(slice) is JsonObject o ? new VideoJson.Doc("\"agg:" + i.P.Rev + "\"", o, i.P.Rev) : throw new InvalidDataException($"videos.agg block {i.P.Id} is not a JSON object");
                }
                return res;
            }
            throw new IOException("videos.agg kept changing under the read (412)");
        }
        catch (NotSupportedException) { unsupported.AddOrUpdate(Identity(store), new object()); return null; }
    }

    /// <summary>True for the aggregate blob. Nexis never ghosts it (it is not cached locally and no reader may hydrate it): RestStore's ghost paths leave it alone.</summary>
    public static bool IsAggregate(string name) => name.EndsWith("data/videos.agg", StringComparison.Ordinal);
    public static string ETagOf(long rev) => "\"agg:" + rev + "\"";

    /// <summary>A bulk job's view of the aggregate: the revs of every upload from ONE Get Block List (no GET), and the whole documents from ONE GET the first time a job really needs one. Null from <see cref="OpenViewAsync"/> when the store has no aggregate yet.</summary>
    public sealed class AggView
    {
        readonly IStore store; readonly Paths paths; readonly Dictionary<string, long> revs; Task<Dictionary<string, VideoJson.Doc>?>? docs; readonly object gate = new();
        internal AggView(IStore s, Paths p, Dictionary<string, long> r) { store = s; paths = p; revs = r; }
        public bool Has(string id) => revs.ContainsKey(id);
        public int Count => revs.Count;
        /// <summary>The ETag a carried record is compared by when the aggregate holds the upload (its rev), else the per-upload file's.</summary>
        public string EtagFor(string id, string? fileETag = null) => revs.TryGetValue(id, out var r) ? ETagOf(r) : fileETag ?? "";
        public Task<Dictionary<string, VideoJson.Doc>?> AllAsync(CancellationToken ct) { lock (gate) return docs ??= ReadManyAsync(store, paths, null, ct); }
        public async Task<VideoJson.Doc?> DocAsync(string id, CancellationToken ct) => (await AllAsync(ct))?.GetValueOrDefault(id);
    }

    public static async Task<AggView?> OpenViewAsync(IStore store, Paths paths, CancellationToken ct)
    {
        if (Off(store)) return null;
        try
        {
            var bl = await store.GetBlockListAsync(Name(paths), ct); Interlocked.Increment(ref Stats.ListGets);
            if (bl == null || bl.Committed.Count == 0) return null;
            var revs = new Dictionary<string, long>(StringComparer.Ordinal);
            foreach (var b in bl.Committed) if (Decode(b.Id) is { } d) revs[d.Id] = d.Rev;
            return new AggView(store, paths, revs);
        }
        catch (NotSupportedException) { unsupported.AddOrUpdate(Identity(store), new object()); return null; }
    }

    public static async Task<VideoJson.Doc?> ReadOneAsync(IStore store, Paths paths, string id, CancellationToken ct) => (await ReadManyAsync(store, paths, [id], ct))?.GetValueOrDefault(id);
}
