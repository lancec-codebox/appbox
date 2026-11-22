using System.Text.Json;
using System.Text.Json.Nodes;

namespace IndexNode;

/// <summary>indexnode videos aggregate [--dry-run]: builds index/data/videos.agg once from the per-upload video.json files (one block per upload, stretches left out) and writes index/videos/&lt;id&gt;/stretches.json for every join that has stretches. The per-upload files are never changed or deleted.</summary>
public static class VideosAggregateCommand
{
    public sealed record Report(int Videos, int Blocks, int Stretches, int Skipped, int AlreadyCurrent, IReadOnlyList<string> Notes, int MetadataSets = 0, int StaleFixed = 0);

    public static async Task<int> RunAsync(Args a)
    {
        var (_, store, paths, http) = Commands.Open(a); using var _h = http;
        var rep = await BuildAsync(store, paths, a.Has("dry-run"), a.Get("threads") is { } t ? int.Parse(t) : 8, Console.WriteLine, default);
        Console.WriteLine($"{(a.Has("dry-run") ? "dry run, nothing written: " : "")}{rep.Videos} upload(s) with a video.json: {rep.Blocks} block(s) {(a.Has("dry-run") ? "would be " : "")}in videos.agg, {rep.AlreadyCurrent} already current, {rep.MetadataSets} rev metadata-only call(s), {rep.StaleFixed} stale blocks fixed, {rep.Stretches} stretches.json {(a.Has("dry-run") ? "would be " : "")}written, {rep.Skipped} skipped");
        foreach (var n in rep.Notes.Take(20)) Console.WriteLine("  " + n);
        return 0;
    }

    public static async Task<Report> BuildAsync(IStore store, Paths paths, bool dry, int threads, Action<string>? say, CancellationToken ct)
    {
        var pre = paths.Prefix + "videos/"; var ids = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var b in await store.ListAsync(pre, false, false, ct)) if (b.Name.EndsWith("/video.json", StringComparison.Ordinal)) { var rest = b.Name[pre.Length..]; if (rest.IndexOf('/') is var s and > 0 && rest.IndexOf('/', s + 1) < 0) ids.Add(rest[..s]); }
        var current = await VideoAggregate.ReadManyAsync(store, paths, null, ct) ?? [];
        int stretches = 0, skipped = 0, already = 0, blocks = 0, metaSets = 0, stale = 0; var notes = new System.Collections.Concurrent.ConcurrentBag<string>();
        await Parallel.ForEachAsync(ids, new ParallelOptions { MaxDegreeOfParallelism = threads, CancellationToken = ct }, async (id, t) =>
        {
            try
            {
                var doc = await VideoJson.ReadAsync(store, paths, id, t, fresh: true); if (doc == null) { Interlocked.Increment(ref skipped); return; }
                long rev = doc.Rev;
                if (!VideoAggregate.Supports(id)) { Interlocked.Increment(ref skipped); notes.Add($"{id}: not a block id the aggregate can hold (1..{VideoAggregate.IdLen} chars of A-Za-z0-9_-)"); return; }
                if (doc.Root["grouping"] is JsonObject g && g["stretches"] is JsonNode st && VideoJson.HasStretches(st))
                {
                    Interlocked.Increment(ref stretches);
                    if (!dry) await store.PutAsync(VideoJson.StretchesName(paths, id), System.Text.Encoding.UTF8.GetBytes(st.ToJsonString()), null, null, false, t);
                }
                if (rev == 0 || !doc.HasRevMeta)
                {   // no x-ms-meta-rev yet (never written, or an older body rev): METADATA ONLY (Set Blob Metadata, the content is untouched); a missing rev is 0, so the first block is rev 1
                    rev = Math.Max(1, VideoAggregate.RevOf(doc.Root));
                    Interlocked.Increment(ref metaSets);
                    if (!dry)
                    {
                        var props = await store.HeadAsync(VideoJson.Name(paths, id), t); var meta = new Dictionary<string, string>(props?.Metadata ?? new Dictionary<string, string>(), StringComparer.OrdinalIgnoreCase) { [VideoJson.RevMeta] = rev.ToString(System.Globalization.CultureInfo.InvariantCulture) };
                        await store.SetMetadataAsync(VideoJson.Name(paths, id), meta, null, t);
                        VideoJson.ClearCache(store);
                    }
                }
                if (current.TryGetValue(id, out var cd))
                {
                    var body = (JsonObject)doc.Root.DeepClone(); body.Remove("rev");
                    if (cd.Rev >= rev && VideoAggregate.SameContent(cd.Root, body)) { Interlocked.Increment(ref already); return; }
                    if (cd.Rev >= rev)
                    {   // stale block: an old node changed the per-upload file after the block was committed (rev 0 / no metadata). Block at committed + 1, metadata to match.
                        rev = cd.Rev + 1; Interlocked.Increment(ref stale); notes.Add($"{id}: stale block fixed (committed rev {cd.Rev}, now {rev})");
                        if (!dry)
                        {
                            var props = await store.HeadAsync(VideoJson.Name(paths, id), t); var meta = new Dictionary<string, string>(props?.Metadata ?? new Dictionary<string, string>(), StringComparer.OrdinalIgnoreCase) { [VideoJson.RevMeta] = rev.ToString(System.Globalization.CultureInfo.InvariantCulture) };
                            await store.SetMetadataAsync(VideoJson.Name(paths, id), meta, null, t); VideoJson.ClearCache(store);
                        }
                    }
                }
                Interlocked.Increment(ref blocks);
                if (!dry) await VideoAggregate.WriteAsync(store, paths, id, rev, doc.Root, t);
            }
            catch (Exception e) when (e is InvalidDataException or JsonException) { Interlocked.Increment(ref skipped); notes.Add($"{id}: {e.Message}"); }
        });
        return new Report(ids.Count, blocks, stretches, skipped, already, notes.OrderBy(n => n, StringComparer.Ordinal).ToList(), metaSets, stale);
    }
}
