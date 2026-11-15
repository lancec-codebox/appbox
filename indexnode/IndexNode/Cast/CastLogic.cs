namespace IndexNode;

/// <summary>One kept face of the cast step.</summary>
public sealed class FaceRec
{
    public double T { get; set; }
    public float X, Y, W, H, Score;
    public double Blur, Yaw, Roll;
    /// <summary>Pitch (looking up or down) as the landmark ratio off frontal (signed; 0 = frontal) and the gradient energy around the eyes (0 when not measured); see FaceMath.Pitch / EyeEnergy.</summary>
    public double Pitch, Eyes;
    /// <summary>Quality in 0..1: size, detector score, sharpness and frontality together.</summary>
    public double Quality { get; set; }
    public int Cluster { get; set; } = -1;
    /// <summary>The largest other detected face in the same frame, as a fraction of this face's area (0: none).</summary>
    public double Other { get; set; }
    public bool Solo => Other <= SoloMaxRatio;
    /// <summary>The burned-in subtitle on screen in this face's frame, [x, y, w, h] in frame pixels; null when there is none.</summary>
    public double[]? Sub { get; set; }
    public const double SoloMaxRatio = 0.25;
    /// <summary>True when the burned-in subtitle of this frame overlaps the face box itself (a band elsewhere on the frame is no reason to drop a frame: the crop puts it outside).</summary>
    public bool SubOnFace => Sub is { Length: 4 } s && s[0] < X + W && s[0] + s[2] > X && s[1] < Y + H && s[1] + s[3] > Y;
    public bool Masked { get; set; }
    public double MaskScore { get; set; }
    public float[] Emb { get; set; } = [];
    public float[] Kps { get; set; } = [];
    /// <summary>SigLIP embedding of the face crop (only for the faces whose crops were scored).</summary>
    public float[]? Vis { get; set; }
}

/// <summary>Merge: centroid distance at or below which two clusters are the same person. Border: up to this distance two clusters also merge when their SigLIP face-crop centroids agree (cosine at least VisAgree).</summary>
public sealed record CastModel(string Id, double Threshold, int Dim, double Merge, double Border);

/// <summary>The cast step's pure logic: filters, quality, clustering, portrait choice, masks, credits.</summary>
public static class CastLogic
{
    public const int MinHeight = 60;
    public const double MinScore = 0.6, MinBlur = 40, MaxYaw = 0.45, MaxRoll = 30;
    /// <summary>Pitch: the nose's place between the eye line and the mouth line of a frontal face (calibrated on stored crops), and the largest |pitch| a face may have.</summary>
    public const double PitchFront = 0.55, MaxPitch = 0.25, EyeRef = 24;

    /// <summary>Per model: the cosine-distance threshold of average linkage (found on input.mp4, see README), then the second-pass merge limits.</summary>
    public static readonly CastModel Default = new("buffalo_l", 0.65, 512, 0.50, 0), YuNetSFace = new("yunet-sface", 0.50, 128, 0.50, 0),
        // buffalo_s (det_500m + w600k_mbf) on the frames stage's 360x640 frames: merge 0.45 (0.50 merged two children), found on 01FsMcrfjWI against r50 on the same faces (14/14 main kept apart)
        BuffaloS = new("buffalo_s", 0.65, 512, 0.45, 0);
    public const double VisAgree = 0.93;

    public sealed record Merge(int Into, int From, double Dist, double? Vis);

    static float[] VisCentroid(IEnumerable<FaceRec> fs) => Centroid(fs.Where(f => f.Vis != null).Select(f => f.Vis!));

