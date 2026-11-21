using System.Diagnostics;

namespace IndexNode;

/// <summary>The desktop implementation of the download pre-check: the node's own frames step on the sample clip, its own int8 SSCD, the lookup in the cached search index, the AI probe.</summary>
public sealed class PreCheckAnalyzer(Tools tools, NodeConfig cfg, HttpClient http, IStore store, Paths paths, SearchIndexCache cache) : IPreCheckAnalyzer
{
    /// <summary>Embeds the extracted frames (000000.jpg ...) as f16 rows. Default: the node's own int8 SSCD. Tests replace it (no model download in tests).</summary>
    public delegate Task<byte[]> Embedder(string framesDir, int count, BarCrop box, int threads, Action<string> progress, CancellationToken ct);
    public Embedder? EmbedOverride { get; init; }
    public bool SkipModelCheck { get; init; }
    /// <summary>The service: frames are decoded/cropped/resized in managed code and the audio is a 16 kHz mono WAV read in memory, so a request starts no child process (no ffmpeg, no ffprobe). The node's own path (false) keeps ffmpeg for the sample it cut itself.</summary>
    public bool InProcess { get; init; }

    /// <summary>Tests replace the SenseVoice step: (sample path, audio seconds) -> segments. Default: the real VAD + SenseVoice, models downloaded on first use.</summary>
    public Func<string, CancellationToken, Task<IReadOnlyList<LangChunk>>>? SpeechOverride { get; init; }

    /// <summary>The language ID on ~60 s from the middle of the clip. Never throws: a model that cannot be downloaded or loaded gives Unavailable (the record then falls back to the metadata and says why).</summary>
    public async Task<SpeechResult> SpeechAsync(string samplePath, int threads, double seconds, double minSeconds, CancellationToken ct)
    {
        try
        {
            float[]? pcm = InProcess && SpeechOverride == null ? InProcessMedia.ReadWav(await File.ReadAllBytesAsync(samplePath, ct)) : null;
            double dur = pcm != null ? pcm.Length / 16000.0 : await Frames.DurationAsync(tools, samplePath, ct);   // the whole window is read; `seconds` is seconds of SPEECH (audio chunks, no VAD)
            IReadOnlyList<LangChunk> segs;
            if (SpeechOverride != null) segs = await SpeechOverride(samplePath, ct);
            else
            {
                if (!cfg.ModelsAutoDownload && !Models.Has(Models.AsrModel)) return new SpeechResult(null, false, 0, 0, 0, "SenseVoice model not present and models.auto-download is off");
                var model = await Models.EnsureAsync(Models.AsrModel, http, ct); var tokens = await Models.EnsureAsync(Models.AsrTokens, http, ct);
                var audio = pcm ?? await Transcribe.ReadAudioAsync(tools, samplePath, 0, dur, ct);
                segs = Transcribe.LanguageId(model, tokens, audio, Math.Max(1, threads), seconds, ct);
            }
            var (lang, dubbed, source, secs, used, none) = PreCheck.LanguageVerdict(segs, minSeconds);   // only Speech-tagged chunks count; under ~10 s: no-speech; a weak non-zh call: unclear
            return new SpeechResult(lang, dubbed, segs.Count, secs, dur, null) { ChunksUsed = used, ChunksNoSpeech = none, Source = source, Chunks = segs };
        }
        catch (Exception e) when (e is not OperationCanceledException) { return new SpeechResult(null, false, 0, 0, 0, "SenseVoice unavailable: " + e.Message); }
    }
    /// <summary>`precheck --exclude-self`: the upload whose own centers are left out of the lookup (its id), and how many centers that dropped in the last analysis.</summary>
    public string? ExcludeId { get; set; }
    /// <summary>0.13.1: the uploads whose centers finished after the index was built (the node: from the queue log; the argument is the index's build time, null = unknown, then every recent one). Searched by brute force; the subject itself is left out.</summary>
    public Func<DateTimeOffset?, IReadOnlyList<string>>? FreshUploads { get; set; }
    public int Excluded { get; private set; }

