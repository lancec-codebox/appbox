using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace IndexNode;

/// <summary>Where a job's files come from: a local job folder or the blob store (names relative to videos/&lt;id&gt;/).</summary>
public interface IJobSource
{
    string JobId { get; }
    Task<byte[]?> ReadAsync(string file, CancellationToken ct);
}

public sealed class LocalJob(string dir, string id) : IJobSource
{
    public string JobId => id;
    public Task<byte[]?> ReadAsync(string file, CancellationToken ct)
    {
        var p = Path.Combine(dir, file.Replace('/', Path.DirectorySeparatorChar));
        return Task.FromResult(File.Exists(p) ? File.ReadAllBytes(p) : null);
    }
}

public sealed class StoreJob(IStore store, Paths paths, string id) : IJobSource
{
    public string JobId => id;
    public async Task<byte[]?> ReadAsync(string file, CancellationToken ct) => await FrameChunks.ReadAsync(store, paths.Video(id, file), ct, file.EndsWith(".f16") || file.EndsWith(".jpgs") || file.EndsWith(".mp4"));
}

/// <summary>Site packs (PACK.md): only what the site needs, never videos, frames or raw per-frame data beyond the 1 fps rows.</summary>
public static class Publish
{
    public const string Version = "publish-1";
    public const int Sscd = 512;

