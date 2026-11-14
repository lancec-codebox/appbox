using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace IndexNode;

/// <summary>One recognised text line: box in the coordinates of the image given to <see cref="OcrEngine.Recognize"/>.</summary>
public sealed record OcrLine(int X0, int Y0, int X1, int Y1, string Text, double Score);

/// <summary>
/// PP-OCRv5 mobile detection and recognition through ONNX Runtime, single-threaded (one engine per worker). Detection runs at the
/// image's native size (limit type max, side 480: only larger images are scaled down); no angle classifier (subtitles are upright).
/// Images are BGR bytes, as the models expect (they were exported from OpenCV-based pipelines).
/// </summary>
public sealed class OcrEngine : IDisposable
{
    public const int DetLimitSide = 480, RecHeight = 48, RecMinWidth = 48, RecMaxWidth = 3200;
    public const double TextScore = 0.5;
    private readonly InferenceSession det, rec;
    private readonly CtcDecoder ctc;
    private readonly string detIn, recIn;

    public OcrEngine(string detPath, string recPath, string dictPath, int threads = 1)
    {
        det = OrtOptions.Session(detPath, threads);
        rec = OrtOptions.Session(recPath, threads);
        ctc = CtcDecoder.FromFile(dictPath);
        detIn = det.InputMetadata.Keys.First(); recIn = rec.InputMetadata.Keys.First();
    }

    public static int DetSide(int v) => Math.Max(32, (int)Math.Round(v / 32.0) * 32);

    /// <summary>Target size of the detector input: unchanged when the longer side is within the limit, else scaled to the limit; both sides rounded to multiples of 32.</summary>
    public static (int W, int H) DetSize(int w, int h, int limit = DetLimitSide)
    {
        double ratio = Math.Max(w, h) > limit ? (double)limit / Math.Max(w, h) : 1.0;
        return (DetSide((int)(w * ratio)), DetSide((int)(h * ratio)));
    }

