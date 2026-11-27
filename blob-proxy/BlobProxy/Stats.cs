using System.Collections.Concurrent;
using System.Text.Json.Nodes;

namespace BlobProxy;

/// <summary>
/// What went through the proxy, in memory only (nothing is written anywhere but one summary line a minute on stdout).
/// Keyed by client (the x-indexnode-node header, else "ip:&lt;address&gt;"), operation, account-container and top-level folder.
/// Totals since start plus per-minute buckets for the last hour.
/// </summary>
public sealed class Stats(TimeProvider clock)
{
    public sealed class Cell
    {
        public long Requests, BytesIn, BytesOut, Ms, S2, S3, S4, S5, Failed;
        public void Add(long bytesIn, long bytesOut, long ms, int status)
        {
            Interlocked.Increment(ref Requests); Interlocked.Add(ref BytesIn, bytesIn); Interlocked.Add(ref BytesOut, bytesOut); Interlocked.Add(ref Ms, ms);
            switch (status / 100) { case 2: Interlocked.Increment(ref S2); break; case 3: Interlocked.Increment(ref S3); break; case 4: Interlocked.Increment(ref S4); break; case 5: Interlocked.Increment(ref S5); break; default: Interlocked.Increment(ref Failed); break; }
        }
        public JsonObject Json() => new() { ["requests"] = Requests, ["bytesIn"] = BytesIn, ["bytesOut"] = BytesOut, ["avgMs"] = Requests == 0 ? 0 : Ms / Requests, ["2xx"] = S2, ["3xx"] = S3, ["4xx"] = S4, ["5xx"] = S5, ["noResponse"] = Failed };
    }

    public readonly record struct Key(string Client, string Op, string Store, string Folder);

    public DateTimeOffset Started { get; } = clock.GetUtcNow();
    readonly ConcurrentDictionary<Key, Cell> total = new();
    readonly ConcurrentDictionary<long, ConcurrentDictionary<Key, Cell>> minutes = new();

    static long MinuteOf(DateTimeOffset t) => t.ToUnixTimeSeconds() / 60;

    public void Record(Key key, long bytesIn, long bytesOut, long ms, int status)
    {
        total.GetOrAdd(key, _ => new Cell()).Add(bytesIn, bytesOut, ms, status);
        var m = MinuteOf(clock.GetUtcNow());
        minutes.GetOrAdd(m, _ => new()).GetOrAdd(key, _ => new Cell()).Add(bytesIn, bytesOut, ms, status);
        if (minutes.Count > 62) foreach (var old in minutes.Keys.Where(k => k < m - 60)) minutes.TryRemove(old, out _);
    }

    /// <summary>The operation class of a blob request: list, get (whole), range, head, put, append, block, tags, lease, delete, other.</summary>
    public static string Op(string method, string? comp, bool hasRange) => (method.ToUpperInvariant(), comp?.ToLowerInvariant()) switch
    {
        ("GET", "list") => "list",
        ("GET", "tags") or ("PUT", "tags") => "tags",
        ("GET", null) => hasRange ? "range" : "get",
        ("HEAD", _) => "head",
        ("PUT", "appendblock") => "append",
        ("PUT", "block") or ("PUT", "blocklist") => "block",
        ("PUT", "lease") => "lease",
        ("PUT", null) => "put",
        ("DELETE", _) => "delete",
        var (m, c) => (m + (c != null ? ":" + c : "")).ToLowerInvariant(),
    };

    /// <summary>The first folder of a blob path ("/index/videos/x" -> "index/videos"), two levels, so the stats say what kind of data moved.</summary>
    public static string Folder(string blobPath)
    {
        var parts = blobPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length switch { 0 => "(container)", 1 => "(root)", 2 => parts[0], _ => parts[0] + "/" + parts[1] };
    }

    static JsonArray Rows(IEnumerable<KeyValuePair<Key, Cell>> cells, Func<Key, string> by) =>
        new(cells.GroupBy(kv => by(kv.Key)).Select(g =>
        {
            var c = new Cell(); foreach (var kv in g) { c.Requests += kv.Value.Requests; c.BytesIn += kv.Value.BytesIn; c.BytesOut += kv.Value.BytesOut; c.Ms += kv.Value.Ms; c.S2 += kv.Value.S2; c.S3 += kv.Value.S3; c.S4 += kv.Value.S4; c.S5 += kv.Value.S5; c.Failed += kv.Value.Failed; }
            var o = c.Json(); o["key"] = g.Key; return (JsonNode)o;
        }).OrderByDescending(o => (long)o!["bytesOut"]! + (long)o!["bytesIn"]!).ToArray());

    /// <summary>GET /v1/stats: totals by client, op, store and folder, since start and over the last 1, 5 and 60 minutes, plus a per-minute series.</summary>
    public JsonObject Snapshot()
    {
        var now = MinuteOf(clock.GetUtcNow());
        IEnumerable<KeyValuePair<Key, Cell>> Window(int n) => minutes.Where(kv => kv.Key > now - n).SelectMany(kv => kv.Value);
        JsonObject Block(IEnumerable<KeyValuePair<Key, Cell>> cells)
        {
            var list = cells.ToList();
            return new JsonObject { ["all"] = Rows(list, _ => "all"), ["byClient"] = Rows(list, k => k.Client), ["byOp"] = Rows(list, k => k.Op), ["byStore"] = Rows(list, k => k.Store), ["byFolder"] = Rows(list, k => k.Folder) };
        }
        var series = new JsonArray(minutes.OrderBy(kv => kv.Key).Select(kv =>
        {
            long r = 0, i = 0, o = 0; foreach (var c in kv.Value.Values) { r += c.Requests; i += c.BytesIn; o += c.BytesOut; }
            return (JsonNode)new JsonObject { ["minute"] = DateTimeOffset.FromUnixTimeSeconds(kv.Key * 60).ToString("yyyy-MM-ddTHH:mmZ"), ["requests"] = r, ["bytesIn"] = i, ["bytesOut"] = o };
        }).ToArray());
        return new JsonObject
        {
            ["started"] = Started.ToString("O"), ["now"] = clock.GetUtcNow().ToString("O"),
            ["last1m"] = Block(Window(1)), ["last5m"] = Block(Window(5)), ["last60m"] = Block(Window(60)), ["sinceStart"] = Block(total),
            ["perMinute"] = series,
        };
    }

    /// <summary>One summary line of the previous minute for stdout (no paths, no query strings).</summary>
    public string? MinuteLine()
    {
        var m = MinuteOf(clock.GetUtcNow()) - 1;
        if (!minutes.TryGetValue(m, out var cells) || cells.IsEmpty) return null;
        long r = 0, i = 0, o = 0, e = 0; foreach (var c in cells.Values) { r += c.Requests; i += c.BytesIn; o += c.BytesOut; e += c.S5 + c.Failed; }
        var clients = string.Join(", ", cells.GroupBy(kv => kv.Key.Client).Select(g => $"{g.Key} {g.Sum(x => x.Value.Requests)}/{Mb(g.Sum(x => x.Value.BytesOut + x.Value.BytesIn))}").Take(12));
        return $"{DateTimeOffset.FromUnixTimeSeconds(m * 60):HH:mm}Z requests {r}, in {Mb(i)}, out {Mb(o)}, 5xx/failed {e}; {clients}";
    }

    static string Mb(long b) => $"{b / 1048576.0:0.0} MB";
}
