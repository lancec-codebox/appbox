using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace IndexNode;

public sealed record CatalogAi(double? Score, bool? Flag);

/// <summary>One upload in index/search/vN/catalog.json.gz. 0.13.1: groupId null, canonical false, mixed false, groupOffset null (0.13.2 fills them).
/// CheckedBy: "index" (the index build's full check: processed) or "precheck" (the short-circuit: a duplicate skip, state metadata-only, or an AI hold: state metadata-only too, Held "ai", read from its precheck.json). Decision, MatchUpload, MatchShare, MatchOffset (seconds), MatchCosine: the check's result.
/// State (processed | metadata-only), Held ("ai" when held, else null), CheckedBy (index | precheck) and Data (full | precheck) are all kept on every entry.
/// Data: "full" (processed: every field from the full analysis, its centers are in the search index) or "precheck" (short-circuited: the same fields as sample estimates, no centers in the index, no transcript/ocr/cast; metadata, YouTube subs and comments are present). A held upload that is processed later switches to "full".</summary>
public sealed record CatalogUpload(string Id, double? DurationSeconds, string? Title, string? Channel, string? SpeechLanguage, bool? Dubbed, CatalogAi? Ai,
    string? GroupId, bool Canonical, bool Mixed, double? GroupOffset, string State,
    string? CheckedBy = null, string? Held = null, string? Decision = null, string? MatchUpload = null, double? MatchShare = null, double? MatchOffset = null, double? MatchCosine = null,
    string? Data = null, double? GroupDuration = null, double? MemberCovered = null, double? CanonicalCovered = null, string? RelationKind = null, string? VideoEtag = null);

/// <summary>0.13.23: a video.json the catalog build read and left out (downloaded, not pre-checked, not indexed), with the ETag it was read at: not read again until the ETag changes.</summary>
public sealed record CatalogSeen(string Id, string VideoEtag);

/// <summary>Per-group section: empty in 0.13.1.</summary>
public sealed record CatalogGroup(string Id, string? Canonical, IReadOnlyList<string> Members);

/// <summary>What the pre-check needs about every upload without opening it: duration, language, AI. Published next to the index version it was built for.</summary>
public sealed class Catalog
{
    public const string File = "catalog.json.gz", Processed = "processed", MetadataOnly = "metadata-only", HeldAi = "ai", ByIndex = "index", ByPrecheck = "precheck", DataFull = "full", DataPrecheck = "precheck", DataMissing = "missing";
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = JsonIgnoreCondition.Never, WriteIndented = false };
    public int Version { get; set; }
    public List<CatalogUpload> Uploads { get; set; } = [];
    public List<CatalogGroup> Groups { get; set; } = [];
    public List<CatalogSeen> NotCataloged { get; set; } = [];
    /// <summary>0.13.3: the uploads whose video.json was missing or unreadable at this build (their entries say Data "missing"); not serialized.</summary>
    [JsonIgnore] public List<string> Missing { get; set; } = [];
    Dictionary<string, CatalogUpload>? by;
    public CatalogUpload? Find(string id) => (by ??= Uploads.GroupBy(u => u.Id).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal)).GetValueOrDefault(id);

    /// <summary>0.13.2: fills groupId / canonical / mixed / groupOffset of every entry and the groups section from the group set of the same version.</summary>
    public void ApplyGroups(GroupSet set)
    {
        var m = new Dictionary<string, (string G, GroupMember M, DramaGroup D)>(StringComparer.Ordinal);
        foreach (var g in set.Groups) foreach (var x in g.Members) m[x.Upload] = (g.Id, x, g);
        Uploads = Uploads.Select(u => m.TryGetValue(u.Id, out var e) ? u with { GroupId = e.G, Canonical = e.D.Canonical == u.Id, Mixed = e.M.Mixed, GroupOffset = e.M.Offset, GroupDuration = e.M.Duration ?? u.DurationSeconds, MemberCovered = e.M.MemberCovered, CanonicalCovered = e.M.CanonicalCovered, RelationKind = e.M.Kind } : u).ToList();
        Groups = set.Groups.Select(g => new CatalogGroup(g.Id, g.Canonical, g.Members.Select(x => x.Upload).ToList())).ToList();
        by = null;
    }

    public byte[] ToGzip()
    {
        var ms = new MemoryStream();
        using (var gz = new GZipStream(ms, CompressionLevel.Optimal, true)) JsonSerializer.Serialize(gz, this, Json);
        return ms.ToArray();
    }
    public static Catalog FromGzip(byte[] b)
    {
        using var gz = new GZipStream(new MemoryStream(b), CompressionMode.Decompress);
        try { return JsonSerializer.Deserialize<Catalog>(gz, Json) ?? throw new InvalidDataException("empty catalog"); }
        catch (JsonException e) { throw new InvalidDataException("catalog.json.gz: " + e.Message); }
    }
    public static string Name(Paths p, int version) => SearchData.Dir(p, version) + File;
    public static async Task<Catalog?> ReadAsync(IStore store, Paths p, int version, CancellationToken ct) => await store.GetAsync(Name(p, version), ct) is { } c ? FromGzip(c.Data) : null;
}

