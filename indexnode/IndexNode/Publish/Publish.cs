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
    /// <summary>The job's files under a folder (relative names), e.g. comments/.</summary>
    Task<IReadOnlyList<string>> ListAsync(string folder, CancellationToken ct) => Task.FromResult<IReadOnlyList<string>>([]);
}

public sealed class LocalJob(string dir, string id) : IJobSource
{
    public string JobId => id;
    public Task<byte[]?> ReadAsync(string file, CancellationToken ct)
    {
        var p = Path.Combine(dir, file.Replace('/', Path.DirectorySeparatorChar));
        return Task.FromResult(File.Exists(p) ? File.ReadAllBytes(p) : null);
    }
    public Task<IReadOnlyList<string>> ListAsync(string folder, CancellationToken ct)
    {
        var d = Path.Combine(dir, folder.Replace('/', Path.DirectorySeparatorChar));
        return Task.FromResult<IReadOnlyList<string>>(Directory.Exists(d) ? Directory.GetFiles(d).Select(f => folder.TrimEnd('/') + "/" + Path.GetFileName(f)).ToList() : []);
    }
}

public sealed class StoreJob(IStore store, Paths paths, string id) : IJobSource
{
    public string JobId => id;
    public async Task<IReadOnlyList<string>> ListAsync(string folder, CancellationToken ct) =>
        (await store.ListAsync(paths.Video(id, folder.TrimEnd('/') + "/"), false, false, ct)).Select(b => b.Name[paths.VideoDir(id).Length..]).ToList();
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

