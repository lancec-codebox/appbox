using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace IndexNode;

/// <summary>
/// 0.13.62: what a person's search page needs beyond POST /v1/search (owner, 2026-11-25: the phone app gets screenshot search and actor search; "it should find the representative frame and some
/// surrounding frames as well. For actor search it should show the shots and dramas the actor appears in"). Four routes of `indexnode serve`, all behind a bearer token (the serve token or the search token):
///   GET  /v1/frames?upload=&amp;at=&amp;around=2&amp;step=3&amp;width=320   the stored frame nearest the moment and the frames around it, as JPEGs, with the upload's title
///   POST /v1/actors/search (multipart "image", optional "upload") the faces of a picture, each with the actors it looks like (exact cosine against the actor index's unit centres); with "upload", also against that upload's own units
///   GET  /v1/actors/{actor}                                       the dramas and uploads an actor appears in
///   GET  /v1/actors/shots?upload=&amp;unit=&amp;count=6&amp;width=240     frames of one upload in which that actor's unit was seen, each with the face's box
/// Reads only. A ghosted frames.zip or cast.zip is made readable the usual way (the read commits its blocks); the actor index and the units files are never ghosted.
/// </summary>
public sealed class LookServer(IStore store, Paths paths, Tools tools, HttpClient http, SearchIndexCache index, int threads)
{
    public const int MaxAround = 6, MaxShots = 12, MaxFaces = 6, MaxWidth = 720;
    /// <summary>A face of the picture counts from this detector score; an actor is listed for a face from this cosine (the page says how close each one is; nothing is decided here).</summary>
    public const float MinFaceScore = 0.5f; public const double MinCosine = 0.40;
    readonly SemaphoreSlim faceGate = new(1, 1), actorsGate = new(1, 1);
    IFaceDetector? det; IFaceEmbedder? emb;
    ActorsIndex? actors; DateTimeOffset actorsChecked;
    /// <summary>Every unit's centre as floats, one row per unit, and the unit each row is: a face is compared with all of them exactly (about 25,000 rows of 512 today: a few milliseconds), so nothing depends on a shortlist.</summary>
    float[] centres = []; (int Upload, int Unit)[] centreOf = [];
    /// <summary>The whole vector file is kept in memory up to this size (120 MB at 2,400 uploads); past it a face is routed by bit code and re-checked by ranged reads, as the actors stage does.</summary>
    public static long MaxVecBytes = 1L << 30;
    long frameCalls, actorSearches, shotCalls;
    public JsonObject Counters() => new() { ["frames"] = Interlocked.Read(ref frameCalls), ["actorSearches"] = Interlocked.Read(ref actorSearches), ["shots"] = Interlocked.Read(ref shotCalls), ["actorIndex"] = actors?.Version };

    static void CheckId(string id) { if (!System.Text.RegularExpressions.Regex.IsMatch(id, "^[A-Za-z0-9_-]{6,40}$")) throw new ArgumentException("not a video id"); }

    /// <summary>The picture as a JPEG no wider than <paramref name="width"/> (a box downscale; never enlarged).</summary>
    public static byte[] Jpeg(Rgb im, int width, int quality = 80)
    {
        if (im.W > width) im = Thumbs.BoxResize(im, width, Math.Max(1, (int)Math.Round((double)im.H * width / im.W)));
        return Imaging.EncodeJpeg(im, quality);
    }

    /// <summary>Title, channel and length of an upload from the loaded search index's catalog (no store read); null fields when the catalog does not have it.</summary>
    async Task<JsonObject> InfoAsync(string upload, CancellationToken ct)
    {
        var cat = (await index.GetAsync(store, paths, ct))?.Catalog; var u = cat?.Find(upload); var (title, channel) = await TitleAsync(cat, upload, ct);
        return new JsonObject { ["upload"] = upload, ["title"] = title, ["channel"] = channel, ["duration"] = u?.DurationSeconds, ["group"] = u?.GroupId, ["url"] = "https://www.youtube.com/watch?v=" + upload };
    }