public static class CatalogBuild
{
    /// <summary>What an upload's video.json says (0.13.2); the pre-check fields are null when it has no precheck section.</summary>
    public sealed record Pre(bool Exists, string? Language, bool? Dubbed, double? AiScore, bool? AiFlag, string? Title, string? Channel, double? Duration, string? Decision = null, string? MatchUpload = null, double? MatchShare = null, double? MatchOffset = null, double? MatchCosine = null);

    /// <summary>The facts of one upload from its video.json. A missing or unreadable video.json THROWS (the entry fails and is retried at the next build): never a quiet null.</summary>
    public static async Task<Pre> ReadPrecheckAsync(IStore store, Paths paths, string id, CancellationToken ct)
    {
        var root = (await VideoJson.RequireAsync(store, paths, id, ct)).Root;
        var sp = root["speech"] as JsonObject; var ai = root["ai"] as JsonObject; var yt = root["youtube"] as JsonObject; var pc = root["precheck"] as JsonObject;
        var top = (pc?["top"] as JsonArray)?.OfType<JsonObject>().ToList() ?? []; var mu = (string?)pc?["matchUpload"];
        var m = (mu == null ? null : top.FirstOrDefault(x => (string?)x["upload"] == mu)) ?? top.FirstOrDefault();
        return new Pre(pc != null, (string?)sp?["language"], (bool?)sp?["dubbed"], (double?)ai?["score"], (bool?)ai?["flag"], (string?)yt?["title"], (string?)yt?["channel"], VideoJson.Duration(root), (string?)pc?["decision"], mu ?? (string?)m?["upload"], (double?)m?["share"], (double?)m?["bestTime"], (double?)m?["bestCosine"]);
    }

    /// <summary>The upload's duration from video.json (youtube, else download); null only when the descriptor says none.</summary>
    public static async Task<double?> DurationAsync(IStore store, Paths paths, string id, Pre? pre, CancellationToken ct) => pre?.Duration ?? (await ReadPrecheckAsync(store, paths, id, ct)).Duration;

    /// <summary>AI score from the frame vectors of the upload's centers.zip (every member vector, evenly subsampled to the calibrated frame count). Null when the file is unreadable.</summary>
    public static async Task<double?> AiScoreAsync(IStore store, Paths paths, string id, CancellationToken ct)
    {
        try
        {
            var z = await FramesReader.OpenAsync(store, paths.FramesFile(id), ct); if (z == null) return null;
            var h = await z.ReadHeaderAsync(ct);
            var blocks = await z.ReadBlocksAsync(Enumerable.Range(0, h.K).ToList(), CenterSearch.MergeGap, CenterSearch.MaxMerged, ct);
            var frames = new List<(float T, float[] V)>();
            foreach (var (_, m) in blocks) { var rows = AiProbe.RowsFromF16(m.Vectors); for (int i = 0; i < rows.Length && i < m.Times.Length; i++) frames.Add((m.Times[i], rows[i])); }
            if (frames.Count == 0) return null;
            frames.Sort((a, b) => a.T.CompareTo(b.T));
            var keep = PreCheck.SubsampleIndices(frames.Count, PreCheckSettings.AiThresholdFrames);
            return AiProbe.Default.Score(keep.Select(i => frames[i].V).ToList());
        }
        catch (Exception e) when (e is not OperationCanceledException) { Log.Info($"catalog: AI score of {id} not computed ({e.Message})"); return null; }
    }

