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
    string? Data = null);

/// <summary>Per-group section: empty in 0.13.1.</summary>
public sealed record CatalogGroup(string Id);

/// <summary>What the pre-check needs about every upload without opening it: duration, language, AI. Published next to the index version it was built for.</summary>
public sealed class Catalog
{
    public const string File = "catalog.json.gz", Processed = "processed", MetadataOnly = "metadata-only", HeldAi = "ai", ByIndex = "index", ByPrecheck = "precheck", DataFull = "full", DataPrecheck = "precheck";
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = JsonIgnoreCondition.Never, WriteIndented = false };
    public int Version { get; set; }
    public List<CatalogUpload> Uploads { get; set; } = [];
    public List<CatalogGroup> Groups { get; set; } = [];
    Dictionary<string, CatalogUpload>? by;
    public CatalogUpload? Find(string id) => (by ??= Uploads.GroupBy(u => u.Id).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal)).GetValueOrDefault(id);

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
    /// <summary>What an upload's precheck.json says (0.12.0+); everything null when there is none.</summary>
    public sealed record Pre(bool Exists, string? Language, bool? Dubbed, double? AiScore, bool? AiFlag, string? Title, string? Channel, double? Duration, string? Decision = null, string? MatchUpload = null, double? MatchShare = null, double? MatchOffset = null, double? MatchCosine = null);

    public static async Task<Pre> ReadPrecheckAsync(IStore store, Paths paths, string id, CancellationToken ct)
    {
        try
        {
            if (await store.GetAsync(paths.Video(id, "precheck.json"), ct) is not { } c || JsonNode.Parse(c.Data) is not JsonObject o) return new Pre(false, null, null, null, null, null, null, null);
            var sp = o["speech"] as JsonObject; var ai = o["ai"] as JsonObject; var v = o["video"] as JsonObject;
            double? dur = (double?)v?["duration"]; if (dur is not > 0) dur = null;
            var top = (o["top"] as JsonArray)?.OfType<JsonObject>().ToList() ?? []; var mu = (string?)o["matchUpload"];
            var m = (mu == null ? null : top.FirstOrDefault(x => (string?)x["upload"] == mu)) ?? top.FirstOrDefault();
            return new Pre(true, (string?)sp?["speechLanguage"], (bool?)sp?["dubbed"], (double?)ai?["score"], (bool?)ai?["flag"], (string?)v?["title"], (string?)v?["channel"], dur, (string?)o["decision"], mu ?? (string?)m?["upload"], (double?)m?["share"], (double?)m?["bestTime"], (double?)m?["bestCosine"]);
        }
        catch (Exception e) when (e is not OperationCanceledException) { return new Pre(false, null, null, null, null, null, null, null); }
    }

    /// <summary>download.zip's idx_summary durationSeconds by HEAD (never a hydrate), else the old downloaded.json, else precheck.json's measured duration.</summary>
    public static async Task<double?> DurationAsync(IStore store, Paths paths, string id, Pre? pre, CancellationToken ct) => await MatchedDuration.ReadAsync(store, paths, id, ct) ?? pre?.Duration;

    /// <summary>AI score from the frame vectors of the upload's centers.zip (every member vector, evenly subsampled to the calibrated frame count). Null when the file is unreadable.</summary>
    public static async Task<double?> AiScoreAsync(IStore store, Paths paths, string id, CancellationToken ct)
    {
        try
        {
            var z = await CentersZip.OpenAsync(store, paths.Video(id, "centers.zip"), ct); if (z == null) return null;
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
        foreach (var b in await store.ListAsync(pre, false, false, ct))
        {
            if (!b.Name.EndsWith("/precheck.json", StringComparison.Ordinal)) continue;
            var id = b.Name[pre.Length..^"/precheck.json".Length]; if (!id.Contains('/') && !have.Contains(id)) metaOnly.Add(id);
        }
        metaOnly.Sort(StringComparer.Ordinal);
        var work = processed.Select(u => (u.Id, u.SpeechLanguage, u.Dubbed, Catalog.Processed)).Concat(metaOnly.Select(i => (i, (string?)null, (bool?)null, Catalog.MetadataOnly))).ToList();
        var res = new CatalogUpload?[work.Count]; int done = 0;
        await Parallel.ForEachAsync(Enumerable.Range(0, work.Count), new ParallelOptions { MaxDegreeOfParallelism = 8, CancellationToken = ct }, async (i, t) =>
        {
            var (id, lang0, dub0, state) = work[i];
            if (prev?.Find(id) is { } old && old.CheckedBy != null && old.Data != null && old.State == state && old.DurationSeconds != null && (old.Ai?.Score != null || state == Catalog.MetadataOnly) && (old.SpeechLanguage != null || lang0 == null)) { res[i] = old with { GroupId = null, Canonical = false, Mixed = false, GroupOffset = null }; return; }
            var pc = await ReadPrecheckAsync(store, paths, id, t); string? held = null;
            if (state == Catalog.MetadataOnly && pc.AiFlag == true && pc.Decision != PreCheckDecision.Duplicate) held = Catalog.HeldAi;   // the AI hold: short-circuited, not processed (yet); state stays metadata-only
            var dur = await DurationAsync(store, paths, id, pc, t);
            string? lang = pc.Language ?? lang0; bool? dub = pc.Language != null ? pc.Dubbed : dub0;
            if (lang == null && state == Catalog.MetadataOnly) (lang, dub) = await SearchIndexBuild.ReadSpeechAsync(store, paths, id, t);
            double? score = pc.AiScore; bool? flag = pc.AiFlag;
            if (score == null && state == Catalog.Processed) { score = await AiScoreAsync(store, paths, id, t); flag = score == null ? null : score >= aiFlag; }
            res[i] = new CatalogUpload(id, dur, pc.Title, pc.Channel, lang, dub, score == null ? null : new CatalogAi(Math.Round(score.Value, 4), flag), null, false, false, null, state,
                state == Catalog.Processed ? Catalog.ByIndex : Catalog.ByPrecheck, held,
                state == Catalog.Processed ? null : pc.Decision, state == Catalog.Processed ? null : pc.MatchUpload, state == Catalog.Processed ? null : pc.MatchShare, state == Catalog.Processed ? null : pc.MatchOffset, state == Catalog.Processed ? null : pc.MatchCosine,
                state == Catalog.Processed ? Catalog.DataFull : Catalog.DataPrecheck);
            int d = Interlocked.Increment(ref done); if (d % 100 == 0) say?.Invoke($"  catalog: {d} uploads");
        });
        return new Catalog { Version = version, Uploads = res.Select(r => r!).ToList() };
    }
}