    public async Task<string?> UnavailableAsync(PreCheckSettings s, CancellationToken ct)
    {
        SearchData? idx;
        try { idx = await cache.GetAsync(store, paths, ct); }
        catch (Exception e) when (e is not OperationCanceledException) { return "search index unreadable: " + e.Message; }
        if (idx == null) return "no search index";
        if (idx.Count == 0) return "the search index is empty";
        if (SkipModelCheck) return null;
        try
        {
            if (!cfg.ModelsAutoDownload && string.IsNullOrEmpty(cfg.ModelPath) && !File.Exists(Models.PathOf(Models.SscdInt8)) && !File.Exists(Path.Combine(NodeConfig.ToolsDir(), Sscd.ModelName + ".onnx"))) return "no SSCD model (automatic model download is off)";
            await Sscd.EnsureModelAsync(cfg, http, ct);
        }
        catch (Exception e) when (e is not OperationCanceledException) { return "no SSCD model: " + e.Message; }
        return null;
    }

    /// <summary>The models this analyzer has loaded (the service's /v1/health).</summary>
    public List<string> Loaded { get; } = [];
    /// <summary>The service's start: every model into memory (SSCD int8, SenseVoice, the AI probe) and the search index, so the first request is as fast as the next. A model that cannot load is left out of <see cref="Loaded"/> (that analysis then falls back as before).</summary>
    public async Task WarmAsync(int threads, CancellationToken ct)
    {
        if (EmbedOverride == null) { var p = await Sscd.EnsureModelAsync(cfg, http, ct); OrtOptions.Shared(p, threads); Loaded.Add("sscd-int8"); }
        if (SpeechOverride == null)
        {
            try { if (cfg.ModelsAutoDownload || Models.Has(Models.AsrModel)) { var m = await Models.EnsureAsync(Models.AsrModel, http, ct); var t = await Models.EnsureAsync(Models.AsrTokens, http, ct); Transcribe.SharedRecognizer(m, t, Math.Max(1, threads)); Loaded.Add("sensevoice"); } }
            catch (Exception e) when (e is not OperationCanceledException) { Log.Info("serve: SenseVoice not loaded: " + e.Message); }
        }
        _ = AiProbe.Default; Loaded.Add("ai-frames-v3");
        await cache.GetAsync(store, paths, ct);
    }

    /// <summary>Shared LRU of member blocks across requests (the service); null = a cache per call (the node).</summary>
    public BlockCache? Blocks { get; init; }

    /// <summary>The node's own frames step on the clip (<see cref="PreCheckFrames"/>), then SSCD: (f16 rows, frame times). Fills secs["frames"] and secs["sscd"].</summary>
    public async Task<(byte[] F16, List<double> Times)> VectorsAsync(string samplePath, string work, int threads, Action<string> progress, Dictionary<string, double> secs, CancellationToken ct, int maxFrames = 0)
    {
        var f = await PreCheckFrames.ExtractAsync(tools, cfg, samplePath, work, threads, maxFrames, progress, secs, ct);
        if (f.Count == 0) return ([], []);
        var sw = Stopwatch.StartNew();
        var f16 = await EmbedAsync(f.Dir, f.Count, f.Box, threads, progress, ct);
        secs["sscd"] = sw.Elapsed.TotalSeconds;
        return (f16, f.Times);
    }

    async Task<byte[]> EmbedAsync(string fdir, int count, BarCrop box, int threads, Action<string> progress, CancellationToken ct)
    {
        progress("sscd");
        if (EmbedOverride != null) return await EmbedOverride(fdir, count, box, threads, p => progress("sscd " + p), ct);
        if (InProcess) return await Sscd.DescribeInProcessAsync(await Sscd.EnsureModelAsync(cfg, http, ct), fdir, count, threads, p => progress("sscd " + p), ct, box);
        return await Sscd.DescribeAsync(tools,
 await Sscd.EnsureModelAsync(cfg, http, ct), fdir, count, threads, p => progress("sscd " + p), ct, box);
    }