    static readonly System.Text.RegularExpressions.Regex NotIntro = new(@"^\s*(not\s+(yet\s+)?(introduced|identified|established|seen|shown|named|known|present|appear\w*|revealed)|(yet\s+)?to\s+be\s+introduced|unknown|unidentified|n/?a|none)\b|^\s*(an?|the)\s+unidentified\b|\b(role|place|identity|standing)\b[^.]*\bis\s+not\s+(yet\s+)?(clear|known|established|revealed)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    /// <summary>A roleAtStart that says the character is not introduced / not yet seen / unknown (older describes): not a line the spoiler-safe view can show.</summary>
    public static bool NotIntroduced(string? roleAtStart) => string.IsNullOrWhiteSpace(roleAtStart) || NotIntro.IsMatch(roleAtStart);

    static string? Str(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : n is JsonObject o && o["en"] is JsonValue ev && ev.TryGetValue<string>(out var es) ? es : null;

    /// <summary>The roleAtStart to publish: the model's own line, else (when it says "not introduced") the description, else the label, else a plain line from the role.
    /// 24FQCFLd7K8 c2: the detector matched, but the description said the same "Not introduced in the opening scenes.", so the line stayed; each fallback is checked in turn.
    /// <paramref name="replaced"/> is true when the model's line was not used.</summary>
    public static JsonNode? SafeRoleAtStart(JsonObject c, out bool replaced)
    {
        var own = Str(c["roleAtStart"]); replaced = false;
        if (!NotIntroduced(own)) return c["roleAtStart"]?.DeepClone();
        replaced = true;
        if (Str(c["description"]) is { } d && !NotIntroduced(d)) return d;
        if (Str(c["label"]) is { } l && !NotIntroduced(l)) return l;
        return Str(c["role"]) switch { "male lead" or "female lead" => "One of the leads.", "main" => "A main character.", _ => "A supporting character." };
    }

    /// <summary>Where a picture came from, so it can be re-cut from a full-HD copy of the same moment: { upload, t, crop: [x, y, w, h] as fractions of the source frame, focus: [x, y] as fractions of the picture }.</summary>
    public static JsonObject Source(string upload, double t, double[] crop, double[] focus) => new()
    {
        ["upload"] = upload, ["t"] = Math.Round(t, 2),
        ["crop"] = new JsonArray(crop.Select(v => (JsonNode)Math.Round(v, 4)).ToArray()), ["focus"] = new JsonArray(focus.Select(v => (JsonNode)Math.Round(v, 3)).ToArray()),
    };

    /// <summary>The head-and-shoulders crop of the cast stage (CastStage.HeadShoulders: box widened 2.5x, 4:5) as fractions of a w x h frame, clamped to the frame; focus is the face centre inside that crop.</summary>
    public static (double[] Crop, double[] Focus) HeadCrop(double[] box, int w, int h)
    {
        var (x0, y0, cw, ch) = CastStage.HeadWindow(box[0], box[1], box[2], box[3], w, h, (double)CastStage.PortraitH / CastStage.PortraitW);
        double fx = box[0] + box[2] / 2, fy = box[1] + box[3] / 2;
        return ([x0 / w, y0 / h, cw / w, ch / h], [Math.Clamp((fx - x0) / cw, 0, 1), Math.Clamp((fy - y0) / ch, 0, 1)]);
    }

    /// <summary>The 9:16 cover crop (CoverFilter's window) as fractions of a w x h frame.</summary>
    public static double[] CoverCrop(int w, int h, double? cx, double? cy)
    {
        double cw = Math.Min(w, h * 9.0 / 16), ch = Math.Min(h, w * 16.0 / 9), x = cx ?? w / 2.0, y = cy ?? h / 2.0;
        double x0 = Math.Min(Math.Max(x - cw / 2, 0), w - cw), y0 = Math.Min(Math.Max(y - ch / 2, 0), h - ch);
        return [x0 / w, y0 / h, cw / w, ch / h];
    }

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
    public static JsonObject BuildDrama(JsonObject describe, JsonObject? info, JsonObject? downloaded, JsonObject? clusters, JsonObject? processed, string jobId, string textSource, bool hasOcr, IReadOnlyDictionary<int, List<(string File, double T, bool Spoiler)>> stills, string by, IReadOnlyDictionary<string, JsonObject>? sources = null)
    {
        double dur = downloaded?["durationSeconds"]?.GetValue<double>() ?? info?["duration"]?.GetValue<double>() ?? 0;
        double cut = describe["cutoffSeconds"]?.GetValue<double>() ?? Describe.Cutoff(dur);
        var cl = (clusters?["clusters"] as JsonArray)?.OfType<JsonObject>().ToDictionary(c => c["id"]!.GetValue<int>()) ?? [];
        var cast = new JsonArray();
        foreach (var c in (describe["cast"] as JsonArray) ?? [])
        {
            int id = c!["cluster"]!.GetValue<int>(); cl.TryGetValue(id, out var f); var grp = Groups(describe).GetValueOrDefault(id) ?? [id];
            var fs = grp.Where(cl.ContainsKey).Select(x => cl[x]).ToList();
            var st = new JsonArray(); foreach (var s in stills.GetValueOrDefault(id) ?? []) { var so = new JsonObject { ["file"] = s.File, ["t"] = s.T, ["spoiler"] = s.Spoiler }; if (sources?.GetValueOrDefault(s.File) is { } src0) so["source"] = src0.DeepClone(); st.Add(so); }
            cast.Add(new JsonObject
            {
                ["id"] = Id(id), ["character"] = c["character"]?.DeepClone(), ["role"] = c["role"]!.DeepClone(), ["roleAtStart"] = SafeRoleAtStart(c.AsObject(), out _), ["description"] = c["description"]!.DeepClone(), ["actor"] = c["actor"]?.DeepClone(),
                ["screenSeconds"] = Math.Round(fs.Sum(x => x["screenSeconds"]?.GetValue<double>() ?? 0), 1), ["firstSeen"] = fs.Count == 0 ? 0 : fs.Min(x => x["first"]!.GetValue<double>()), ["masked"] = Describe.ClusterMasked(describe, id) is { } dm ? dm : f?["masked"]?.DeepClone() ?? false, ["faceCluster"] = id, ["faceClusters"] = new JsonArray(grp.Select(x => (JsonNode)x).ToArray()), ["stills"] = st, ["label"] = c["label"]!.DeepClone(),
                ["portrait"] = sources?.GetValueOrDefault($"cast/{Id(id)}.jpg") is { } ps0 ? new JsonObject { ["file"] = $"cast/{Id(id)}.jpg", ["source"] = ps0.DeepClone() } : null,
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
            ["era"] = describe["era"]?.DeepClone(), ["fantasy"] = describe["fantasy"]?.DeepClone() ?? false, ["aiGenerated"] = describe["aiGenerated"]?["level"]?.DeepClone(), ["ending"] = describe["ending"]?.DeepClone(), ["aiFlag"] = describe["aiGenerated"]?["level"]?.ToString() is "partly" or "yes", ["tags"] = describe["tags"]!.DeepClone(),
            ["cast"] = cast, ["relations"] = rel, ["cutoffSeconds"] = cut,
            ["sources"] = new JsonObject { ["text"] = textSource, ["asrModel"] = processed?["asrModel"]?.DeepClone(), ["ocr"] = hasOcr },
            ["made"] = new JsonObject { ["at"] = Protocol.NowUtc(), ["by"] = by, ["notes"] = $"describe {describe["promptVersion"]}, models {describe["models"]?.ToJsonString()}" },
        };
    }

    /// <summary>The fingerprint of a pack's inputs: when it is unchanged the pack is up to date.</summary>
    public static string Stamp(byte[]? processed, byte[]? describe, byte[]? ourEn = null) =>
        Version + "|" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(processed ?? [])) + "|" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(describe ?? []))
        + (ourEn == null ? "" : "|en:" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(ourEn)));

    public sealed record Result(string Id, bool Written, string? Skipped, IReadOnlyList<string>? Warnings = null);

    public static async Task<Result> PublishOneAsync(Tools tools, IJobSource src, string outDir, bool force, CancellationToken ct, string by = "indexnode")
    {
        var id = src.JobId;
        var processedB = await src.ReadAsync("processed.json", ct); var describeB = await src.ReadAsync("describe.json", ct);
        if (processedB == null) return new(id, false, "no processed.json");
        if (describeB == null) return new(id, false, "no describe.json");
        var dir = Path.Combine(outDir, id); var stampFile = Path.Combine(dir, ".stamp"); var ourEn = await src.ReadAsync(Translate.OutVtt, ct); var stamp = Stamp(processedB, describeB, ourEn);
        if (!force && File.Exists(stampFile) && File.ReadAllText(stampFile) == stamp) return new(id, false, "up to date");
        var tmp = dir + ".tmp"; if (Directory.Exists(tmp)) Directory.Delete(tmp, true); Directory.CreateDirectory(Path.Combine(tmp, "cast"));
        async Task<JsonObject?> J(string f) => await src.ReadAsync(f, ct) is { } b && StoredJson.Parse(b, $"{id}/{f}") is JsonObject o ? o : null;
        async Task<JsonNode?> JN(string f) => await src.ReadAsync(f, ct) is { } b ? StoredJson.Parse(b, $"{id}/{f}") : null;
        var describe = JsonNode.Parse(describeB)!.AsObject(); var processed = JsonNode.Parse(processedB)!.AsObject();
        var info = await J("info.json"); var downloaded = await J("downloaded.json"); var clusters = await J("cast/clusters.json"); var facesDoc = await J("cast/faces.json");
        var subs = SubtitleBand.Parse(await src.ReadAsync("ocr/lines.json", ct), $"{id}/ocr/lines.json"); double frameW = facesDoc?["source"]?["width"]?.GetValue<double>() ?? 360;

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
        // our own translation (translate step, subs/ai/en.vtt) is the default English track; existing English (uploaded or burned-in) stays as the separate "original English" track
        if (ourEn != null) { File.WriteAllText(Path.Combine(tmp, "subs.en.vtt"), CueShaper.ToVtt(CueShaper.Shape(Vtt(ourEn)))); if (en != null) File.WriteAllText(Path.Combine(tmp, "subs.en-orig.vtt"), CueShaper.ToVtt(CueShaper.Shape(Vtt(en)))); }
        else if (en != null) File.WriteAllText(Path.Combine(tmp, "subs.en.vtt"), CueShaper.ToVtt(CueShaper.Shape(Vtt(en))));
        // pictures: portraits, stills (spoiler after the cutoff), thumb
        double cut = describe["cutoffSeconds"]?.GetValue<double>() ?? 0; double dur0 = info?["duration"]?.GetValue<double>() ?? downloaded?["durationSeconds"]?.GetValue<double>() ?? 0; var stills = new Dictionary<int, List<(string, double, bool)>>(); var picks = describe["picks"] as JsonObject; var sources = new Dictionary<string, JsonObject>(); var warnings = new List<string>();
        foreach (var c in (describe["cast"] as JsonArray) ?? [])
        {
            int cid = c!["cluster"]!.GetValue<int>();
            var cand = await JN($"cast/cand_{cid}.json"); Describe.ApplyMasks(cand as JsonArray, describe, cid); MarkSolo(cand as JsonArray, facesDoc, subs, frameW);
            // portrait: the best expression-scored head shot before the late limit; else the cast stage's portrait
            byte[]? pb = null;
            if (ExpressionScores(describe, cid.ToString()) is { } sc && cand is JsonArray ca
                && PickPortrait(ca, picks?[cid.ToString()]?["portrait"] is JsonValue ppv && ppv.TryGetValue<int>(out var ppk) ? ppk : null, dur0, sc) is { } ps)
            {
                if (ps.Score < MinExpression) warnings.Add($"warning: {id} {Id(cid)}: best portrait expression score is {ps.Score} (below {MinExpression}), candidate {ps.K} at t={Math.Round(ps.T)}");
                pb = await src.ReadAsync($"cast/cand_hs_{cid}_{ps.K}.jpg", ct);
                var pcand = ca.OfType<JsonObject>().FirstOrDefault(x => x["k"]?.GetValue<int>() == ps.K);
                if (pb != null && ps.Box is { } pbox && await src.ReadAsync($"cast/cand_{cid}_{ps.K}.jpg", ct) is { } full && JpegSize(full) is var (fw, fh))
                {
                    var (pc, pf) = HeadCrop(pbox, fw, fh);
                    pb = CropTo(full, pc, CastStage.PortraitW, CastStage.PortraitH); // cut from the full frame: never the stored head shot (older ones are padded)
                    if (IsCrowded(pcand) || HasSub(pcand)) // no solo / subtitle-free frame: cut tight so the other faces and the subtitle band are out
                    {
                        (pc, pf) = TightWindow(pbox, Avoid(pcand), (double)CastStage.PortraitH / CastStage.PortraitW, Math.Max(pbox[2], pbox[3] * 0.8) * 2.5, 0.45, fw, fh);
                        pb = CropTo(full, pc, CastStage.PortraitW, CastStage.PortraitH);
                    }
                    sources[$"cast/{Id(cid)}.jpg"] = Source(id, ps.T, pc, pf);
                }
            }
            pb ??= await src.ReadAsync($"cast/portrait_{cid}.jpg", ct);
            if (pb != null) File.WriteAllBytes(Path.Combine(tmp, "cast", $"{Id(cid)}.jpg"), pb); var list = new List<(string, double, bool)>(); int n = 0;
            foreach (var s in (picks?[cid.ToString()]?["stills"] as JsonArray) ?? [])
            {
                if (n >= 3 || s is not JsonValue sv || !sv.TryGetValue<int>(out var k)) continue;
                if (await src.ReadAsync($"cast/cand_{cid}_{k}.jpg", ct) is not { } jb) continue;
                var cc = (cand as JsonNode)?.AsArray().FirstOrDefault(x => x?["k"]?.GetValue<int>() == k); double t = cc?["t"]?.GetValue<double>() ?? 0;
                n++; var name = $"cast/{Id(cid)}_s{n}.jpg"; File.WriteAllBytes(Path.Combine(tmp, name), jb); list.Add((name, t, t > cut));
                var sbox = (cc?["box"] as JsonArray)?.Select(v => v!.GetValue<double>()).ToArray();
                double[] sfocus = [0.5, 0.5]; if (sbox is { Length: 4 } && JpegSize(jb) is var (sw, sh)) sfocus = [Math.Clamp((sbox[0] + sbox[2] / 2) / sw, 0, 1), Math.Clamp((sbox[1] + sbox[3] / 2) / sh, 0, 1)];
                sources[name] = Source(id, t, [0, 0, 1, 1], sfocus);
            }
            stills[cid] = list;
        }
        if (await src.ReadAsync("thumb.jpg", ct) is { } th) File.WriteAllBytes(Path.Combine(tmp, "thumb.jpg"), th);
        var cover = await CoversAsync(src, describe, facesDoc, subs, frameW, tmp, id, info?["duration"]?.GetValue<double>() ?? downloaded?["durationSeconds"]?.GetValue<double>() ?? 0, ct);
        Poster(describe, tmp); // poster.jpg stays for one release (compatibility)
        var drama = BuildDrama(describe, info, downloaded, clusters, processed, id, ocrZh.Count > 0 ? "ocr" : "transcript", ocrZh.Count > 0, stills, by, sources);
        if (cover != null) drama["cover"] = cover;
        File.WriteAllText(Path.Combine(tmp, "drama.json"), drama.ToJsonString(Opt));
        File.WriteAllText(Path.Combine(tmp, ".stamp"), stamp);
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
        Directory.Move(tmp, dir);
        return new(id, true, null, warnings);
    }

    static string Srt2Vtt(string srt) => "WEBVTT\n\n" + System.Text.RegularExpressions.Regex.Replace(srt.Replace("\r", ""), @"(\d\d:\d\d:\d\d),(\d\d\d)", "$1.$2");

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

    /// <summary>A candidate qualifies as solo when no other detected face in its frame (cast/faces.json, same time) has more than this fraction of the candidate's face area.</summary>
    public const double SoloMaxRatio = 0.25;

    /// <summary>Marks each candidate "solo" (true/false) and lists the boxes of the other faces on its frame as "others" (only those that break the rule are not needed: all are listed, so a tight crop can avoid every one). Uses the stored faces.json; without it the candidates stay unmarked.</summary>
    public static void MarkSolo(JsonArray? cands, JsonObject? facesDoc, SubtitleBand? subs = null, double frameW = 360)
    {
        if (cands != null && subs != null) // burned-in subtitle on screen at the candidate's time: "sub" = [x, y, w, h] in frame pixels (cast's own value, when there, wins)
            foreach (var c in cands.OfType<JsonObject>())
                if (c["sub"] == null && c["t"] != null && subs.At(c["t"]!.GetValue<double>(), frameW) is { } sb) c["sub"] = new JsonArray(sb.Select(v => (JsonNode)v).ToArray());
        if (cands == null || facesDoc?["faces"] is not JsonArray fa) return;
        var byT = fa.OfType<JsonObject>().Where(f => f["box"] is JsonArray { Count: 4 }).GroupBy(f => Math.Round(f["t"]!.GetValue<double>(), 2)).ToDictionary(g => g.Key, g => g.Select(f => ((JsonArray)f["box"]!).Select(v => v!.GetValue<double>()).ToArray()).ToList());
        foreach (var c in cands.OfType<JsonObject>())
        {
            if ((c["box"] as JsonArray)?.Select(v => v!.GetValue<double>()).ToArray() is not { Length: 4 } b) continue;
            var others = (byT.GetValueOrDefault(Math.Round(c["t"]?.GetValue<double>() ?? 0, 2)) ?? []).Where(o => Math.Abs(o[0] + o[2] / 2 - (b[0] + b[2] / 2)) > 2 || Math.Abs(o[1] + o[3] / 2 - (b[1] + b[3] / 2)) > 2).ToList();
            if (c["solo"] == null) c["solo"] = !others.Any(o => o[2] * o[3] > SoloMaxRatio * b[2] * b[3]); // cast (new dramas) judged every detection of the frame, faces.json holds only kept faces
            c["others"] = new JsonArray(others.Select(o => (JsonNode)new JsonArray(o.Select(v => (JsonNode)v).ToArray())).ToArray());
        }
    }

    /// <summary>The candidate pool: the solo ones when there are any, else all (today's choice; the picture is then cut tight).</summary>
    static List<JsonObject> SoloPool(List<JsonObject> all) =>
        all.Where(c => c["solo"]?.GetValue<bool>() != false && !SubOnFace(c)).ToList() is { Count: > 0 } a ? a
        : all.Where(c => c["solo"]?.GetValue<bool>() != false).ToList() is { Count: > 0 } s ? s : all;

    /// <summary>True when the candidate's subtitle band overlaps its face box: the only subtitle that counts against a frame (a band elsewhere is cropped out).</summary>
    public static bool SubOnFace(JsonObject? c) => SubBox(c) is { } s && (c!["box"] as JsonArray) is { Count: 4 } b && b.Select(v => v!.GetValue<double>()).ToArray() is var f && s[0] < f[0] + f[2] && s[0] + s[2] > f[0] && s[1] < f[1] + f[3] && s[1] + s[3] > f[1];

    public static bool HasSub(JsonObject? c) => c?["sub"] is JsonArray { Count: 4 };

    public static double[]? SubBox(JsonObject? c) => c?["sub"] is JsonArray { Count: 4 } a ? a.Select(v => v!.GetValue<double>()).ToArray() : null;

    /// <summary>What a tight crop must keep out: the other faces of a crowded frame, and the subtitle band when one is on screen.</summary>
    public static List<double[]> Avoid(JsonObject? c) { var l = IsCrowded(c) ? OtherBoxes(c) : []; if (SubBox(c) is { } s) l.Add(s); return l; }

    public static bool IsCrowded(JsonObject? c) => c?["solo"]?.GetValue<bool>() == false;

    public static List<double[]> OtherBoxes(JsonObject? c) => (c?["others"] as JsonArray)?.Select(o => ((JsonArray)o!).Select(v => v!.GetValue<double>()).ToArray()).ToList() ?? [];

    /// <summary>The largest window (ratio = height / width, up to maxW wide) around a face that holds none of the other faces, shrinking toward 1.3x the face; the face centre sits yOff face-heights above the window centre. Returns crop fractions of the w x h frame and the face centre inside the window.</summary>
    public static (double[] Crop, double[] Focus) TightWindow(double[] box, IReadOnlyList<double[]> others, double ratio, double maxW, double yOff, int w, int h)
    {
        double bw = box[2], bh = box[3], fx = box[0] + bw / 2, fy = box[1] + bh / 2, face = Math.Max(bw, bh * 0.8);
        maxW = Math.Min(Math.Min(maxW, w), h / ratio); double min = Math.Min(face * 1.3, maxW), cw = maxW;
        (double X, double Y, double W, double H) Win(double cw0, double yo)
        {
            double ch0 = cw0 * ratio, x0 = CastStage.Within(fx - cw0 / 2, w - cw0), y0 = CastStage.Within(fy + bh * yo - ch0 / 2, h - ch0);
            return (x0, y0, cw0, ch0);
        }
        bool Hits(double cw0, double yo) { var r = Win(cw0, yo); return others.Any(o => o[0] < r.X + r.W && o[0] + o[2] > r.X && o[1] < r.Y + r.H && o[1] + o[3] > r.Y); }
        // largest window first; at each size try the head-and-shoulders offset, then centred on the face, then shifted up (a subtitle band sits below the face)
        double[] offs = [yOff, yOff / 2, 0, -0.2, -0.4]; double yBest = yOff;
        for (; ; cw = Math.Max(min, cw * 0.95))
        {
            var ok = offs.Where(o => !Hits(cw, o)).Cast<double?>().FirstOrDefault();
            if (ok != null) { yBest = ok.Value; break; }
            if (cw <= min) break;
        }
        var k = Win(cw, yBest);
        return ([k.X / w, k.Y / h, k.W / w, k.H / h], [Math.Clamp((fx - k.X) / k.W, 0, 1), Math.Clamp((fy - k.Y) / k.H, 0, 1)]);
    }

    /// <summary>The candidate for a lead's cover: describe's portrait pick when it is unmasked, else the best-quality unmasked candidate, else the pick. Null: no candidates.</summary>
    public static (int K, double T, double[]? Box)? PickLeadShot(JsonArray? cands, int? picked)
    {
        var all = SoloPool(cands?.OfType<JsonObject>().ToList() ?? []);
        if (all.Count == 0) return null;
        JsonObject? c = all.FirstOrDefault(x => x["k"]?.GetValue<int>() == picked && x["masked"]?.GetValue<bool>() != true)
            ?? all.Where(x => x["masked"]?.GetValue<bool>() != true).OrderByDescending(x => x["quality"]?.GetValue<double>() ?? 0).FirstOrDefault()
            ?? all.FirstOrDefault(x => x["k"]?.GetValue<int>() == picked) ?? all[0];
        var box = (c["box"] as JsonArray)?.Select(v => v!.GetValue<double>()).ToArray();
        return (c["k"]!.GetValue<int>(), c["t"]?.GetValue<double>() ?? 0, box is { Length: 4 } ? box : null);
    }

    /// <summary>Expression-scored cover pick. The score floor comes first: only unmasked candidates scoring at least MinExpression compete, and the tiers relax within it: alone with no burned-in subtitle, alone, any frame (cut tight by the caller). Inside a tier the windows run in order (start, first half, then up to the late limit, 75%); the best score wins, ties go to describe's pick, then quality. Nothing scores the floor: null.</summary>
    public static (int K, double T, double[]? Box, string Window)? PickByExpression(JsonArray? cands, int? picked, double cutoff, double duration, IReadOnlyList<int?> scores)
    {
        var all = cands?.OfType<JsonObject>().ToList() ?? [];
        var windows = CoverWindows(cutoff, duration).ToList(); if (duration > 0) windows.Add(("late", duration * CoverLateFraction));
        foreach (var tier in Tiers)
            foreach (var (name, maxT) in windows)
            {
                var limit = Math.Min(maxT, duration > 0 ? duration * CoverLateFraction : double.MaxValue);
                var best = Ranked(all, picked, scores, tier, MinExpression).Where(x => (x.C["t"]?.GetValue<double>() ?? 0) <= limit).Cast<(JsonObject C, int K, int S)?>().FirstOrDefault();
                if (best is not { } b) continue;
                var box = (b.C["box"] as JsonArray)?.Select(v => v!.GetValue<double>()).ToArray();
                return (b.K, b.C["t"]?.GetValue<double>() ?? 0, box is { Length: 4 } ? box : null, name);
            }
        // tier 4: nothing scores the floor anywhere before the late limit: the best score overall (the caller warns)
        var lateLimit = duration > 0 ? duration * CoverLateFraction : double.MaxValue;
        if (Ranked(all.Where(c => (c["t"]?.GetValue<double>() ?? 0) <= lateLimit).ToList(), picked, scores, Tiers[^1], int.MinValue).Cast<(JsonObject C, int K, int S)?>().FirstOrDefault() is { } f)
        {
            var fb = (f.C["box"] as JsonArray)?.Select(v => v!.GetValue<double>()).ToArray();
            return (f.K, f.C["t"]?.GetValue<double>() ?? 0, fb is { Length: 4 } ? fb : null, "low");
        }
        return null;
    }

    /// <summary>The tiers, strictest first: alone with no subtitle on the face, alone, any frame. A subtitle elsewhere on the frame never rules a candidate out (the crop keeps the band outside).</summary>
    static readonly Func<JsonObject, bool>[] Tiers = [c => !IsCrowded(c) && !SubOnFace(c), c => !IsCrowded(c), _ => true];

    /// <summary>Unmasked, scored candidates passing a tier and at least the floor, best first: score, then describe's pick, then quality.</summary>
    static IEnumerable<(JsonObject C, int K, int S)> Ranked(List<JsonObject> all, int? picked, IReadOnlyList<int?> scores, Func<JsonObject, bool> tier, int floor) =>
        all.Where(c => c["masked"]?.GetValue<bool>() != true && tier(c))
            .Select(c => (C: c, K: c["k"]!.GetValue<int>())).Select(x => (x.C, x.K, S: x.K >= 0 && x.K < scores.Count ? scores[x.K] : null)).Where(x => x.S != null && x.S >= floor).Select(x => (x.C, x.K, S: x.S!.Value))
            .OrderByDescending(x => x.S).ThenByDescending(x => x.K == picked).ThenByDescending(x => x.C["quality"]?.GetValue<double>() ?? 0);

    /// <summary>The crop (fractions of the frame) Lanczos-resized to w x h and JPEG-encoded.</summary>
    public static byte[] CropTo(byte[] jpg, double[] crop, int w, int h)
    {
        var im = Imaging.DecodeJpeg(jpg);
        return Imaging.EncodeJpeg(Imaging.CropResizeLanczos(im, crop[0] * im.W, crop[1] * im.H, crop[2] * im.W, crop[3] * im.H, w, h));
    }

    /// <summary>The portrait. The score floor comes first: among the unmasked candidates before the late limit that score at least MinExpression, the tiers relax alone and no subtitle, alone, any frame (cut tight by the caller); the best score wins, describe's pick breaks a tie, then quality. Only when nothing scores the floor: the best score overall (any frame), which the caller warns about. Null: no scores for any candidate.</summary>
    public static (int K, double T, double[]? Box, int Score)? PickPortrait(JsonArray? cands, int? picked, double duration, IReadOnlyList<int?> scores)
    {
        double limit = duration > 0 ? duration * CoverLateFraction : double.MaxValue;
        var all = (cands?.OfType<JsonObject>() ?? []).Where(c => (c["t"]?.GetValue<double>() ?? 0) <= limit).ToList();
        foreach (var (tier, floor) in Tiers.Select(t => (t, MinExpression)).Append((Tiers[^1], int.MinValue)))
        {
            var best = Ranked(all, picked, scores, tier, floor).Cast<(JsonObject C, int K, int S)?>().FirstOrDefault();
            if (best is not { } b) continue;
            var box = (b.C["box"] as JsonArray)?.Select(v => v!.GetValue<double>()).ToArray();
            return (b.K, b.C["t"]?.GetValue<double>() ?? 0, box is { Length: 4 } ? box : null, b.S);
        }
        return null;
    }

    /// <summary>The 9:16 cover: decode the stored frame, crop CoverCrop's window, Lanczos-resize to 360x640, encode JPEG. Same crop box as CoverFilter, no ffmpeg.</summary>
    public static byte[] CropCover(byte[] jpg, double? cx, double? cy)
    {
        var im = Imaging.DecodeJpeg(jpg); var c = CoverCrop(im.W, im.H, cx, cy);
        return Imaging.EncodeJpeg(Imaging.CropResizeLanczos(im, c[0] * im.W, c[1] * im.H, c[2] * im.W, c[3] * im.H, CoverW, CoverH));
    }

    /// <summary>cover/lead1.jpg (female lead) and lead2.jpg (male lead): two separate 9:16 crops at 360x640, no text (no together cover). Each entry is { file, t, window, cast, focus: [x, y] }, the face centre as fractions of the image. A lead without a flattering shot is omitted. Returns the drama.json "cover" object, or null when there is none.</summary>
    static async Task<JsonObject?> CoversAsync(IJobSource src, JsonObject describe, JsonObject? facesDoc, SubtitleBand? subs, double frameW, string tmp, string upload, double dur, CancellationToken ct)
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
            var cands = await src.ReadAsync($"cast/cand_{cid}.json", ct) is { } cb ? StoredJson.Parse(cb, $"cast/cand_{cid}.json") as JsonArray : null; Describe.ApplyMasks(cands, describe, cid); MarkSolo(cands, facesDoc, subs, frameW);
            int? pk = picks?[cid.ToString()]?["portrait"] is JsonValue pv && pv.TryGetValue<int>(out var p) ? p : null;
            var shot = PickCoverShot(cands, pk, cutoff, dur, ExpressionScores(describe, cid.ToString()));
            if (shot is not { } s || await src.ReadAsync($"cast/cand_{cid}_{s.K}.jpg", ct) is not { } img) return; // no shot before the late limit: no cover (the portrait has no known time)
            double? t = s.T; double[]? box = s.Box;
            var file = $"cover/{key}.jpg";
            double? fx = box == null ? null : box[0] + box[2] / 2, fy = box == null ? null : box[1] + box[3] / 2;
            var scand = (cands ?? []).OfType<JsonObject>().FirstOrDefault(x => x["k"]?.GetValue<int>() == s.K);
            double[]? tight = null; double[] focus = [0.5, 0.5];
            if ((IsCrowded(scand) || HasSub(scand)) && box != null && JpegSize(img) is var (tw, th)) // no solo frame: tight 9:16 window clear of the other faces
            {
                var (tc, tf) = TightWindow(box, Avoid(scand), 16.0 / 9, Math.Min(tw, th * 9.0 / 16), 0.0, tw, th); tight = tc; focus = tf;
                File.WriteAllBytes(Path.Combine(tmp, file), CropTo(img, tc, CoverW, CoverH));
            }
            else
            {
                File.WriteAllBytes(Path.Combine(tmp, file), CropCover(img, fx, fy));
                if (JpegSize(img) is var (iw, ih)) focus = CoverFocus(iw, ih, fx, fy);
            }
            cover[key] = new JsonObject { ["file"] = file, ["t"] = Math.Round(s.T, 2), ["window"] = s.Window, ["cast"] = Id(cid), ["focus"] = new JsonArray(Math.Round(focus[0], 3), Math.Round(focus[1], 3)) };
            if (JpegSize(img) is var (sw, sh)) cover[key]!["source"] = Source(upload, s.T, tight ?? CoverCrop(sw, sh, fx, fy), focus);
        }
        await Lead(f, "lead1"); await Lead(m, "lead2");
        return cover.Count == 0 ? null : cover;
    }

    /// <summary>The picture scaled to cover w x h (centre crop), as Lanczos pixels.</summary>
    static Rgb Fill(Rgb im, int w, int h)
    {
        double sc = Math.Max((double)w / im.W, (double)h / im.H), cw = w / sc, ch = h / sc;
        return Imaging.CropResizeLanczos(im, (im.W - cw) / 2, (im.H - ch) / 2, cw, ch, w, h);
    }

    /// <summary>poster.jpg, 600x800, no text: the two leads' portraits side by side (297 px each) with a 6 px dark gutter (0x101010). Built in-process.</summary>
    static void Poster(JsonObject describe, string tmp)
    {
        var cast = (describe["cast"] as JsonArray)?.OfType<JsonObject>().ToList() ?? [];
        var f = cast.FirstOrDefault(c => c["role"]?.GetValue<string>() == "female lead"); var m = cast.FirstOrDefault(c => c["role"]?.GetValue<string>() == "male lead");
        if (f == null || m == null) return;
        var a = Path.Combine(tmp, "cast", Id(f["cluster"]!.GetValue<int>()) + ".jpg"); var b = Path.Combine(tmp, "cast", Id(m["cluster"]!.GetValue<int>()) + ".jpg");
        if (!File.Exists(a) || !File.Exists(b)) return;
        var l = Fill(Imaging.DecodeJpeg(File.ReadAllBytes(a)), 297, 800); var r = Fill(Imaging.DecodeJpeg(File.ReadAllBytes(b)), 297, 800);
        var o = Rgb.Blank(600, 800, 0x10);
        for (int y = 0; y < 800; y++) { Buffer.BlockCopy(l.Data, y * 297 * 3, o.Data, y * 600 * 3, 297 * 3); Buffer.BlockCopy(r.Data, y * 297 * 3, o.Data, (y * 600 + 303) * 3, 297 * 3); }
        File.WriteAllBytes(Path.Combine(tmp, "poster.jpg"), Imaging.EncodeJpeg(o));
    }

    /// <summary>packs.json at the top: one entry per pack folder present.</summary>
    public static void WriteIndex(string outDir)
    {
        var packs = new JsonArray();
        foreach (var d in Directory.GetDirectories(outDir).OrderBy(x => x))
        {
            var f = Path.Combine(d, "drama.json"); if (!File.Exists(f)) continue;
            var j = JsonNode.Parse(File.ReadAllText(f))!.AsObject();
            packs.Add(new JsonObject { ["packId"] = Path.GetFileName(d), ["title"] = j["title"]?.DeepClone(), ["era"] = j["era"]?.DeepClone(), ["fantasy"] = j["fantasy"]?.DeepClone(), ["aiGenerated"] = j["aiGenerated"]?.DeepClone(), ["ending"] = j["ending"]?.DeepClone(), ["aiFlag"] = j["aiFlag"]?.DeepClone(), ["durationSeconds"] = j["durationSeconds"]?.DeepClone(), ["madeAt"] = j["made"]?["at"]?.DeepClone() });
        }
        File.WriteAllText(Path.Combine(outDir, "packs.json"), new JsonObject { ["v"] = 0, ["packs"] = packs }.ToJsonString(Opt));
    }
}