    /// <summary>
    /// Second pass over the clusters: repeatedly merges the closest pair of cluster centroids (face embedding) while the distance is at most <paramref name="maxDist"/>;
    /// with <paramref name="border"/> set, a pair up to that distance also merges when both have SigLIP crop centroids that agree (cosine at least <paramref name="visAgree"/>).
    /// Relabels the faces (clusters renumbered by decreasing size) and returns the merges made.
    /// </summary>
    public static List<Merge> MergeClusters(IReadOnlyList<FaceRec> faces, double maxDist, double border = 0, double visAgree = VisAgree)
    {
        var merges = new List<Merge>();
        while (true)
        {
            var groups = faces.Where(f => f.Cluster >= 0).GroupBy(f => f.Cluster).ToDictionary(g => g.Key, g => g.ToList());
            var cent = groups.ToDictionary(g => g.Key, g => Centroid(g.Value.Select(f => f.Emb)));
            var vis = groups.ToDictionary(g => g.Key, g => g.Value.Count(f => f.Vis != null) >= 3 ? VisCentroid(g.Value) : []);
            var keys = groups.Keys.OrderBy(k => k).ToList(); double best = double.MaxValue; int bi = -1, bj = -1; double? bv = null;
            for (int x = 0; x < keys.Count; x++)
                for (int y = x + 1; y < keys.Count; y++)
                {
                    double d = 1 - Cosine(cent[keys[x]], cent[keys[y]]); double? v = vis[keys[x]].Length > 0 && vis[keys[y]].Length > 0 ? Cosine(vis[keys[x]], vis[keys[y]]) : null;
                    bool ok = d <= maxDist || (border > 0 && d <= border && v >= visAgree);
                    if (ok && d < best) { best = d; bi = keys[x]; bj = keys[y]; bv = v; }
                }
            if (bi < 0) break;
            foreach (var f in groups[bj]) f.Cluster = bi;
            merges.Add(new Merge(bi, bj, Math.Round(best, 3), bv == null ? null : Math.Round(bv.Value, 3)));
        }
        var order = faces.Where(f => f.Cluster >= 0).GroupBy(f => f.Cluster).OrderByDescending(g => g.Count()).Select(g => g.Key).ToList();
        var map = order.Select((c, i) => (c, i)).ToDictionary(p => p.c, p => p.i);
        foreach (var f in faces) if (f.Cluster >= 0) f.Cluster = map[f.Cluster];
        return merges;
    }

    /// <summary>The visual-agreement table of the borderline main-cluster pairs, for the log: (a, b, embedding distance, SigLIP cosine).</summary>
    public static List<(int A, int B, double Dist, double Vis)> BorderPairs(IReadOnlyList<FaceRec> faces, double maxDist, double border)
    {
        var groups = faces.Where(f => f.Cluster >= 0).GroupBy(f => f.Cluster).Where(g => g.Count(f => f.Vis != null) >= 3).ToDictionary(g => g.Key, g => g.ToList());
        var res = new List<(int, int, double, double)>(); var keys = groups.Keys.OrderBy(k => k).ToList();
        for (int x = 0; x < keys.Count; x++) for (int y = x + 1; y < keys.Count; y++)
        {
            double d = 1 - Cosine(Centroid(groups[keys[x]].Select(f => f.Emb)), Centroid(groups[keys[y]].Select(f => f.Emb)));
            if (d > maxDist && d <= border + 0.1) res.Add((keys[x], keys[y], Math.Round(d, 3), Math.Round(Cosine(VisCentroid(groups[keys[x]]), VisCentroid(groups[keys[y]])), 3)));
        }
        return res.OrderBy(r => r.Item3).ToList();
    }

    public static CastModel ModelFor(string? name) => (name ?? "").Trim().ToLowerInvariant() switch
    {
        "" or "default" or "buffalo_l" or "buffalo-l" => Default,
        "yunet-sface" or "yunet+sface" or "sface" => YuNetSFace,
        "buffalo_s" or "buffalo-s" or "w600k_mbf" => BuffaloS,
        _ => throw new ArgumentException("cast.model: buffalo_l (default), buffalo_s or yunet-sface"),
    };

    /// <summary>The filter before clustering: tall enough, confident, sharp and roughly frontal.</summary>
    public static bool Passes(FaceRec f) =>
        f.H >= MinHeight && f.Score >= MinScore && f.Blur >= MinBlur && f.Yaw <= MaxYaw && f.Roll <= MaxRoll && Math.Abs(f.Pitch) <= MaxPitch;