    public async Task<PreCheckAnalysis> AnalyzeAsync(string samplePath, PreCheckSettings s, int threads, Action<string> progress, CancellationToken ct)
    {
        if (await cache.GetAsync(store, paths, ct) == null) return PreCheckAnalysis.Skipped("no search index");
        var secs = new Dictionary<string, double>();
        var work = Path.Combine(Path.GetTempPath(), "indexnode-precheck-" + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(work);
        try
        {
            var f = await PreCheckFrames.ExtractAsync(tools, cfg, samplePath, work, threads, s.Frames, progress, secs, ct);
            var a = await AnalyzeFramesAsync(f.Dir, f.Count, f.Box, samplePath, s, threads, progress, secs, ExcludeId, ct);
            Excluded = a.Excluded;
            return a;
        }
        finally { try { Directory.Delete(work, true); } catch { } }
    }

    /// <summary>
    /// The analysis proper, on frames already extracted (000000.jpg ... in <paramref name="fdir"/>) and an audio file: bar box (from the frames when not given), SSCD, lookup, AI probe, language ID.
    /// The service runs exactly this on what a node sent; the in-process path runs it on the sample it just extracted.
    /// </summary>
    public async Task<PreCheckAnalysis> AnalyzeFramesAsync(string fdir, int count, BarCrop? box, string? audioPath, PreCheckSettings s, int threads, Action<string> progress, Dictionary<string, double> secs, string? excludeId, CancellationToken ct, IReadOnlyList<SegInfo>? segInfo = null)
    {
        var idx = await cache.GetAsync(store, paths, ct);
        if (idx == null) return PreCheckAnalysis.Skipped("no search index");
        if (count == 0) return new PreCheckAnalysis(null, idx.Version, 0, 0, [], 0, secs);
        var sw = Stopwatch.StartNew();
        var crop = box ?? FrameBox(fdir, count);
        var f16 = await EmbedAsync(fdir, count, crop, threads, progress, ct);
        secs["sscd"] = sw.Elapsed.TotalSeconds; sw.Restart();
        var rows = AiProbe.RowsFromF16(f16);
        if (rows.Length == 0) return new PreCheckAnalysis(null, idx.Version, 0, 0, [], 0, secs);
        foreach (var r in rows) Sscd.L2Normalize(r);
        progress("lookup");
        int ex = excludeId == null ? -1 : idx.Uploads.FindIndex(u => u.Id == excludeId);
        int excluded = ex < 0 ? 0 : idx.RefUpload.Count(u => u == ex);
        var fc0 = FramesCache.Current is { } fcA ? (fcA.Hits, fcA.RangedMisses, fcA.FillsQueued) : (0, 0, 0);
        var (best, st) = await PreCheckSearch.BestPerFrameAsync(store, paths, idx, rows, s.TopCenters, threads, ct, ex, Blocks);
        var fresh = new List<string>();
        if (FreshUploads != null)
        {
            var have = idx.Uploads.Select(u => u.Id).ToHashSet(StringComparer.Ordinal);
            fresh = FreshUploads(idx.BuiltAt).Where(id => id != excludeId && !have.Contains(id)).Distinct(StringComparer.Ordinal).ToList();
        }
        var used = fresh.Count == 0 ? [] : await PreCheckSearch.MergeFreshAsync(store, paths, idx, fresh, rows, best, threads, ct);
        secs["freshUploads"] = used.Count;
        var (matches, noHit) = PreCheckSearch.Shares(idx, best, s.Tau, used);
        // duration and language of the matches: the catalog of the searched version; the uploads newer than it from their precheck.json / download.zip (never a hydrate)
        var freshIds = used.Select(u => u.Id).ToHashSet(StringComparer.Ordinal);
        for (int i = 0; i < matches.Count && i < 5; i++)
        {
            var m = matches[i];
            if (idx.Catalog?.Find(m.Upload) is { DurationSeconds: not null } ce && !freshIds.Contains(m.Upload)) matches[i] = m with { Duration = ce.DurationSeconds, SpeechLanguage = ce.SpeechLanguage ?? m.SpeechLanguage, Dubbed = ce.Dubbed ?? m.Dubbed };
            else
            {
                var p = await CatalogBuild.ReadPrecheckAsync(store, paths, m.Upload, ct);
                matches[i] = m with { Duration = await CatalogBuild.DurationAsync(store, paths, m.Upload, p, ct), SpeechLanguage = p.Language ?? m.SpeechLanguage, Dubbed = p.Language != null ? p.Dubbed : m.Dubbed };
            }
        }
        var gs = await GroupStore.LoadAsync(store, paths, idx.Version, ct);
        var upIds = idx.Uploads.Select(u => u.Id).Concat(used.Select(u => u.Id)).ToList();
        var hits = new List<FrameHit>();
        for (int i = 0; i < best.Length; i++) if (best[i] is { } b && b.Cosine >= s.Tau && b.Upload >= 0 && b.Upload < upIds.Count) hits.Add(new FrameHit(i, upIds[b.Upload], b.Time, b.Cosine));
        var ofGroup = gs?.GroupOf() ?? new Dictionary<string, string>(StringComparer.Ordinal);
        var ug = hits.Select(h => h.Upload).Distinct(StringComparer.Ordinal).ToDictionary(u => u, u => ofGroup.GetValueOrDefault(u), StringComparer.Ordinal);
        var gm = (gs?.Groups ?? []).Where(g => ug.Values.Contains(g.Id)).ToDictionary(g => g.Id, g => (IReadOnlyList<GroupCopy>)g.Members.Select(m => new GroupCopy(m.Upload, m.SpeechLanguage, m.Dubbed, m.Duration)).ToList(), StringComparer.Ordinal);
        secs["lookup"] = sw.Elapsed.TotalSeconds; secs["lookupScan"] = st.ScanMs / 1000; secs["lookupRead"] = st.ReadMs / 1000; secs["lookupScore"] = st.ScoreMs / 1000; secs["lookupRequests"] = st.Requests; secs["lookupBytes"] = st.Bytes;
        if (FramesCache.Current is { } fc1) { secs["cacheHits"] = fc1.Hits - fc0.Item1; secs["cacheRanged"] = fc1.RangedMisses - fc0.Item2; secs["fillsQueued"] = fc1.FillsQueued - fc0.Item3; }
        sw.Restart();
        double ai = AiProbe.Default.Score(rows);
        secs["ai"] = sw.Elapsed.TotalSeconds; sw.Restart();
        SpeechResult? speech = null;
        IReadOnlyList<PreCheckSegment>? segments = null;
        if (segInfo != null)
        {
            sw.Restart();
            segments = await SegmentsAsync(store, paths, segInfo, best, upIds, ug, gm, excludeId, rows, s, threads, secs, ct);
            secs["members"] = sw.Elapsed.TotalSeconds; sw.Restart();
        }
        if (audioPath != null) { progress("language"); speech = await SpeechAsync(audioPath, threads, s.LanguageSeconds, s.LanguageMinSeconds, ct); secs["speech"] = sw.Elapsed.TotalSeconds; }
        return new PreCheckAnalysis(null, idx.Version, rows.Length, noHit, matches, ai, secs) { Speech = speech, Excluded = excluded, Hits = hits, UploadGroups = ug, GroupMembers = gm, Segments = segments };
    }

    /// <summary>One sample segment as the analysis sees it (frames are numbered in time order across the segments).</summary>
    public sealed record SegInfo(double Position, double Start, double End, int Frames);

    /// <summary>
    /// 0.13.15: per segment and per upload the share of the segment's frames matched at Tau and the median matched time, from the lookup's per-frame best. Every member of every candidate group (a group either segment matched) is then
    /// measured: a member the lookup already shows at >= SkipShare in every segment keeps those numbers; any other is scored directly from its frames file (through FramesCache: a cached file costs no request), because copies of one drama
    /// tie in the lookup and a member must never be skipped just because a sibling ranked higher.
    /// </summary>
    public static async Task<List<PreCheckSegment>> SegmentsAsync(IStore store, Paths paths, IReadOnlyList<SegInfo> info, FrameBest?[] best, List<string> upIds, Dictionary<string, string?> ug, Dictionary<string, IReadOnlyList<GroupCopy>> gm, string? excludeId, float[][] rows, PreCheckSettings s, int threads, Dictionary<string, double> secs, CancellationToken ct)
    {
        var ranges = new List<(int From, int Count)>(); int at = 0; foreach (var si in info) { ranges.Add((at, si.Frames)); at += si.Frames; }
        static double Median(List<double> v) { v.Sort(); return v.Count == 0 ? 0 : v.Count % 2 == 1 ? v[v.Count / 2] : (v[v.Count / 2 - 1] + v[v.Count / 2]) / 2; }
        var shares = info.Select(_ => new Dictionary<string, SegmentShare>(StringComparer.Ordinal)).ToList();
        for (int k = 0; k < info.Count; k++)
        {
            var by = new Dictionary<string, List<double>>(StringComparer.Ordinal);
            for (int i = ranges[k].From; i < ranges[k].From + ranges[k].Count && i < best.Length; i++)
                if (best[i] is { } b && b.Cosine >= s.Tau && b.Upload >= 0 && b.Upload < upIds.Count && upIds[b.Upload] != excludeId) { if (!by.TryGetValue(upIds[b.Upload], out var l)) by[upIds[b.Upload]] = l = []; l.Add(b.Time); }
            foreach (var (u, l) in by) shares[k][u] = new SegmentShare((double)l.Count / Math.Max(1, ranges[k].Count), Median(l), l.Count);
        }
        var members = new HashSet<string>(StringComparer.Ordinal);
        foreach (var g in shares.SelectMany(x => x.Keys).Select(u => ug.GetValueOrDefault(u)).Where(g => g != null).Distinct()) if (gm.TryGetValue(g!, out var cs)) foreach (var c in cs) members.Add(c.Upload);
        members.Remove(excludeId ?? "");
        var todo = members.Where(m => !shares.All(x => x.TryGetValue(m, out var sh) && sh.Share >= s.SkipShare)).ToList();   // every member of every candidate group: no cap
        int direct = 0;
        await Parallel.ForEachAsync(todo, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Math.Min(4, threads)), CancellationToken = ct }, async (m, c) =>
        {
            var res = await DirectSharesAsync(store, paths, m, rows, ranges, s.Tau, c);
            if (res == null) return;
            Interlocked.Increment(ref direct);
            lock (shares) for (int k = 0; k < info.Count; k++) if (res[k].Frames > 0) shares[k][m] = res[k]; else shares[k].Remove(m);
        });
        secs["membersDirect"] = direct; secs["membersChecked"] = members.Count;
        return info.Select((si, k) => new PreCheckSegment(si.Position, si.Start, si.End, si.Frames, shares[k])).ToList();
    }

    /// <summary>One member's segment shares computed from its own frames file (all of its vectors, exact cosine); null when it has none.</summary>
    static async Task<SegmentShare[]?> DirectSharesAsync(IStore store, Paths paths, string id, float[][] rows, List<(int From, int Count)> ranges, double tau, CancellationToken ct)
    {
        try
        {
            var z = await FramesReader.OpenAsync(store, paths.FramesFile(id), ct); if (z == null) return null;
            var h = await z.ReadHeaderAsync(ct);
            var blocks = await z.ReadBlocksAsync(Enumerable.Range(0, h.K).ToList(), CenterSearch.MergeGap, CenterSearch.MaxMerged, ct);
            var vs = new List<float[]>(); var ts = new List<float>();
            foreach (var (_, m) in blocks) { var r = AiProbe.RowsFromF16(m.Vectors); for (int i = 0; i < r.Length && i < m.Times.Length; i++) { Sscd.L2Normalize(r[i]); vs.Add(r[i]); ts.Add(m.Times[i]); } }
            var bestC = new float[rows.Length]; var bestT = new double[rows.Length];
            Parallel.For(0, rows.Length, new ParallelOptions { CancellationToken = ct }, i => { float bc = -2; double bt = 0; for (int j = 0; j < vs.Count; j++) { float c = Vec.Dot(rows[i], vs[j]); if (c > bc) { bc = c; bt = ts[j]; } } bestC[i] = bc; bestT[i] = bt; });
            var res = new SegmentShare[ranges.Count];
            for (int k = 0; k < ranges.Count; k++)
            {
                var l = new List<double>(); for (int i = ranges[k].From; i < ranges[k].From + ranges[k].Count; i++) if (bestC[i] >= tau) l.Add(bestT[i]);
                l.Sort(); res[k] = new SegmentShare((double)l.Count / Math.Max(1, ranges[k].Count), l.Count == 0 ? 0 : l.Count % 2 == 1 ? l[l.Count / 2] : (l[l.Count / 2 - 1] + l[l.Count / 2]) / 2, l.Count);
            }
            return res;
        }
        catch (Exception e) when (e is not OperationCanceledException) { Log.Info($"pre-check: member {id} not scored ({e.Message})"); return null; }
    }

    Task<SpeechResult> IPreCheckAnalyzer.SpeechAsync(string samplePath, PreCheckSettings s, int threads, CancellationToken ct) => SpeechAsync(samplePath, threads, s.LanguageSeconds, s.LanguageMinSeconds, ct);

    /// <summary>0.13.15: the segments' frames (each decoded and subsampled to its share of precheck.frames), ONE SSCD batch over all of them, one lookup, per-segment shares and member checks. No language ID (the stage asks for it only when it can change the decision).</summary>
    public async Task<PreCheckAnalysis> AnalyzeSegmentsAsync(IReadOnlyList<SampleSegment> segments, PreCheckSettings s, int threads, Action<string> progress, CancellationToken ct)
    {
        if (await cache.GetAsync(store, paths, ct) == null) return PreCheckAnalysis.Skipped("no search index");
        var secs = new Dictionary<string, double>();
        var work = Path.Combine(Path.GetTempPath(), "indexnode-precheck-" + Guid.NewGuid().ToString("N")[..10]);
        var all = Path.Combine(work, "all"); Directory.CreateDirectory(all);
        try
        {
            int per = s.Frames <= 0 ? 0 : Math.Max(1, s.Frames / Math.Max(1, segments.Count));
            var info = new List<SegInfo>(); int n = 0; BarCrop? box = null; double frameSecs = 0;
            for (int k = 0; k < segments.Count; k++)
            {
                var ss = new Dictionary<string, double>();
                var f = await PreCheckFrames.ExtractSampleAsync(tools, cfg, segments[k].Path, Path.Combine(work, "s" + k), 0, 0, per <= 0 ? 0 : per, threads, progress, ss, ct);
                frameSecs += ss.GetValueOrDefault("frames");
                if (box == null && f.Count > 0) box = f.Box;
                for (int i = 0; i < f.Count; i++) File.Move(Path.Combine(f.Dir, $"{i:D6}.jpg"), Path.Combine(all, $"{n + i:D6}.jpg"), true);
                info.Add(new SegInfo(segments[k].Position, segments[k].Start, segments[k].End, f.Count)); n += f.Count;
            }
            secs["frames"] = frameSecs;
            var a = await AnalyzeFramesAsync(all, n, box, null, s, threads, progress, secs, ExcludeId, ct, info);
            Excluded = a.Excluded;
            return a;
        }
        finally { try { Directory.Delete(work, true); } catch { } }
    }


    /// <summary>The bar box from the frames themselves (a service that only received JPEGs): up to 24 evenly spaced frames, the frames step's rule.</summary>
    static BarCrop FrameBox(string fdir, int count)
    {
        var im = Imaging.DecodeJpeg(File.ReadAllBytes(Path.Combine(fdir, "000000.jpg")));
        return FrameChunks.BarsFromFrames(fdir, count, im.W, im.H);
    }
}