    /// <summary>Accumulated ticks per phase (profiling, dev ocrprof).</summary>
    public long TPre, TDet, TPost, TCrop, TRecPre, TRec, TCtc;
    public List<OcrLine> Recognize(byte[] bgr, int w, int h)
    {
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        var (dw, dh) = DetSize(w, h);
        var input = DetTensor(bgr, w, h, dw, dh);
        float[] prob;
        long t1 = System.Diagnostics.Stopwatch.GetTimestamp(); TPre += t1 - t0;
        using (var r = det.Run([NamedOnnxValue.CreateFromTensor(detIn, input)]))
            prob = r.First().AsTensor<float>().ToArray();
        long t2 = System.Diagnostics.Stopwatch.GetTimestamp(); TDet += t2 - t1;
        var boxes = DbPostProcess.Process(prob, dh, dw, w, h);
        TPost += System.Diagnostics.Stopwatch.GetTimestamp() - t2;
        // reading order: top to bottom, then left to right (rows within 10 px count as one line)
        boxes = boxes.OrderBy(b => b.Corners[0].Y).ThenBy(b => b.Corners[0].X).ToList();
        for (int i = 0; i < boxes.Count - 1; i++)
            for (int j = i; j >= 0; j--)
                if (Math.Abs(boxes[j + 1].Corners[0].Y - boxes[j].Corners[0].Y) < 10 && boxes[j + 1].Corners[0].X < boxes[j].Corners[0].X)
                    (boxes[j], boxes[j + 1]) = (boxes[j + 1], boxes[j]);
        if (boxes.Count == 0) return [];

        long t3 = System.Diagnostics.Stopwatch.GetTimestamp();
        var crops = boxes.Select(b => Crop(bgr, w, h, b.Corners)).ToList();
        long t4 = System.Diagnostics.Stopwatch.GetTimestamp(); TCrop += t4 - t3;
        long t5 = System.Diagnostics.Stopwatch.GetTimestamp(); TRecPre += 0;
        var order = Enumerable.Range(0, crops.Count).OrderBy(i => crops[i].W / (double)crops[i].H).ToList();
        var results = new (string, double)[crops.Count];
        for (int g = 0; g < order.Count; g += RecBatch)
        {
            var idx = order.Skip(g).Take(RecBatch).ToList();
            int maxW = Math.Min(RecMaxWidth, idx.Max(i => RecWidth(crops[i].W, crops[i].H)));
            maxW = Math.Max(RecMinWidth, maxW);
            int plane = RecHeight * maxW, need = idx.Count * 3 * plane;
            if (recBuf.Length < need) recBuf = new float[need];
            Array.Clear(recBuf, 0, need); // right padding is 0 after normalisation... (x/255-0.5)/0.5 of 0 would be -1: PaddleOCR pads with 0 in the normalised tensor
            for (int k = 0; k < idx.Count; k++)
            {
                var (cb, cw, ch) = crops[idx[k]];
                int rw = Math.Min(RecWidth(cw, ch), maxW);
                ResizeNorm(cb, cw, ch, rw, RecHeight, recBuf, k * 3 * plane, maxW, 0.5f, 0.5f, 0.5f, 0.5f, 0.5f, 0.5f);
            }
            long t5b = System.Diagnostics.Stopwatch.GetTimestamp(); TRecPre += t5b - t5;
            using var rr = rec.Run([NamedOnnxValue.CreateFromTensor(recIn, new DenseTensor<float>(recBuf.AsMemory(0, need), [idx.Count, 3, RecHeight, maxW]))]);
            var o = rr.First().AsTensor<float>();
            var dims = o.Dimensions.ToArray(); var flat = o.ToArray();
            long t6b = System.Diagnostics.Stopwatch.GetTimestamp(); TRec += t6b - t5b;
            int steps = dims[1], classes = dims[2];
            for (int k = 0; k < idx.Count; k++) results[idx[k]] = ctc.Decode(flat.AsSpan(k * steps * classes, steps * classes), steps, classes);
            TCtc += System.Diagnostics.Stopwatch.GetTimestamp() - t6b;
            t5 = System.Diagnostics.Stopwatch.GetTimestamp();
        }
        var lines = new List<OcrLine>();
        for (int i = 0; i < crops.Count; i++)
        {
            var (text, score) = results[i];
            if (text.Length == 0 || score < TextScore) continue;
            var bx = boxes[i];
            lines.Add(new OcrLine((int)bx.MinX, (int)bx.MinY, (int)bx.MaxX, (int)bx.MaxY, text, score));
        }
        return lines;
    }

    private float[] recBuf = [];
    public const int RecBatch = 6;

    /// <summary>Bilinear resize of a BGR image into planar float (x/255 - mean) / std at dst[dstOff..] with row stride dstStride, with the column maths done once per call.</summary>
    private static void ResizeNorm(byte[] img, int w, int h, int tw, int th, float[] dst, int dstOff, int dstStride, float m0, float m1, float m2, float s0, float s1, float s2)
    {
        var x0 = new int[tw]; var x1 = new int[tw]; var fx = new float[tw];
        for (int x = 0; x < tw; x++) { double sx = Math.Clamp((x + 0.5) * w / tw - 0.5, 0, w - 1); x0[x] = (int)sx; x1[x] = Math.Min(x0[x] + 1, w - 1); fx[x] = (float)(sx - x0[x]); }
        float[] inv = [1f / (255f * s0), 1f / (255f * s1), 1f / (255f * s2)], off = [m0 / s0, m1 / s1, m2 / s2];
        int plane = th * dstStride;
        for (int y = 0; y < th; y++)
        {
            double sy = Math.Clamp((y + 0.5) * h / th - 0.5, 0, h - 1); int y0 = (int)sy, y1 = Math.Min(y0 + 1, h - 1); float fy = (float)(sy - y0);
            int r0 = y0 * w * 3, r1 = y1 * w * 3;
            for (int x = 0; x < tw; x++)
            {
                int a = r0 + x0[x] * 3, b = r0 + x1[x] * 3, d = r1 + x0[x] * 3, e = r1 + x1[x] * 3; float f = fx[x];
                for (int c = 0; c < 3; c++)
                {
                    float top = img[a + c] + (img[b + c] - img[a + c]) * f, bot = img[d + c] + (img[e + c] - img[d + c]) * f;
                    dst[dstOff + c * plane + y * dstStride + x] = (top + (bot - top) * fy) * inv[c] - off[c];
                }
            }
        }
    }