    public static string DefaultOut() => OperatingSystem.IsWindows()
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "indexnode", "site")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share", "indexnode", "site");

    static readonly JsonSerializerOptions Opt = new() { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>Row index of the stored frame nearest to each whole second, one row per second (1 fps).</summary>
    public static List<int> OneFps(IReadOnlyList<double> times)
    {
        var rows = new List<int>(); if (times.Count == 0) return rows;
        int j = 0; int last = (int)Math.Floor(times[^1]);
        for (int s = 0; s <= last; s++)
        {
            while (j + 1 < times.Count && Math.Abs(times[j + 1] - s) <= Math.Abs(times[j] - s)) j++;
            if (rows.Count == 0 || rows[^1] != j) rows.Add(j); // a gap in the frames must not repeat a row: frames.json lists each stored frame once
        }
        return rows;
    }

    /// <summary>One row per shot (the clip search's shot rule: a cut where neighbouring cosine falls below 0.92): the frame nearest the shot's middle in time.</summary>
    public static List<int> ShotMiddles(IReadOnlyList<float[]> rows, IReadOnlyList<double> times)
    {
        var ids = Clip.ShotIds(rows); var res = new List<int>(); int i = 0;
        while (i < rows.Count)
        {
            int k = i; while (k + 1 < rows.Count && ids[k + 1] == ids[i]) k++;
            double mid = (times[i] + times[k]) / 2; int best = i;
            for (int r = i; r <= k; r++) if (Math.Abs(times[r] - mid) < Math.Abs(times[best] - mid)) best = r;
            res.Add(best); i = k + 1;
        }
        return res;
    }

    static byte[] Rows(byte[] f16, IReadOnlyList<int> rows, int dim)
    {
        var o = new byte[rows.Count * dim * 2];
        for (int i = 0; i < rows.Count; i++) Buffer.BlockCopy(f16, rows[i] * dim * 2, o, i * dim * 2, dim * 2);
        return o;
    }

    static List<ShapeCue> Vtt(byte[]? b) => b == null ? [] : CueShaper.ParseVtt(Encoding.UTF8.GetString(b));

    public static string Id(int cluster) => "c" + cluster;

    /// <summary>describe's samePerson groups: kept cluster id to every cluster id of that person (the kept one first).</summary>
    public static Dictionary<int, List<int>> Groups(JsonObject describe)
    {
        var res = new Dictionary<int, List<int>>();
        foreach (var g in (describe["samePerson"] as JsonArray) ?? [])
        {
            var ids = ((g as JsonArray) ?? []).Select(x => x!.GetValue<int>()).ToList(); if (ids.Count > 1) res[ids[0]] = ids;
        }
        return res;
    }

    /// <summary>drama.json: describe.json plus the facts (youtubeId, embeddable, channel, duration, sources) and the cluster facts of the cast.</summary>
    public static JsonObject BuildDrama(JsonObject describe, JsonObject? info, JsonObject? downloaded, JsonObject? clusters, JsonObject? processed, string jobId, string textSource, bool hasOcr, IReadOnlyDictionary<int, List<(string File, double T, bool Spoiler)>> stills, string by)
    {
        double dur = downloaded?["durationSeconds"]?.GetValue<double>() ?? info?["duration"]?.GetValue<double>() ?? 0;
        double cut = describe["cutoffSeconds"]?.GetValue<double>() ?? Describe.Cutoff(dur);
        var cl = (clusters?["clusters"] as JsonArray)?.OfType<JsonObject>().ToDictionary(c => c["id"]!.GetValue<int>()) ?? [];
        var cast = new JsonArray();
        foreach (var c in (describe["cast"] as JsonArray) ?? [])
        {
            int id = c!["cluster"]!.GetValue<int>(); cl.TryGetValue(id, out var f); var grp = Groups(describe).GetValueOrDefault(id) ?? [id];
            var fs = grp.Where(cl.ContainsKey).Select(x => cl[x]).ToList();
            var st = new JsonArray(); foreach (var s in stills.GetValueOrDefault(id) ?? []) st.Add(new JsonObject { ["file"] = s.File, ["t"] = s.T, ["spoiler"] = s.Spoiler });
            cast.Add(new JsonObject
            {
                ["id"] = Id(id), ["character"] = c["character"]?.DeepClone(), ["role"] = c["role"]!.DeepClone(), ["roleAtStart"] = c["roleAtStart"]?.DeepClone(), ["description"] = c["description"]!.DeepClone(), ["actor"] = c["actor"]?.DeepClone(),
                ["screenSeconds"] = Math.Round(fs.Sum(x => x["screenSeconds"]?.GetValue<double>() ?? 0), 1), ["firstSeen"] = fs.Count == 0 ? 0 : fs.Min(x => x["first"]!.GetValue<double>()), ["masked"] = f?["masked"]?.DeepClone() ?? false, ["faceCluster"] = id, ["faceClusters"] = new JsonArray(grp.Select(x => (JsonNode)x).ToArray()), ["stills"] = st, ["label"] = c["label"]!.DeepClone(),
            });
        }
        var rel = new JsonArray();
        foreach (var r in (describe["relations"] as JsonArray) ?? [])
            rel.Add(new JsonObject { ["from"] = Id(r!["from"]!.GetValue<int>()), ["to"] = Id(r["to"]!.GetValue<int>()), ["type"] = r["type"]!.DeepClone(), ["label"] = r["label"]!.DeepClone(), ["since"] = r["since"]!.DeepClone(), ["until"] = r["until"]?.DeepClone(), ["spoiler"] = r["spoiler"]!.DeepClone(), ["implied"] = r["implied"]!.DeepClone() });
        return new JsonObject
        {
            ["v"] = 0, ["packId"] = jobId, ["youtubeId"] = info?["id"]?.GetValue<string>() ?? jobId, ["embeddable"] = downloaded?["embeddable"]?.GetValue<bool>() ?? false,
            ["channel"] = info?["channel"]?.GetValue<string>() ?? info?["uploader"]?.GetValue<string>() ?? "", ["durationSeconds"] = dur,
            ["originalTitle"] = info?["title"]?.GetValue<string>() ?? "",
            ["title"] = describe["title"]!.DeepClone(), ["fullTitle"] = describe["fullTitle"]!.DeepClone(), ["teaser"] = describe["teaser"]!.DeepClone(), ["synopsis"] = describe["synopsis"]!.DeepClone(),
            ["era"] = describe["era"]?.DeepClone(), ["fantasy"] = describe["fantasy"]?.DeepClone() ?? false, ["tags"] = describe["tags"]!.DeepClone(),
            ["cast"] = cast, ["relations"] = rel, ["cutoffSeconds"] = cut,
            ["sources"] = new JsonObject { ["text"] = textSource, ["asrModel"] = processed?["asrModel"]?.DeepClone(), ["ocr"] = hasOcr },
            ["made"] = new JsonObject { ["at"] = Protocol.NowUtc(), ["by"] = by, ["notes"] = $"describe {describe["promptVersion"]}, models {describe["models"]?.ToJsonString()}" },
        };
    }

    /// <summary>The fingerprint of a pack's inputs: when it is unchanged the pack is up to date.</summary>
    public static string Stamp(byte[]? processed, byte[]? describe) =>
        Version + "|" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(processed ?? [])) + "|" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(describe ?? []));

    public sealed record Result(string Id, bool Written, string? Skipped);

    public static async Task<Result> PublishOneAsync(Tools tools, IJobSource src, string outDir, bool force, CancellationToken ct, string by = "indexnode")
    {
        var id = src.JobId;
        var processedB = await src.ReadAsync("processed.json", ct); var describeB = await src.ReadAsync("describe.json", ct);
        if (processedB == null) return new(id, false, "no processed.json");
        if (describeB == null) return new(id, false, "no describe.json");
        var dir = Path.Combine(outDir, id); var stampFile = Path.Combine(dir, ".stamp"); var stamp = Stamp(processedB, describeB);
        if (!force && File.Exists(stampFile) && File.ReadAllText(stampFile) == stamp) return new(id, false, "up to date");
        var tmp = dir + ".tmp"; if (Directory.Exists(tmp)) Directory.Delete(tmp, true); Directory.CreateDirectory(Path.Combine(tmp, "cast"));
        async Task<JsonObject?> J(string f) => await src.ReadAsync(f, ct) is { } b && JsonNode.Parse(b) is JsonObject o ? o : null;
        async Task<JsonNode?> JN(string f) => await src.ReadAsync(f, ct) is { } b ? JsonNode.Parse(b) : null;
        var describe = JsonNode.Parse(describeB)!.AsObject(); var processed = JsonNode.Parse(processedB)!.AsObject();
        var info = await J("info.json"); var downloaded = await J("downloaded.json"); var clusters = await J("cast/clusters.json");

        // frames.json + sscd.f16 at 1 fps; clip.f16 + clipFrames.json one row per shot
        var times = await FrameChunks.TimesAsync(src, ct);
        if (times != null)
        {
            if (await FrameChunks.VectorsAsync(src, "sscd", ct) is { } sscd && sscd.Length >= times.Count * Sscd * 2)
            {
                var rows = OneFps(times);
                File.WriteAllBytes(Path.Combine(tmp, "sscd.f16"), Rows(sscd, rows, Sscd));
                File.WriteAllText(Path.Combine(tmp, "frames.json"), JsonSerializer.Serialize(rows.Select(r => Math.Round(times[r], 2))));
            }
            if (await FrameChunks.VectorsAsync(src, "clip", ct) is { } clipB)
            {
                var crow = Clip.FromF16Bytes(clipB); var n = Math.Min(crow.Length, times.Count);
                var pick = ShotMiddles(crow.Take(n).ToList(), times.Take(n).ToList());
                File.WriteAllBytes(Path.Combine(tmp, "clip.f16"), Rows(clipB, pick, Clip.Dim));
                File.WriteAllText(Path.Combine(tmp, "clipFrames.json"), JsonSerializer.Serialize(pick.Select(r => Math.Round(times[r], 2))));
            }
        }
        // subtitles: OCR first, transcript filling gaps; English: uploaded, else burned-in OCR
        var ocrZh = Vtt(await src.ReadAsync("ocr/subs.zh.vtt", ct)); var asr = Vtt(await src.ReadAsync("asr/" + Transcribe.ModelName + ".zh.vtt", ct));
        var merged = TextTrack.Merge(ocrZh, asr);
        File.WriteAllText(Path.Combine(tmp, "subs.zh.vtt"), CueShaper.ToVtt(merged));
        byte[]? en = null; foreach (var f in new[] { "subs.en.vtt", "subs.en.srt", "ocr/subs.en.vtt" }) { en = await src.ReadAsync(f, ct); if (en != null) { if (f.EndsWith(".srt")) en = Encoding.UTF8.GetBytes(Srt2Vtt(Encoding.UTF8.GetString(en))); break; } }
        if (en != null) File.WriteAllText(Path.Combine(tmp, "subs.en.vtt"), CueShaper.ToVtt(CueShaper.Shape(Vtt(en))));
        // pictures: portraits, stills (spoiler after the cutoff), thumb
        double cut = describe["cutoffSeconds"]?.GetValue<double>() ?? 0; double dur0 = info?["duration"]?.GetValue<double>() ?? downloaded?["durationSeconds"]?.GetValue<double>() ?? 0; var stills = new Dictionary<int, List<(string, double, bool)>>(); var picks = describe["picks"] as JsonObject;
        foreach (var c in (describe["cast"] as JsonArray) ?? [])
        {
            int cid = c!["cluster"]!.GetValue<int>();
            var cand = await JN($"cast/cand_{cid}.json");
            // portrait: the best expression-scored head shot before the late limit; else the cast stage's portrait
            byte[]? pb = null;
            if (ExpressionScores(describe, cid.ToString()) is { } sc && cand is JsonArray ca
                && PickByExpression(ca, picks?[cid.ToString()]?["portrait"] is JsonValue ppv && ppv.TryGetValue<int>(out var ppk) ? ppk : null, cut, dur0, sc) is { } ps)
                pb = await src.ReadAsync($"cast/cand_hs_{cid}_{ps.K}.jpg", ct);
            pb ??= await src.ReadAsync($"cast/portrait_{cid}.jpg", ct);
            if (pb != null) File.WriteAllBytes(Path.Combine(tmp, "cast", $"{Id(cid)}.jpg"), pb); var list = new List<(string, double, bool)>(); int n = 0;
            foreach (var s in (picks?[cid.ToString()]?["stills"] as JsonArray) ?? [])
            {
                if (n >= 3 || s is not JsonValue sv || !sv.TryGetValue<int>(out var k)) continue;
                if (await src.ReadAsync($"cast/cand_{cid}_{k}.jpg", ct) is not { } jb) continue;
                double t = (cand as JsonNode)?.AsArray().FirstOrDefault(x => x?["k"]?.GetValue<int>() == k)?["t"]?.GetValue<double>() ?? 0;
                n++; var name = $"cast/{Id(cid)}_s{n}.jpg"; File.WriteAllBytes(Path.Combine(tmp, name), jb); list.Add((name, t, t > cut));
            }
            stills[cid] = list;
        }
        if (await src.ReadAsync("thumb.jpg", ct) is { } th) File.WriteAllBytes(Path.Combine(tmp, "thumb.jpg"), th);
        var cover = await CoversAsync(tools, src, describe, tmp, info?["duration"]?.GetValue<double>() ?? downloaded?["durationSeconds"]?.GetValue<double>() ?? 0, ct);
        await PosterAsync(tools, src, describe, tmp, ct); // poster.jpg stays for one release (compatibility)
        var drama = BuildDrama(describe, info, downloaded, clusters, processed, id, ocrZh.Count > 0 ? "ocr" : "transcript", ocrZh.Count > 0, stills, by);
        if (cover != null) drama["cover"] = cover;
        File.WriteAllText(Path.Combine(tmp, "drama.json"), drama.ToJsonString(Opt));
        File.WriteAllText(Path.Combine(tmp, ".stamp"), stamp);
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
        Directory.Move(tmp, dir);
        return new(id, true, null);
    }

    static string Srt2Vtt(string srt) => "WEBVTT\n\n" + System.Text.RegularExpressions.Regex.Replace(srt.Replace("\r", ""), @"(\d\d:\d\d:\d\d),(\d\d\d)", "$1.$2");

    /// <summary>The two-shot for the poster: only candidates that show both lead clusters; describe's pick if it is one of them, else the best-scoring one (the list is ordered by score). Null: no candidate has both leads.</summary>
    public static int? PickTwoShot(JsonArray? cands, int a, int b, int? chosen) => PickTwoShot(cands, [a], [b], chosen);

    public static int? PickTwoShot(JsonArray? cands, IReadOnlyCollection<int> a, IReadOnlyCollection<int> b, int? chosen, IReadOnlyList<int?>? scores = null)
    {
        if (cands == null) return null;
        var ok = cands.OfType<JsonObject>().Where(c => c["clusters"] is JsonArray cl && cl.Select(x => x!.GetValue<int>()).Any(a.Contains) && cl.Select(x => x!.GetValue<int>()).Any(b.Contains) && !cl.Select(x => x!.GetValue<int>()).All(x => a.Contains(x) && b.Contains(x))).Select(c => c["k"]!.GetValue<int>()).ToList();
        if (scores != null)   // the vision expression score decides: both faces flattering, best score first, ties to describe's pick then to the list's order
        {
            int S(int k) => k >= 0 && k < scores.Count ? scores[k] ?? -1 : -1;
            ok = ok.Where(k => S(k) >= MinExpression).OrderByDescending(S).ThenByDescending(k => k == chosen).ToList();
            return ok.Count == 0 ? null : ok[0];
        }
        if (ok.Count == 0) return null;
        return chosen is { } kk && ok.Contains(kk) ? kk : ok[0];
    }

    public const int CoverW = 360, CoverH = 640;
    public const double CoverLateFraction = 0.75;
    /// <summary>A shot needs at least this vision expression score (0-10) to be a cover or a portrait: eyes open and toward the camera, mouth closed or a slight smile.</summary>
    public const int MinExpression = 6;

    /// <summary>The model's scores for a key ("0" for cluster 0, "poster"), by candidate index; null when it gave none (an older describe).</summary>
    public static List<int?>? ExpressionScores(JsonObject describe, string key) =>
        (describe["expression"] as JsonObject)?[key] is JsonArray a ? a.Select(x => x is JsonValue v && v.TryGetValue<int>(out var i) ? (int?)i : null).ToList() : null;

    /// <summary>Where cover shots may come from, in order: the start window (up to the spoiler cutoff, about the first 12%), then the first half. Never past 75% of the runtime.</summary>
    public static (string Name, double MaxT)[] CoverWindows(double cutoff, double duration)
    {
        if (duration <= 0) return [("any", double.MaxValue)];
        var start = Math.Min(cutoff > 0 ? cutoff : duration * Describe.CutoffFraction, duration * 0.5);
        return [("start", start), ("half", duration * 0.5)];
    }

    /// <summary>The first window with a usable shot (an unmasked one; a masked pick is a last resort within the window): its shot and the window's name. Null: none before the late limit.</summary>
    public static (int K, double T, double[]? Box, string Window)? PickCoverShot(JsonArray? cands, int? picked, double cutoff, double duration, IReadOnlyList<int?>? scores = null)
    {
        if (scores != null) return PickByExpression(cands, picked, cutoff, duration, scores);
        foreach (var (name, maxT) in CoverWindows(cutoff, duration))
        {
            var inWin = new JsonArray(cands?.OfType<JsonObject>().Where(c => (c["t"]?.GetValue<double>() ?? 0) <= maxT && (c["t"]?.GetValue<double>() ?? 0) <= (duration > 0 ? duration * CoverLateFraction : double.MaxValue)).Select(c => (JsonNode)c.DeepClone()).ToArray() ?? []);
            if (!inWin.OfType<JsonObject>().Any(c => c["masked"]?.GetValue<bool>() != true)) continue; // widen rather than show a masked face
            if (PickLeadShot(inWin, picked) is { } s) return (s.K, s.T, s.Box, name);
        }
        return null;
    }

    /// <summary>The ffmpeg filter for a 9:16 crop at 360x640 centred on (cx, cy) in source pixels (null: the middle), clamped inside the frame; no text, no compositing.</summary>
    public static string CoverFilter(double? cx, double? cy)
    {
        var x = cx is { } a ? System.FormattableString.Invariant($"{a:0.#}") : "iw/2"; var y = cy is { } b ? System.FormattableString.Invariant($"{b:0.#}") : "ih/2";
        return $"crop=w='min(iw,ih*9/16)':h='min(ih,iw*16/9)':x='min(max({x}-out_w/2,0),iw-out_w)':y='min(max({y}-out_h/2,0),ih-out_h)',scale={CoverW}:{CoverH}:flags=lanczos,setsar=1";
    }

    /// <summary>Where the face centre lands in the 9:16 crop, as fractions [x, y] of the cover image (the crop window is clamped inside the frame like CoverFilter does); [0.5, 0.5] without a face.</summary>
    public static double[] CoverFocus(int w, int h, double? cx, double? cy)
    {
        if (cx is not { } x || cy is not { } y) return [0.5, 0.5];
        double cw = Math.Min(w, h * 9.0 / 16), ch = Math.Min(h, w * 16.0 / 9);
        double x0 = Math.Min(Math.Max(x - cw / 2, 0), w - cw), y0 = Math.Min(Math.Max(y - ch / 2, 0), h - ch);
        return [Math.Clamp((x - x0) / cw, 0, 1), Math.Clamp((y - y0) / ch, 0, 1)];
    }

    /// <summary>Width and height of a JPEG from its start-of-frame marker; null when not found.</summary>
    public static (int W, int H)? JpegSize(byte[] b)
    {
        for (int i = 2; i + 9 < b.Length;)
        {
            if (b[i] != 0xFF) { i++; continue; }
            int m = b[i + 1];
            if (m == 0xFF) { i++; continue; }
            if (m is >= 0xC0 and <= 0xCF && m is not (0xC4 or 0xC8 or 0xCC)) return ((b[i + 7] << 8) | b[i + 8], (b[i + 5] << 8) | b[i + 6]);
            i += 2 + ((b[i + 2] << 8) | b[i + 3]);
        }
        return null;
    }

    /// <summary>The candidate for a lead's cover: describe's portrait pick when it is unmasked, else the best-quality unmasked candidate, else the pick. Null: no candidates.</summary>
    public static (int K, double T, double[]? Box)? PickLeadShot(JsonArray? cands, int? picked)
    {
        var all = cands?.OfType<JsonObject>().ToList() ?? [];
        if (all.Count == 0) return null;
        JsonObject? c = all.FirstOrDefault(x => x["k"]?.GetValue<int>() == picked && x["masked"]?.GetValue<bool>() != true)
            ?? all.Where(x => x["masked"]?.GetValue<bool>() != true).OrderByDescending(x => x["quality"]?.GetValue<double>() ?? 0).FirstOrDefault()
            ?? all.FirstOrDefault(x => x["k"]?.GetValue<int>() == picked) ?? all[0];
        var box = (c["box"] as JsonArray)?.Select(v => v!.GetValue<double>()).ToArray();
        return (c["k"]!.GetValue<int>(), c["t"]?.GetValue<double>() ?? 0, box is { Length: 4 } ? box : null);
    }

    /// <summary>Expression-scored pick: in each window (start, then first half; never the last 25%) the unmasked candidates scoring at least MinExpression compete; the best score wins, ties go to describe's pick, then to quality. No window with a flattering shot: null.</summary>
    public static (int K, double T, double[]? Box, string Window)? PickByExpression(JsonArray? cands, int? picked, double cutoff, double duration, IReadOnlyList<int?> scores)
    {
        var all = cands?.OfType<JsonObject>().ToList() ?? [];
        foreach (var (name, maxT) in CoverWindows(cutoff, duration))
        {
            var limit = Math.Min(maxT, duration > 0 ? duration * CoverLateFraction : double.MaxValue);
            var best = all.Where(c => c["masked"]?.GetValue<bool>() != true && (c["t"]?.GetValue<double>() ?? 0) <= limit)
                .Select(c => (C: c, K: c["k"]!.GetValue<int>()))
                .Select(x => (x.C, x.K, S: x.K >= 0 && x.K < scores.Count ? scores[x.K] ?? -1 : -1))
                .Where(x => x.S >= MinExpression)
                .OrderByDescending(x => x.S).ThenByDescending(x => x.K == picked).ThenByDescending(x => x.C["quality"]?.GetValue<double>() ?? 0)
                .Select(x => ((JsonObject C, int K, int S)?)x).FirstOrDefault();
            if (best is not { } b) continue;
            var box = (b.C["box"] as JsonArray)?.Select(v => v!.GetValue<double>()).ToArray();
            return (b.K, b.C["t"]?.GetValue<double>() ?? 0, box is { Length: 4 } ? box : null, name);
        }
        return null;
    }

    static async Task<bool> CropAsync(Tools tools, byte[] jpg, string outPath, double? cx, double? cy, string tmp, CancellationToken ct)
    {
        var inp = Path.Combine(tmp, "cover_src.tmp.jpg"); File.WriteAllBytes(inp, jpg);
        try
        {
            var r = await Proc.RunAsync(await tools.FfmpegAsync(ct), ["-v", "error", "-y", "-i", inp, "-vf", CoverFilter(cx, cy), "-frames:v", "1", "-q:v", "3", outPath], ct);
            if (r.ExitCode != 0) throw new InvalidOperationException("cover: ffmpeg failed: " + Redact.Sas(r.Stderr.Length > 300 ? r.Stderr[..300] : r.Stderr));
            return true;
        }
        finally { File.Delete(inp); }
    }

    /// <summary>cover/lead1.jpg (female lead) and lead2.jpg (male lead): two separate 9:16 crops at 360x640, no text (no together cover). Each entry is { file, t, window, cast, focus: [x, y] }, the face centre as fractions of the image. A lead without a flattering shot is omitted. Returns the drama.json "cover" object, or null when there is none.</summary>
    static async Task<JsonObject?> CoversAsync(Tools tools, IJobSource src, JsonObject describe, string tmp, double dur, CancellationToken ct)
    {
        var cast = (describe["cast"] as JsonArray)?.OfType<JsonObject>().ToList() ?? [];
        var f = cast.FirstOrDefault(c => c["role"]?.GetValue<string>() == "female lead"); var m = cast.FirstOrDefault(c => c["role"]?.GetValue<string>() == "male lead");
        var picks = describe["picks"] as JsonObject; var cover = new JsonObject();
        double cutoff = describe["cutoffSeconds"]?.GetValue<double>() ?? Describe.Cutoff(dur);
        Directory.CreateDirectory(Path.Combine(tmp, "cover"));
        async Task Lead(JsonObject? c, string key)
        {
            if (c == null) return;
            int cid = c["cluster"]!.GetValue<int>();
            var cands = await src.ReadAsync($"cast/cand_{cid}.json", ct) is { } cb ? JsonNode.Parse(cb) as JsonArray : null;
            int? pk = picks?[cid.ToString()]?["portrait"] is JsonValue pv && pv.TryGetValue<int>(out var p) ? p : null;
            var shot = PickCoverShot(cands, pk, cutoff, dur, ExpressionScores(describe, cid.ToString()));
            if (shot is not { } s || await src.ReadAsync($"cast/cand_{cid}_{s.K}.jpg", ct) is not { } img) return; // no shot before the late limit: no cover (the portrait has no known time)
            double? t = s.T; double[]? box = s.Box;
            var file = $"cover/{key}.jpg";
            double? fx = box == null ? null : box[0] + box[2] / 2, fy = box == null ? null : box[1] + box[3] / 2;
            await CropAsync(tools, img, Path.Combine(tmp, file), fx, fy, tmp, ct);
            var focus = JpegSize(img) is var (iw, ih) ? CoverFocus(iw, ih, fx, fy) : [0.5, 0.5];
            cover[key] = new JsonObject { ["file"] = file, ["t"] = Math.Round(s.T, 2), ["window"] = s.Window, ["cast"] = Id(cid), ["focus"] = new JsonArray(Math.Round(focus[0], 3), Math.Round(focus[1], 3)) };
        }
        await Lead(f, "lead1"); await Lead(m, "lead2");
        return cover.Count == 0 ? null : cover;
    }

    /// <summary>poster.jpg, 600x800, no text: top 40% the two leads' portraits, bottom 60% the chosen two-shot (cast/poster_&lt;k&gt;.jpg), thin dark gutters; with no two-shot the two portraits fill it.</summary>
    static async Task PosterAsync(Tools tools, IJobSource src, JsonObject describe, string tmp, CancellationToken ct)
    {
        var cast = (describe["cast"] as JsonArray)?.OfType<JsonObject>().ToList() ?? [];
        var f = cast.FirstOrDefault(c => c["role"]?.GetValue<string>() == "female lead"); var m = cast.FirstOrDefault(c => c["role"]?.GetValue<string>() == "male lead");
        if (f == null || m == null) return;
        var a = Path.Combine(tmp, "cast", Id(f["cluster"]!.GetValue<int>()) + ".jpg"); var b = Path.Combine(tmp, "cast", Id(m["cluster"]!.GetValue<int>()) + ".jpg");
        if (!File.Exists(a) || !File.Exists(b)) return;
        string? two = null; int fc0 = f["cluster"]!.GetValue<int>(), mc0 = m["cluster"]!.GetValue<int>();
        var cands = await src.ReadAsync("cast/poster_cands.json", ct) is { } cb ? JsonNode.Parse(cb) as JsonArray : null;
        var gr = Groups(describe); var k = PickTwoShot(cands, gr.GetValueOrDefault(fc0) ?? [fc0], gr.GetValueOrDefault(mc0) ?? [mc0], describe["poster"] is JsonValue pv && pv.TryGetValue<int>(out var pk) ? pk : null);
        if (k != null && await src.ReadAsync($"cast/poster_{k}.jpg", ct) is { } pb) { two = Path.Combine(tmp, "poster_two.tmp.jpg"); File.WriteAllBytes(two, pb); }
        string P(string inp, string o, int w, int h) => $"[{inp}]scale={w}:{h}:force_original_aspect_ratio=increase,crop={w}:{h}[{o}]";
        var ff = await tools.FfmpegAsync(ct); var outp = Path.Combine(tmp, "poster.jpg");
        var args = new List<string> { "-v", "error", "-y", "-i", a, "-i", b };
        string fc;
        if (two == null) fc = P("0:v", "l", 297, 800) + ";" + P("1:v", "r", 297, 800) + ";color=c=0x101010:s=6x800[g];[l][g][r]hstack=inputs=3";
        else { args.AddRange(["-i", two]); fc = P("0:v", "l", 297, 317) + ";" + P("1:v", "r", 297, 317) + ";color=c=0x101010:s=6x317[g];[l][g][r]hstack=inputs=3[top];" + P("2:v", "bot", 600, 477) + ";color=c=0x101010:s=600x6[g2];[top][g2][bot]vstack=inputs=3"; }
        args.AddRange(["-filter_complex", fc, "-frames:v", "1", "-q:v", "3", outp]);
        var r = await Proc.RunAsync(ff, args, ct);
        if (two != null) File.Delete(two);
        if (r.ExitCode != 0) throw new InvalidOperationException("poster: ffmpeg failed: " + Redact.Sas(r.Stderr.Length > 300 ? r.Stderr[..300] : r.Stderr));
    }

    /// <summary>packs.json at the top: one entry per pack folder present.</summary>
    public static void WriteIndex(string outDir)
    {
        var packs = new JsonArray();
        foreach (var d in Directory.GetDirectories(outDir).OrderBy(x => x))
        {
            var f = Path.Combine(d, "drama.json"); if (!File.Exists(f)) continue;
            var j = JsonNode.Parse(File.ReadAllText(f))!.AsObject();
            packs.Add(new JsonObject { ["packId"] = Path.GetFileName(d), ["title"] = j["title"]?.DeepClone(), ["era"] = j["era"]?.DeepClone(), ["fantasy"] = j["fantasy"]?.DeepClone(), ["durationSeconds"] = j["durationSeconds"]?.DeepClone(), ["madeAt"] = j["made"]?["at"]?.DeepClone() });
        }
        File.WriteAllText(Path.Combine(outDir, "packs.json"), new JsonObject { ["v"] = 0, ["packs"] = packs }.ToJsonString(Opt));
    }
}