    public static double QualityOf(FaceRec f, int frameHeight)
    {
        double size = Math.Min(1, f.H / (0.25 * Math.Max(1, frameHeight)));
        double sharp = Math.Min(1, f.Blur / 300), front = Math.Max(0, 1 - f.Yaw / MaxYaw);
        // pitch: 1 for a level face, 0 at MaxPitch (looking up or down); eyes: gradient energy around the eye landmarks against EyeRef (not measured: neutral)
        double level = Math.Max(0, 1 - Math.Abs(f.Pitch) / MaxPitch), eyes = f.Eyes > 0 ? Math.Min(1, f.Eyes / EyeRef) : 0.5;
        return Math.Round(0.28 * size + 0.2 * Math.Clamp((f.Score - 0.5) / 0.5, 0, 1) + 0.17 * sharp + 0.1 * front + 0.15 * level * level + 0.1 * eyes, 4);
    }

    public static double Cosine(float[] a, float[] b) { double s = 0; for (int i = 0; i < a.Length; i++) s += a[i] * b[i]; return s; }

    /// <summary>
    /// Agglomerative clustering, average linkage on cosine distance (1 - cosine): merge the closest pair of clusters while their mean pairwise
    /// distance is at most <paramref name="threshold"/>. Returns a cluster id per face, numbered by decreasing size. Exact, O(n^2) memory: callers cap n.
    /// </summary>
    public static int[] Cluster(IReadOnlyList<float[]> embs, double threshold)
    {
        int n = embs.Count; if (n == 0) return [];
        var d = new double[n, n];
        for (int i = 0; i < n; i++) for (int j = i + 1; j < n; j++) d[i, j] = d[j, i] = 1 - Cosine(embs[i], embs[j]);
        var members = Enumerable.Range(0, n).Select(i => new List<int> { i }).ToList(); var alive = Enumerable.Repeat(true, n).ToArray();
        var sum = new double[n, n]; for (int i = 0; i < n; i++) for (int j = 0; j < n; j++) sum[i, j] = d[i, j]; // sum of pairwise distances between clusters
        while (true)
        {
            double best = double.MaxValue; int bi = -1, bj = -1;
            for (int i = 0; i < n; i++)
            {
                if (!alive[i]) continue;
                for (int j = i + 1; j < n; j++)
                {
                    if (!alive[j]) continue;
                    double avg = sum[i, j] / ((double)members[i].Count * members[j].Count);
                    if (avg < best) { best = avg; bi = i; bj = j; }
                }
            }
            if (bi < 0 || best > threshold) break;
            for (int k = 0; k < n; k++) if (alive[k] && k != bi && k != bj) { sum[bi, k] += sum[bj, k]; sum[k, bi] = sum[bi, k]; }
            members[bi].AddRange(members[bj]); members[bj].Clear(); alive[bj] = false;
        }
        var order = Enumerable.Range(0, n).Where(i => alive[i]).OrderByDescending(i => members[i].Count).ThenBy(i => members[i].Min()).ToList();
        var ids = new int[n];
        for (int c = 0; c < order.Count; c++) foreach (var m in members[order[c]]) ids[m] = c;
        return ids;
    }

    public static float[] Centroid(IEnumerable<float[]> embs)
    {
        float[]? c = null; int n = 0;
        foreach (var e in embs) { c ??= new float[e.Length]; for (int i = 0; i < e.Length; i++) c[i] += e[i]; n++; }
        if (c == null) return [];
        FaceMath.L2(c); return c;
    }

    /// <summary>Screen seconds of a cluster: keyframes are not evenly spaced, so each face counts the time to the next keyframe (at most 10 s).</summary>
    public static double ScreenSeconds(IEnumerable<FaceRec> faces, IReadOnlyList<double> keyframeTimes)
    {
        double total = 0;
        foreach (var t in faces.Select(f => f.T).Distinct())
        {
            int i = ((List<double>)keyframeTimes).BinarySearch(t); if (i < 0) i = ~i;
            total += i + 1 < keyframeTimes.Count ? Math.Min(10, keyframeTimes[i + 1] - t) : 2;
        }
        return Math.Round(total, 1);
    }