/// <summary>The client half of the pre-check: the node's own frames step on the sample clip (same selection, bar box and featureless filtering as processing stores), evenly subsampled to <c>precheck.frames</c>, extracted as 000000.jpg ...</summary>
public static class PreCheckFrames
{
    public sealed record Extracted(string Dir, int Count, List<double> Times, BarCrop Box);

    public static async Task<Extracted> ExtractAsync(Tools tools, NodeConfig cfg, string samplePath, string work, int threads, int maxFrames, Action<string> progress, Dictionary<string, double> secs, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var paths = new Paths("p/"); var local = new LocalStore(Path.Combine(work, "store")); const string id = "sample";
        progress("frames");
        double duration = await Frames.DurationAsync(tools, samplePath, ct);
        await Frames.ProduceAsync(tools, samplePath, work, FrameSettings.From(cfg) with { Keyframes = false }, Math.Max(1, threads), duration, p => progress("frames " + p),
            (name, bytes, type) => local.PutAsync(paths.Video(id, name), bytes, null, null, false, ct, type), ct);
        var fs = await FrameChunks.DownloadAsync(local, paths, id, "frames", Path.Combine(work, "framechunks"), ct);
        var fdir = Path.Combine(work, "frames");
        if (fs == null || fs.Count == 0) { secs["frames"] = sw.Elapsed.TotalSeconds; return new(fdir, 0, [], BarCrop.None); }
        await fs.ExtractAsync(tools, fdir, ct);
        var box = await FrameChunks.EnsureBoxAsync(local, paths, id, fs, fdir, ct);
        // precheck.frames: keep an even subsample (the rest never goes through SSCD); the files are renumbered 000000.jpg ...
        var times = fs.Times; int count = fs.Count;
        var keep = PreCheck.SubsampleIndices(count, maxFrames);
        if (keep.Length < count)
        {
            for (int i = 0; i < keep.Length; i++) File.Move(Path.Combine(fdir, $"{keep[i]:D6}.jpg"), Path.Combine(fdir, $"k{i:D6}.jpg"), true);
            foreach (var f in Directory.GetFiles(fdir, "??????.jpg")) File.Delete(f);
            for (int i = 0; i < keep.Length; i++) File.Move(Path.Combine(fdir, $"k{i:D6}.jpg"), Path.Combine(fdir, $"{i:D6}.jpg"), true);
            times = keep.Select(i => fs.Times[i]).ToList(); count = keep.Length;
        }
        secs["frames"] = sw.Elapsed.TotalSeconds;
        return new(fdir, count, times, box);
    }

