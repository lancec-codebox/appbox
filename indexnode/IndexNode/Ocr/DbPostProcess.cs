namespace IndexNode;

public readonly record struct Pt(double X, double Y);

/// <summary>A detected text box: corners in the order top-left, top-right, bottom-right, bottom-left, in image pixels.</summary>
public sealed record DetBox(Pt[] Corners, double Score)
{
    public double MinX => Corners.Min(p => p.X);
    public double MaxX => Corners.Max(p => p.X);
    public double MinY => Corners.Min(p => p.Y);
    public double MaxY => Corners.Max(p => p.Y);
}

/// <summary>
/// DB (differentiable binarization) post-processing of the PP-OCR text detector, as in PaddleOCR / RapidOCR: threshold the probability
/// map, take each connected region, fit its minimum-area rectangle, score it ("fast" mode: mean probability inside the box), drop
/// weak or tiny ones, and unclip (grow) the rectangle by area*ratio/perimeter.
/// </summary>
public static class DbPostProcess
{
    public const double Thresh = 0.3, BoxThresh = 0.5, UnclipRatio = 1.6;
    public const int MaxCandidates = 1000, MinSize = 3;

    public static List<DetBox> Process(float[] prob, int h, int w, int destW, int destH,
        double thresh = Thresh, double boxThresh = BoxThresh, double unclip = UnclipRatio)
    {
        var boxes = new List<DetBox>();
        var label = new int[h * w];
        var stack = new Stack<int>();
        int comps = 0;
        for (int start = 0; start < h * w && comps < MaxCandidates; start++)
        {
            if (label[start] != 0 || prob[start] <= thresh) continue;
            comps++;
            var pixels = new List<int>();
            label[start] = comps; stack.Push(start);
            while (stack.Count > 0)
            {
                int p = stack.Pop(); pixels.Add(p);
                int y = p / w, x = p % w;
                for (int dy = -1; dy <= 1; dy++)
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        int ny = y + dy, nx = x + dx;
                        if (ny < 0 || nx < 0 || ny >= h || nx >= w) continue;
                        int q = ny * w + nx;
                        if (label[q] == 0 && prob[q] > thresh) { label[q] = comps; stack.Push(q); }
                    }
            }
            if (pixels.Count < 4) continue;
            // boundary pixels are enough for the hull
            var pts = new List<Pt>();
            foreach (var p in pixels)
            {
                int y = p / w, x = p % w;
                bool edge = x == 0 || y == 0 || x == w - 1 || y == h - 1 || prob[p - 1] <= thresh || prob[p + 1] <= thresh || prob[p - w] <= thresh || prob[p + w] <= thresh;
                if (edge) pts.Add(new Pt(x, y));
            }
            var (box, sside) = MiniBox(pts);
            if (sside < MinSize) continue;
            var score = ScoreFast(prob, h, w, box);
            if (score < boxThresh) continue;
            var grown = Unclip(box, unclip);
            var (box2, sside2) = MiniBox(grown);
            if (sside2 < MinSize + 2) continue;
            var scaled = box2.Select(p => new Pt(
                Math.Clamp(Math.Round(p.X / w * destW), 0, destW), Math.Clamp(Math.Round(p.Y / h * destH), 0, destH))).ToArray();
            boxes.Add(new DetBox(scaled, score));
        }
        return boxes;
    }

    /// <summary>Minimum-area rotated rectangle of a point set (convex hull, then every hull edge as an axis); corners ordered tl, tr, br, bl.</summary>
    public static (Pt[] Corners, double ShortSide) MiniBox(IReadOnlyList<Pt> points)
    {
        var hull = Hull(points);
        if (hull.Count == 0) return ([new(0, 0), new(0, 0), new(0, 0), new(0, 0)], 0);
        double best = double.MaxValue; Pt[] bestRect = [];
        var cand = hull.Count == 1 ? [(hull[0], hull[0])] : Enumerable.Range(0, hull.Count).Select(i => (hull[i], hull[(i + 1) % hull.Count])).ToArray();
        foreach (var (a, b) in cand)
        {
            double dx = b.X - a.X, dy = b.Y - a.Y, len = Math.Sqrt(dx * dx + dy * dy);
            if (len < 1e-9) { dx = 1; dy = 0; len = 1; }
            double ux = dx / len, uy = dy / len, vx = -uy, vy = ux;
            double minU = double.MaxValue, maxU = double.MinValue, minV = double.MaxValue, maxV = double.MinValue;
            foreach (var p in hull)
            {
                double pu = p.X * ux + p.Y * uy, pv = p.X * vx + p.Y * vy;
                minU = Math.Min(minU, pu); maxU = Math.Max(maxU, pu); minV = Math.Min(minV, pv); maxV = Math.Max(maxV, pv);
            }
            double area = (maxU - minU) * (maxV - minV);
            if (area < best - 1e-9)
            {
                best = area;
                Pt C(double u, double v) => new(u * ux + v * vx, u * uy + v * vy);
                bestRect = [C(minU, minV), C(maxU, minV), C(maxU, maxV), C(minU, maxV)];
            }
        }
        double s1 = Dist(bestRect[0], bestRect[1]), s2 = Dist(bestRect[1], bestRect[2]);
        return (Order(bestRect), Math.Min(s1, s2));
    }

    /// <summary>The ordering of PaddleOCR's get_mini_boxes: by x, then the two left points top/bottom and the two right points top/bottom.</summary>
    public static Pt[] Order(Pt[] r)
    {
        var p = r.OrderBy(q => q.X).ThenBy(q => q.Y).ToArray();
        int i1, i4, i2, i3;
        if (p[1].Y > p[0].Y) { i1 = 0; i4 = 1; } else { i1 = 1; i4 = 0; }
        if (p[3].Y > p[2].Y) { i2 = 2; i3 = 3; } else { i2 = 3; i3 = 2; }
        return [p[i1], p[i2], p[i3], p[i4]];
    }

    private static double Dist(Pt a, Pt b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

    /// <summary>Mean probability over the pixels inside the (convex) box.</summary>
    public static double ScoreFast(float[] prob, int h, int w, Pt[] box)
    {
        int x0 = Math.Max(0, (int)Math.Floor(box.Min(p => p.X))), x1 = Math.Min(w - 1, (int)Math.Ceiling(box.Max(p => p.X)));
        int y0 = Math.Max(0, (int)Math.Floor(box.Min(p => p.Y))), y1 = Math.Min(h - 1, (int)Math.Ceiling(box.Max(p => p.Y)));
        double sum = 0; int n = 0;
        for (int y = y0; y <= y1; y++)
            for (int x = x0; x <= x1; x++)
                if (Inside(box, x, y)) { sum += prob[y * w + x]; n++; }
        return n == 0 ? 0 : sum / n;
    }

    private static bool Inside(Pt[] poly, double x, double y)
    {
        int pos = 0, neg = 0;
        for (int i = 0; i < poly.Length; i++)
        {
            var a = poly[i]; var b = poly[(i + 1) % poly.Length];
            double cross = (b.X - a.X) * (y - a.Y) - (b.Y - a.Y) * (x - a.X);
            if (cross > 1e-9) pos++; else if (cross < -1e-9) neg++;
        }
        return pos == 0 || neg == 0;
    }

    /// <summary>The rectangle grown by d = area*ratio/perimeter on every side (what Clipper's offset gives for a rectangle).</summary>
    public static Pt[] Unclip(Pt[] box, double ratio)
    {
        double a = Dist(box[0], box[1]), b = Dist(box[0], box[3]);
        double d = a * b * ratio / (2 * (a + b));
        double ux = (box[1].X - box[0].X) / Math.Max(a, 1e-9), uy = (box[1].Y - box[0].Y) / Math.Max(a, 1e-9);
        double vx = (box[3].X - box[0].X) / Math.Max(b, 1e-9), vy = (box[3].Y - box[0].Y) / Math.Max(b, 1e-9);
        Pt P(Pt o, double su, double sv) => new(o.X + su * d * ux + sv * d * vx, o.Y + su * d * uy + sv * d * vy);
        return [P(box[0], -1, -1), P(box[1], 1, -1), P(box[2], 1, 1), P(box[3], -1, 1)];
    }

    private static List<Pt> Hull(IReadOnlyList<Pt> pts)
    {
        var s = pts.Distinct().OrderBy(p => p.X).ThenBy(p => p.Y).ToList();
        if (s.Count <= 2) return s;
        double Cross(Pt o, Pt a, Pt b) => (a.X - o.X) * (b.Y - o.Y) - (a.Y - o.Y) * (b.X - o.X);
        var h = new List<Pt>();
        foreach (var p in s) { while (h.Count >= 2 && Cross(h[^2], h[^1], p) <= 0) h.RemoveAt(h.Count - 1); h.Add(p); }
        int lower = h.Count + 1;
        for (int i = s.Count - 2; i >= 0; i--) { while (h.Count >= lower && Cross(h[^2], h[^1], s[i]) <= 0) h.RemoveAt(h.Count - 1); h.Add(s[i]); }
        h.RemoveAt(h.Count - 1);
        return h;
    }
}