    /// <summary>Main clusters: at least <paramref name="minFaces"/> faces; at most <paramref name="max"/> of them, the largest first.</summary>
    public static List<int> MainClusters(IReadOnlyList<FaceRec> faces, int minFaces = 8, int max = 20) =>
        faces.Where(f => f.Cluster >= 0).GroupBy(f => f.Cluster).Where(g => g.Count() >= minFaces).OrderByDescending(g => g.Count()).Take(max).Select(g => g.Key).ToList();

    /// <summary>The portrait face of a cluster: the best quality among unmasked faces; only when every face is masked, the best masked one.</summary>
    public static FaceRec? PortraitFace(IEnumerable<FaceRec> cluster)
    {
        var all = cluster.ToList();
        return all.Where(f => !f.Masked).OrderByDescending(f => f.Quality).FirstOrDefault() ?? all.OrderByDescending(f => f.Quality).FirstOrDefault();
    }

    /// <summary>12 diverse faces for the AI contact sheet: highest quality first, then each next pick maximises its distance to the picks so far (greedy farthest point, in embedding space and time).</summary>
    public static List<FaceRec> Diverse(IReadOnlyList<FaceRec> cluster, int count = 12)
    {
        // candidates come from frames where the person is alone (no other face above 25% of theirs); other frames only fill what is missing.
        // A burned-in subtitle elsewhere in the frame is no reason to leave a frame out (the crop keeps the band outside); only one that overlaps the face counts against it, last.
        var all = cluster.Where(f => f.Quality > 0).OrderByDescending(f => f.Quality).ToList();
        var picked = new List<FaceRec>();
        foreach (var tier in new[] { all.Where(f => f.Solo && !f.SubOnFace), all.Where(f => !f.SubOnFace), all })
            if (picked.Count < count) picked = Greedy(tier.ToList(), picked, count);
        return picked.OrderBy(f => f.T).ToList();
    }

    static List<FaceRec> Greedy(List<FaceRec> pool, List<FaceRec> start, int count)
    {
        var picked = new List<FaceRec>(start);
        if (pool.Count <= count - picked.Count) { picked.AddRange(pool.Where(f => !picked.Contains(f))); return picked; }   // a later tier repeats the earlier ones: never twice
        if (picked.Count == 0) picked.Add(pool[0]);
        while (picked.Count < count)
        {
            FaceRec? best = null; double bs = -1;
            foreach (var f in pool)
            {
                if (picked.Contains(f)) continue;
                double dist = picked.Min(p => (1 - Cosine(p.Emb, f.Emb)) + Math.Min(1, Math.Abs(p.T - f.T) / 600.0));
                double score = dist * (0.5 + f.Quality);
                if (score > bs) { bs = score; best = f; }
            }
            if (best == null) break;   // the pool has nothing left that is not picked already (a later tier repeats the earlier ones): fewer than count candidates
            picked.Add(best);
        }
        return picked;
    }

    /// <summary>Zero-shot mask decision from the two prompt similarities: masked when the "mask" prompt scores higher (SigLIP logit scale applied by the caller).</summary>
    public static bool IsMasked(double maskLogit, double bareLogit) => maskLogit > bareLogit;

    /// <summary>The softmax probability of the mask prompt of the two.</summary>
    public static double MaskProbability(double maskLogit, double bareLogit) { double e = Math.Exp(Math.Min(maskLogit, bareLogit) - Math.Max(maskLogit, bareLogit)); return maskLogit > bareLogit ? 1 / (1 + e) : e / (1 + e); }

    public static readonly string[] CreditWords = ["主演", "饰", "领衔"];

    /// <summary>OCR cues of the first and last <paramref name="edgeSeconds"/> of the video that name actors: lines with 主演, 饰 or 领衔.</summary>
    public static List<(double Start, double End, string Text)> CreditLines(IEnumerable<(double Start, double End, string Text)> cues, double duration, double edgeSeconds = 180) =>
        cues.Where(c => (c.Start < edgeSeconds || c.End > duration - edgeSeconds) && CreditWords.Any(w => c.Text.Contains(w))).OrderBy(c => c.Start).ToList();
}