    /// <summary>
    /// 0.13.15: ONLY the sample frames of a clip: <paramref name="count"/> frames at the centres of that many equal slices of the clip (the same even spread the subsample picks), each one seeked to and decoded alone
    /// (no full frames step over the clip: no colour-grid pruning, no chunk encode), scaled to the stored size (360 px short side, bicubic) and written as JPEG with the frames step's BT.601 full-range conversion and quality.
    /// The bar box comes from those frames (<see cref="FrameChunks.BarsFromFrames"/>). Files are 000000.jpg ... in <paramref name="dir"/>; a frame that cannot be decoded is dropped (the rest are renumbered).
    /// </summary>
    public static async Task<Extracted> ExtractSampleAsync(Tools tools, NodeConfig cfg, string clip, string dir, double start, double length, int count, int threads, Action<string> progress, Dictionary<string, double> secs, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew(); Directory.CreateDirectory(dir);
        var ffmpeg = await tools.FfmpegAsync(ct); var probe = await tools.FfprobeAsync(ct);
        var pr = await Proc.RunAsync(probe, ["-v", "error", "-select_streams", "v:0", "-show_entries", "stream=width,height", "-of", "csv=p=0:s=x", clip], ct);
        var wh = pr.Stdout.Trim().Split('x'); var (SW, SH) = Frames.StoredSize(int.Parse(wh[0]), int.Parse(wh[1]));
        double dur = length > 0 ? length : await Frames.DurationAsync(tools, clip, ct);
        int n = count <= 0 ? Math.Max(1, (int)dur) : count; var q = FrameSettings.From(cfg).JpegQ.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var times = Enumerable.Range(0, n).Select(k => Math.Min(Math.Max(0, dur - 0.05), (k + 0.5) * dur / n)).ToList(); var ok = new bool[n];
        progress("frames");
        await Parallel.ForEachAsync(Enumerable.Range(0, n), new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, threads), CancellationToken = ct }, async (k, t) =>
        {
            var o = Path.Combine(dir, $"s{k:D6}.jpg");
            var r = await Proc.RunAsync(ffmpeg, ["-hide_banner", "-loglevel", "error", "-nostdin", "-y", "-threads", "1", "-ss", times[k].ToString("F3", System.Globalization.CultureInfo.InvariantCulture), "-i", clip, "-an", "-sn", "-frames:v", "1",
                "-vf", $"scale={SW}:{SH}:flags=bicubic,scale=out_color_matrix=bt601:out_range=full,format=yuvj444p", "-c:v", "mjpeg", "-q:v", q, "-f", "image2", o], t);
            ok[k] = r.ExitCode == 0 && File.Exists(o) && new FileInfo(o).Length > 0;
        });
        int c = 0; var kept = new List<double>();
        for (int k = 0; k < n; k++) if (ok[k]) { File.Move(Path.Combine(dir, $"s{k:D6}.jpg"), Path.Combine(dir, $"{c:D6}.jpg"), true); kept.Add(start + times[k]); c++; } else try { File.Delete(Path.Combine(dir, $"s{k:D6}.jpg")); } catch { }
        secs["frames"] = sw.Elapsed.TotalSeconds;
        if (c == 0) return new(dir, 0, [], BarCrop.None);
        return new(dir, c, kept, FrameChunks.BarsFromFrames(dir, c, SW, SH));
    }

    /// <summary>The audio clip of the sample for the language ID: <paramref name="seconds"/> from the middle, 16 kHz mono 16-bit PCM WAV (the service decodes no Opus: it reads these samples as they are; the client's ffmpeg does the decoding). Null when the sample has no audio.</summary>
    public static async Task<(byte[] Data, string FileName, string ContentType)?> AudioClipAsync(Tools tools, string samplePath, double seconds, string work, CancellationToken ct)
    {
        var ffmpeg = await tools.FfmpegAsync(ct);
        double dur = await Frames.DurationAsync(tools, samplePath, ct);
        double len = seconds <= 0 ? dur : Math.Min(seconds, dur), start = Math.Max(0, dur / 2 - len / 2);
        string F(double v) => v.ToString("F3", System.Globalization.CultureInfo.InvariantCulture);
        foreach (var (ext, codec, type) in new[] { ("wav", new[] { "-c:a", "pcm_s16le" }, "audio/wav") })
        {
            var outp = Path.Combine(work, "clip." + ext);
            var args = new List<string> { "-hide_banner", "-loglevel", "error", "-nostdin", "-y", "-ss", F(start), "-t", F(len), "-i", samplePath, "-vn", "-ac", "1", "-ar", "16000" };
            args.AddRange(codec); args.Add(outp);
            var r = await Proc.RunAsync(ffmpeg, args, ct);
            if (r.ExitCode == 0 && File.Exists(outp) && new FileInfo(outp).Length > 100) return (await File.ReadAllBytesAsync(outp, ct), "clip." + ext, type);
        }
        return null;
    }
}