    /// <summary>The catalog of an index version: one entry per indexed upload (processed) plus the uploads the pre-check skipped (precheck.json, no centers.zip: metadata-only). Entries of <paramref name="prev"/> that are complete are reused.</summary>
    public static async Task<Catalog> BuildAsync(IStore store, Paths paths, int version, IReadOnlyList<SearchUpload> processed, Catalog? prev, double aiFlag, Action<string>? say, CancellationToken ct)
    {
        var have = processed.Select(u => u.Id).ToHashSet(StringComparer.Ordinal); var metaOnly = new List<string>(); var pre = paths.Prefix + "videos/";
        var etags = new Dictionary<string, string>(StringComparer.Ordinal);   // 0.13.23: the one listing's ETags: a video.json whose ETag is the one the previous catalog read is not read again
        var skipped = prev?.NotCataloged.ToDictionary(s => s.Id, s => s.VideoEtag, StringComparer.Ordinal) ?? new Dictionary<string, string>(StringComparer.Ordinal);
        var notCataloged = new System.Collections.Concurrent.ConcurrentBag<CatalogSeen>();
        foreach (var b in await store.ListAsync(pre, false, false, ct))
        {
            if (!b.Name.EndsWith("/video.json", StringComparison.Ordinal)) continue;
            var id = b.Name[pre.Length..^"/video.json".Length]; if (id.Contains('/')) continue;
            if (b.ETag is { Length: > 0 } et0) etags[id] = et0;
            if (have.Contains(id)) continue;
            if (b.Size > 0) metaOnly.Add(id);   // an upload with no centers.zip (the index holds only processed ones): the pre-check's short circuit, read below
        }
        metaOnly.Sort(StringComparer.Ordinal);
        var work = processed.Select(u => (u.Id, u.SpeechLanguage, u.Dubbed, Catalog.Processed)).Concat(metaOnly.Select(i => (i, (string?)null, (bool?)null, Catalog.MetadataOnly))).ToList();
        var res = new CatalogUpload?[work.Count]; int done = 0; var failed = new List<string>();
        await Parallel.ForEachAsync(Enumerable.Range(0, work.Count), new ParallelOptions { MaxDegreeOfParallelism = 8, CancellationToken = ct }, async (i, t) =>
        {
            var (id, lang0, dub0, state) = work[i];
            etags.TryGetValue(id, out var etag);
            if (etag != null && prev?.Find(id) is { } same && same.VideoEtag == etag && same.State == state && same.Data != null && same.Data != Catalog.DataMissing) { res[i] = same with { GroupId = null, Canonical = false, Mixed = false, GroupOffset = null }; return; }
            if (etag != null && state == Catalog.MetadataOnly && skipped.TryGetValue(id, out var se) && se == etag) { notCataloged.Add(new CatalogSeen(id, etag)); return; }
            if (etag == null && prev?.Find(id) is { } old && old.CheckedBy != null && old.Data != null && old.State == state && old.DurationSeconds != null && (old.Ai?.Score != null || state == Catalog.MetadataOnly) && (old.SpeechLanguage != null || lang0 == null)) { res[i] = old with { GroupId = null, Canonical = false, Mixed = false, GroupOffset = null }; return; }
            Pre pc;
            try { pc = await ReadPrecheckAsync(store, paths, id, t); }
            catch (Exception e) when (e is not OperationCanceledException)
            {   // 0.13.3: the build goes on: the entry is marked data:"missing" and named in a warning (the centers index does not need video.json)
                lock (failed) failed.Add(id); Log.Info($"catalog: WARNING {id}: {e.Message}");
                res[i] = new CatalogUpload(id, null, null, null, lang0, dub0, null, null, false, false, null, state, state == Catalog.Processed ? Catalog.ByIndex : Catalog.ByPrecheck, null, Data: Catalog.DataMissing); return;
            }
            if (state == Catalog.MetadataOnly && !pc.Exists) { if (etag != null) notCataloged.Add(new CatalogSeen(id, etag)); return; }   // downloaded, not pre-checked yet (and not indexed): not in the catalog
            string? held = null;
            if (state == Catalog.MetadataOnly && pc.AiFlag == true && !PreCheckDecision.IsSkip(pc.Decision)) held = Catalog.HeldAi;   // the AI hold: short-circuited, not processed (yet); state stays metadata-only
            var dur = pc.Duration;
            string? lang = pc.Language ?? lang0; bool? dub = pc.Language != null ? pc.Dubbed : dub0;
            double? score = pc.AiScore; bool? flag = pc.AiFlag;
            res[i] = new CatalogUpload(id, dur, pc.Title, pc.Channel, lang, dub, score == null ? null : new CatalogAi(Math.Round(score.Value, 4), flag), null, false, false, null, state,
                state == Catalog.Processed ? Catalog.ByIndex : Catalog.ByPrecheck, held,
                state == Catalog.Processed ? null : pc.Decision, state == Catalog.Processed ? null : pc.MatchUpload, state == Catalog.Processed ? null : pc.MatchShare, state == Catalog.Processed ? null : pc.MatchOffset, state == Catalog.Processed ? null : pc.MatchCosine,
                state == Catalog.Processed ? Catalog.DataFull : Catalog.DataPrecheck, VideoEtag: etag);
            int d = Interlocked.Increment(ref done); if (d % 100 == 0) say?.Invoke($"  catalog: {d} uploads");
        });
        failed.Sort(StringComparer.Ordinal);
        return new Catalog { Version = version, Uploads = res.Where(r => r != null).Select(r => r!).ToList(), Missing = failed, NotCataloged = notCataloged.OrderBy(s => s.Id, StringComparer.Ordinal).ToList() };
    }
}