    readonly System.Collections.Concurrent.ConcurrentDictionary<string, (string? Title, string? Channel)> titles = new(StringComparer.Ordinal);
    /// <summary>Title and channel: the catalog's, and for an upload whose catalog entry has none (about a third on 2026-11-25: uploads from before video.json carried them) its stored YouTube info (info.json inside download.zip), read once and remembered.</summary>
    async Task<(string? Title, string? Channel)> TitleAsync(Catalog? cat, string upload, CancellationToken ct)
    {
        var u = cat?.Find(upload); if (!string.IsNullOrEmpty(u?.Title)) return (u!.Title, u.Channel);
        if (titles.TryGetValue(upload, out var t)) return t;
        try
        {
            if (await store.GetAsync(paths.Video(upload, "info.json"), ct) is { } b && JsonNode.Parse(Encoding.UTF8.GetString(b.Data)) is JsonObject o)
                t = (o["title"] is JsonValue tv && tv.TryGetValue<string>(out var ts) ? ts : null, o["channel"] is JsonValue cv && cv.TryGetValue<string>(out var cs) ? cs : o["uploader"] is JsonValue uv && uv.TryGetValue<string>(out var us) ? us : null);
        }
        catch (Exception e) when (e is System.Text.Json.JsonException or InvalidDataException or FileNotFoundException) { }
        return titles[upload] = t;
    }

    // ---- frames around a moment

    public async Task<JsonObject> FramesAsync(string upload, double at, int around, double step, int width, CancellationToken ct)
    {
        CheckId(upload); around = Math.Clamp(around, 0, MaxAround); step = Math.Clamp(step, 1, 600); width = Math.Clamp(width, 64, MaxWidth);
        await using var src = await SheetCommand.FramesSource.OpenAsync(store, paths, upload, tools, ct) ?? throw new FileNotFoundException($"{upload} has no stored frames");
        var got = new SortedDictionary<double, (SheetCommand.Shot Shot, bool Hit)>();
        // the moment itself first, so a neighbour that lands on the same stored frame never replaces it
        foreach (int k in Enumerable.Range(0, 2 * around + 1).Select(i => i == 0 ? 0 : (i + 1) / 2 * (i % 2 == 1 ? -1 : 1)))
        {
            double want = at + k * step; if (want < 0 || (k > 0 && src.Duration > 0 && want > src.Duration + step)) continue;
            if (await src.AtAsync(want, ct) is { } s && !got.ContainsKey(s.T)) got[s.T] = (s, k == 0);
        }
        Interlocked.Increment(ref frameCalls);
        return new JsonObject
        {
            ["info"] = await InfoAsync(upload, ct), ["at"] = Math.Round(at, 2),
            ["frames"] = new JsonArray(got.Values.Select(x => (JsonNode?)new JsonObject { ["t"] = Math.Round(x.Shot.T, 2), ["timestamp"] = SheetCommand.Clock(x.Shot.T), ["hit"] = x.Hit, ["jpeg"] = Convert.ToBase64String(Jpeg(x.Shot.Im, width)) }).ToArray()),
        };
    }

    // ---- actors