    private float[] detBuf = [];
    private DenseTensor<float> DetTensor(byte[] bgr, int w, int h, int dw, int dh)
    {
        int n = 3 * dh * dw; if (detBuf.Length < n) detBuf = new float[n];
        ResizeNorm(bgr, w, h, dw, dh, detBuf, 0, dw, 0.485f, 0.456f, 0.406f, 0.229f, 0.224f, 0.225f);
        return new DenseTensor<float>(detBuf.AsMemory(0, n), [1, 3, dh, dw]);
    }

    public static int RecWidth(int cw, int ch) => (int)Math.Ceiling(RecHeight * Math.Max(cw / (double)ch, 1.0));

    private static float Bilinear(byte[] img, int w, int h, int c, double x, double y)
    {
        x = Math.Clamp(x, 0, w - 1); y = Math.Clamp(y, 0, h - 1);
        int x0 = (int)x, y0 = (int)y, x1 = Math.Min(x0 + 1, w - 1), y1 = Math.Min(y0 + 1, h - 1);
        double fx = x - x0, fy = y - y0;
        double a = img[(y0 * w + x0) * 3 + c], b = img[(y0 * w + x1) * 3 + c], d = img[(y1 * w + x0) * 3 + c], e = img[(y1 * w + x1) * 3 + c];
        return (float)((a * (1 - fx) + b * fx) * (1 - fy) + (d * (1 - fx) + e * fx) * fy);
    }

    /// <summary>Straightens the box (affine, which is exact for rectangles) into an upright crop; tall crops are turned 90 degrees as in PaddleOCR.</summary>
    public static (byte[] Data, int W, int H) Crop(byte[] img, int w, int h, Pt[] q)
    {
        double D(Pt a, Pt b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));
        int cw = Math.Max(1, (int)Math.Round(Math.Max(D(q[0], q[1]), D(q[2], q[3])))), ch = Math.Max(1, (int)Math.Round(Math.Max(D(q[0], q[3]), D(q[1], q[2]))));
        var o = new byte[cw * ch * 3];
        for (int y = 0; y < ch; y++)
            for (int x = 0; x < cw; x++)
            {
                double u = (x + 0.5) / cw, v = (y + 0.5) / ch;
                double sx = q[0].X + u * (q[1].X - q[0].X) + v * (q[3].X - q[0].X) - 0.5, sy = q[0].Y + u * (q[1].Y - q[0].Y) + v * (q[3].Y - q[0].Y) - 0.5;
                for (int c = 0; c < 3; c++) o[(y * cw + x) * 3 + c] = (byte)Math.Round(Bilinear(img, w, h, c, sx, sy));
            }
        if (ch / (double)cw < 1.5) return (o, cw, ch);
        var r = new byte[o.Length]; // rot90 counter-clockwise: new[i, j] = old[j, cw-1-i]
        for (int i = 0; i < cw; i++)
            for (int j = 0; j < ch; j++)
                for (int c = 0; c < 3; c++) r[(i * ch + j) * 3 + c] = o[(j * cw + (cw - 1 - i)) * 3 + c];
        return (r, ch, cw);
    }

    /// <summary>Boxes only (no recognition): used to find the subtitle band.</summary>
    public List<DetBox> Detect(byte[] bgr, int w, int h, int limit = DetLimitSide)
    {
        var (dw, dh) = DetSize(w, h, limit);
        var input = DetTensor(bgr, w, h, dw, dh);
        using var r = det.Run([NamedOnnxValue.CreateFromTensor(detIn, input)]);
        return DbPostProcess.Process(r.First().AsTensor<float>().ToArray(), dh, dw, w, h);
    }

    public void Dispose() { det.Dispose(); rec.Dispose(); }
}