    /// <summary>The actor index, loaded on the first call and looked at again (head.json, one read) at most every ten minutes, on a request: no timer.</summary>
    async Task<(ActorsIndex Idx, ActorsIO Io)> ActorsAsync(CancellationToken ct)
    {
        var io = new ActorsIO(store, paths, dry: true);
        await actorsGate.WaitAsync(ct);
        try
        {
            if (actors != null && DateTimeOffset.UtcNow - actorsChecked < TimeSpan.FromMinutes(10)) return (actors, io);
            var head = await io.GetAsync(ActorsData.HeadFile(paths), ct) ?? throw new FileNotFoundException("there is no actor index yet");
            int v = (int?)(JsonNode.Parse(Encoding.UTF8.GetString(head.Data)) as JsonObject)?["version"] ?? 0;
            if (actors == null || actors.Version != v)
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                actors = (await ActorsIndex.LoadAsync(io, ct, withVec: true, head: head, vecCap: MaxVecBytes, links: false)).Idx ?? throw new InvalidOperationException("the actor index could not be read");
                centres = []; centreOf = [];
                if (actors.Vec is { } vec)
                {
                    centreOf = actors.Uploads.SelectMany((u, i) => u.Units.Select((_, k) => (i, k))).ToArray(); centres = new float[(long)centreOf.Length * actors.Dim];
                    for (int r = 0; r < centreOf.Length; r++) { int row = actors.Uploads[centreOf[r].Upload].Units[centreOf[r].Unit].Row; for (int d = 0; d < actors.Dim; d++) centres[r * actors.Dim + d] = (float)BitConverter.ToHalf(vec, (row * actors.Dim + d) * 2); }
                }
                Log.Info($"serve: actor index v{actors.Version} loaded ({actors.Uploads.Count} uploads, {actors.Count} vectors, {(actors.Vec == null ? "vectors by range" : "vectors in memory")}) in {sw.Elapsed.TotalSeconds:0.0}s");
            }
            actorsChecked = DateTimeOffset.UtcNow; return (actors, io);
        }
        finally { actorsGate.Release(); }
    }

    async Task<Rgb> DecodeAsync(byte[] image, CancellationToken ct)
    {
        try
        {
            if (InProcessMedia.IsWebp(image)) image = await InProcessMedia.WebpToPngAsync(await tools.FfmpegAsync(ct), image, ct);
            return InProcessMedia.DecodeImage(image);
        }
        catch (Exception e) when (e is InvalidOperationException or InvalidDataException) { throw new ArgumentException("the image could not be read: " + e.Message); }
    }

    /// <summary>The faces of the picture (largest first, at most <see cref="MaxFaces"/>), each embedded with the cast step's own models (det_500m + w600k_mbf, what the index's unit centres were made with).</summary>
    async Task<List<(FaceDet Det, float[] Vec, Rgb Aligned)>> FacesAsync(Rgb im, CancellationToken ct)
    {
        await faceGate.WaitAsync(ct);
        try
        {
            if (det == null || emb == null) { var (d, r) = await Models.EnsureBuffaloSAsync(http, ct); det = new Scrfd(d, Math.Max(1, threads)); emb = new ArcFace(r, Math.Max(1, threads)); }
            var dets = det.Detect(im).Where(x => x.Score >= MinFaceScore).OrderByDescending(x => x.W * x.H).Take(MaxFaces).ToList();
            if (dets.Count == 0) return [];
            var aligned = dets.Select(x => Imaging.AlignFace(im, x.Kps)).ToList(); var vecs = emb.EmbedBatch(aligned);
            foreach (var v in vecs) FaceMath.L2(v);
            return dets.Select((x, i) => (x, vecs[i], aligned[i])).ToList();
        }
        finally { faceGate.Release(); }
    }

    /// <summary>The units nearest a face, best first: exact against every unit centre when the vectors are in memory, otherwise the bit-code shortlist re-checked by ranged reads.</summary>
    async Task<List<(int Upload, int Unit, double Cos)>> NearestAsync(ActorsIndex idx, ActorsIO io, float[] v, int keep, CancellationToken ct)
    {
        if (centres.Length > 0)
        {
            int dim = idx.Dim; var cos = new double[centreOf.Length];
            Parallel.For(0, centreOf.Length, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, threads) }, r => { double s = 0; int o = r * dim; for (int d = 0; d < dim; d++) s += centres[o + d] * v[d]; cos[r] = s; });
            return Enumerable.Range(0, cos.Length).OrderByDescending(r => cos[r]).Take(keep).Select(r => (centreOf[r].Upload, centreOf[r].Unit, cos[r])).ToList();
        }
        var routed = idx.Route(v, Math.Max(keep, 400), _ => true);
        await idx.PrefetchAsync(io, routed.Select(x => idx.Uploads[x.Upload].Units[x.Unit].Row), ct);
        return (await idx.ExactAsync(io, v, routed, ct)).Take(keep).ToList();
    }

    public async Task<JsonObject> ActorsSearchAsync(byte[] image, int top, CancellationToken ct, string? upload = null)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew(); top = Math.Clamp(top, 1, 20);
        var im = await DecodeAsync(image, ct);
        var faces = await FacesAsync(im, ct); double faceMs = sw.Elapsed.TotalMilliseconds;
        var (idx, io) = await ActorsAsync(ct);
        // the drama is already known (a scene search found it): the face is also compared with that upload's own units, straight from its units file
        UnitDoc? own = null;
        if (!string.IsNullOrEmpty(upload)) { CheckId(upload); if (await store.GetAsync(ActorsData.UnitFile(paths, upload), ct) is { } ub) own = UnitDoc.FromBytes(ub.Data, ActorsData.UnitFile(paths, upload)); }
        var outFaces = new JsonArray();
        foreach (var (d, v, al) in faces)
        {
            var exact = await NearestAsync(idx, io, v, 200, ct);
            // one line per actor: its closest unit; how many dramas and uploads it is in comes from the whole index
            var best = exact.Where(x => x.Cos >= MinCosine).GroupBy(x => idx.Uploads[x.Upload].Units[x.Unit].Actor, StringComparer.Ordinal).Select(g => g.First()).Take(top).ToList();
            var list = new JsonArray();
            foreach (var (u, k, cos) in best)
            {
                var unit = idx.Uploads[u].Units[k]; var where = Appearances(idx, unit.Actor);
                list.Add(new JsonObject { ["actor"] = unit.Actor, ["cosine"] = Math.Round(cos, 4), ["dramas"] = where.Select(x => x.Prod).Distinct(StringComparer.Ordinal).Count(), ["uploads"] = where.Count, ["upload"] = idx.Uploads[u].Id, ["unit"] = unit.Key });
            }
            outFaces.Add(new JsonObject
            {
                ["box"] = new JsonArray(Rel(d.X, im.W), Rel(d.Y, im.H), Rel(d.W, im.W), Rel(d.H, im.H)), ["score"] = Math.Round(d.Score, 3),
                ["jpeg"] = Convert.ToBase64String(Imaging.EncodeJpeg(al, 85)), ["actors"] = list, ["inUpload"] = own == null ? null : OwnUnits(own, v, idx),
            });
        }
        Interlocked.Increment(ref actorSearches);
        return new JsonObject { ["actorIndex"] = idx.Version, ["image"] = new JsonObject { ["width"] = im.W, ["height"] = im.H }, ["faces"] = outFaces, ["timings"] = new JsonObject { ["faceMs"] = Math.Round(faceMs, 1), ["totalMs"] = Math.Round(sw.Elapsed.TotalMilliseconds, 1) } };
    }
    /// <summary>The upload's own units nearest the face (centre cosine, and the best of the unit's representative faces), best three.</summary>
    static JsonArray OwnUnits(UnitDoc doc, float[] v, ActorsIndex idx) => new(doc.Units.Where(u => u.Vec.Length > 0 && u.Vec[0].Length == v.Length)
        .Select(u => (U: u, Cos: CastLogic.Cosine(v, u.Vec[0]), Best: u.Vec.Max(r => CastLogic.Cosine(v, r)))).OrderByDescending(x => x.Cos).Take(3)
        .Select(x => (JsonNode?)new JsonObject { ["upload"] = doc.Id, ["unit"] = x.U.Key, ["actor"] = ActorOf(idx, doc.Id, x.U.Key) ?? x.U.Actor, ["cosine"] = Math.Round(x.Cos, 4), ["best"] = Math.Round(x.Best, 4), ["faces"] = x.U.N }).ToArray());
    /// <summary>The actor a unit belongs to, as the index has it (a units file written before the index build carries no actor id).</summary>
    static string? ActorOf(ActorsIndex idx, string upload, string unit) => idx.Uploads.FirstOrDefault(u => u.Id == upload).Units?.FirstOrDefault(x => x.Key == unit)?.Actor is { Length: > 0 } a ? a : null;
    static JsonNode Rel(float v, int of) => JsonValue.Create(Math.Round(Math.Clamp(v / Math.Max(1, of), 0, 1), 4));

    /// <summary>Every unit of the index that belongs to the actor: (upload, unit key, faces, production).</summary>
    static List<(string Upload, string Unit, int N, string Prod)> Appearances(ActorsIndex idx, string actor) =>
        idx.Uploads.SelectMany(u => u.Units.Where(x => x.Actor == actor).Select(x => (u.Id, x.Key, x.N, x.Prod))).ToList();

    public async Task<JsonObject> ActorAsync(string actor, CancellationToken ct)
    {
        if (actor.Length is 0 or > 64) throw new ArgumentException("not an actor id");
        var (idx, _) = await ActorsAsync(ct); var cat = (await index.GetAsync(store, paths, ct))?.Catalog;
        var where = Appearances(idx, actor); if (where.Count == 0) throw new FileNotFoundException($"the actor index (v{idx.Version}) has no actor {actor}");
        // a drama = a production of the index; inside it the uploads the actor was seen in, the one with most faces first
        var dramas = where.GroupBy(x => x.Prod.Length > 0 ? x.Prod : x.Upload, StringComparer.Ordinal).Select(g => (Prod: g.Key, Ups: g.GroupBy(x => x.Upload, StringComparer.Ordinal).Select(ug => ug.OrderByDescending(x => x.N).First()).OrderByDescending(x => x.N).ToList()))
            .OrderByDescending(d => d.Ups.Sum(x => x.N)).ToList();
        var names = new Dictionary<string, (string? Title, string? Channel)>(StringComparer.Ordinal);
        foreach (var x in dramas.SelectMany(d => d.Ups)) names[x.Upload] = await TitleAsync(cat, x.Upload, ct);
        return new JsonObject
        {
            ["actor"] = actor, ["actorIndex"] = idx.Version,
            ["dramas"] = new JsonArray(dramas.Select(d => (JsonNode?)new JsonObject
            {
                ["drama"] = d.Prod, ["title"] = names[d.Ups[0].Upload].Title, ["faces"] = d.Ups.Sum(x => x.N),
                ["uploads"] = new JsonArray(d.Ups.Select(x => (JsonNode?)new JsonObject { ["upload"] = x.Upload, ["unit"] = x.Unit, ["faces"] = x.N, ["title"] = names[x.Upload].Title, ["channel"] = names[x.Upload].Channel, ["duration"] = cat?.Find(x.Upload)?.DurationSeconds, ["url"] = "https://www.youtube.com/watch?v=" + x.Upload }).ToArray()),
            }).ToArray()),
        };
    }

    /// <summary>Frames of one upload in which the unit's faces were found, spread over the stretch it appears in. The face times come from the upload's stored faces (cast/faces.json), the unit's face list from its units file.</summary>
    public async Task<JsonObject> ShotsAsync(string upload, string unitKey, int count, int width, CancellationToken ct)
    {
        CheckId(upload); count = Math.Clamp(count, 1, MaxShots); width = Math.Clamp(width, 64, MaxWidth);
        var ub = await store.GetAsync(ActorsData.UnitFile(paths, upload), ct) ?? throw new FileNotFoundException($"{upload} has no units file");
        var unit = UnitDoc.FromBytes(ub.Data, ActorsData.UnitFile(paths, upload)).Units.FirstOrDefault(x => x.Key == unitKey) ?? throw new FileNotFoundException($"{upload} has no unit {unitKey}");
        var fj = await store.GetAsync(paths.Video(upload, "cast/faces.json"), ct) ?? throw new FileNotFoundException($"{upload} has no stored faces");
        var all = (JsonNode.Parse(Encoding.UTF8.GetString(fj.Data)) as JsonObject)?["faces"] as JsonArray ?? [];
        var mine = unit.Faces.Where(i => i >= 0 && i < all.Count).Select(i => all[i] as JsonObject).Where(f => f?["t"] != null && f["box"] is JsonArray { Count: 4 })
            .Select(f => (T: f!["t"]!.GetValue<double>(), Box: (f["box"] as JsonArray)!.Select(v => v!.GetValue<float>()).ToArray())).OrderBy(f => f.T).ToList();
        // one face per frame time, then `count` of them evenly through the list (first and last included)
        mine = mine.GroupBy(f => f.T).Select(g => g.OrderByDescending(f => f.Box[2] * f.Box[3]).First()).ToList();
        var pick = mine.Count <= count ? mine : Enumerable.Range(0, count).Select(i => mine[(int)Math.Round(i * (mine.Count - 1.0) / (count - 1.0))]).Distinct().ToList();
        var shots = new JsonArray();
        if (pick.Count > 0)
        {
            await using var src = await SheetCommand.FramesSource.OpenAsync(store, paths, upload, tools, ct) ?? throw new FileNotFoundException($"{upload} has no stored frames");
            foreach (var f in pick)
            {
                if (await src.AtAsync(f.T, ct) is not { } s || Math.Abs(s.T - f.T) > 1.5) continue;   // the frame the face was found on, not a neighbour
                shots.Add(new JsonObject { ["t"] = Math.Round(s.T, 2), ["timestamp"] = SheetCommand.Clock(s.T), ["box"] = new JsonArray(Rel(f.Box[0], s.Im.W), Rel(f.Box[1], s.Im.H), Rel(f.Box[2], s.Im.W), Rel(f.Box[3], s.Im.H)), ["jpeg"] = Convert.ToBase64String(Jpeg(s.Im, width)) });
            }
        }
        Interlocked.Increment(ref shotCalls);
        return new JsonObject { ["info"] = await InfoAsync(upload, ct), ["unit"] = unitKey, ["actor"] = actors is { } ai ? ActorOf(ai, upload, unitKey) ?? unit.Actor : unit.Actor, ["faces"] = unit.N, ["first"] = Math.Round(unit.First, 2), ["last"] = Math.Round(unit.Last, 2), ["shots"] = shots };
    }

    // ---- routes

    /// <summary>Adds the four routes. Each is one step of the storage budget (the search step's limit) and answers like /v1/search: 400 for a bad request, 404 for something the index does not have, 503 when the store budget is spent.</summary>
    public void Map(WebApplication app, Func<HttpContext, bool> authorized)
    {
        async Task<IResult> Run(HttpContext c, string what, Func<Task<JsonObject>> f)
        {
            if (!authorized(c)) return Results.Json(new { error = "unauthorized" }, statusCode: 401);
            var meter = StoreMeter.Current;
            try
            {
                if (meter.Tripped) throw new StorageBudgetExceeded(meter.TripMessage ?? "the storage breaker is open");
                JsonObject res; long ops;
                using (meter.BeginStep(meter.Limits.StepLimitFor("search"), null)) { res = await f(); ops = meter.StepOps; }
                res["storeOps"] = ops;
                return Results.Text(res.ToJsonString(), "application/json");
            }
            catch (Exception e) when (e is ArgumentException or FormatException or OverflowException or InvalidDataException or BadHttpRequestException) { return Results.BadRequest(new { error = e.Message }); }
            catch (FileNotFoundException e) { return Results.Json(new { error = e.Message }, statusCode: 404); }
            catch (OperationCanceledException) { return Results.StatusCode(499); }
            catch (StorageBudgetExceeded e) { return Results.Json(new { error = Redact.Sas(e.Message) }, statusCode: 503); }
            catch (Exception e) { Log.Info($"serve: {what} failed: " + Redact.Sas(e.ToString())); return Results.Json(new { error = Redact.Sas(e.Message) }, statusCode: 500); }
        }
        // block bodies with a return, on purpose: `c => Run(...)` and `async c => await Run(...)` both bind to RequestDelegate, and the result is thrown away (an empty 200, no token check)
        static string Q(HttpContext c, string n) => c.Request.Query[n].ToString();
        static int I(HttpContext c, string n, int d) => Q(c, n) is { Length: > 0 } s ? int.Parse(s, CultureInfo.InvariantCulture) : d;
        static double D(HttpContext c, string n, double d) => Q(c, n) is { Length: > 0 } s ? double.Parse(s, CultureInfo.InvariantCulture) : d;
        app.MapGet("/v1/frames", async (HttpContext c) => { return await Run(c, "frames", () => FramesAsync(Q(c, "upload"), D(c, "at", 0), I(c, "around", 2), D(c, "step", 3), I(c, "width", 320), c.RequestAborted)); });
        app.MapPost("/v1/actors/search", async (HttpContext c) => { return await Run(c, "actor search", async () =>
        {
            if (!c.Request.HasFormContentType) throw new ArgumentException("multipart/form-data expected");
            var form = await c.Request.ReadFormAsync(c.RequestAborted);
            if (form.Files.GetFile("image") is not { } file) throw new ArgumentException("send an image (multipart 'image', JPEG/PNG/WebP)");
            using var ms = new MemoryStream(); await file.CopyToAsync(ms, c.RequestAborted);
            return await ActorsSearchAsync(ms.ToArray(), I(c, "top", 5), c.RequestAborted, form.TryGetValue("upload", out var up) && up.ToString().Length > 0 ? up.ToString() : Q(c, "upload"));
        }); });
        app.MapGet("/v1/actors/shots", async (HttpContext c) => { return await Run(c, "actor shots", () => ShotsAsync(Q(c, "upload"), Q(c, "unit"), I(c, "count", 6), I(c, "width", 240), c.RequestAborted)); });
        app.MapGet("/v1/actors/{actor}", async (HttpContext c, string actor) => { return await Run(c, "actor", () => ActorAsync(actor, c.RequestAborted)); });
    }
}
