using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace IndexNode;

/// <summary>A detected face: box in image pixels, detector score and 5 landmarks (x0,y0 .. x4,y4: right eye, left eye, nose, mouth corners).</summary>
public sealed record FaceDet(float X, float Y, float W, float H, float Score, float[] Kps);

public interface IFaceDetector : IDisposable { List<FaceDet> Detect(Rgb im); }
public interface IFaceEmbedder : IDisposable
{
    int Dim { get; }
    float[] Embed(Rgb alignedFace112);
    /// <summary>Many aligned faces in one call (one model run per batch where the model allows it); the rows are the same as Embed gives, in order.</summary>
    float[][] EmbedBatch(IReadOnlyList<Rgb> faces) => faces.Select(Embed).ToArray();
}

public static class FaceMath
{
    public static float Iou(FaceDet a, FaceDet b)
    {
        float x0 = Math.Max(a.X, b.X), y0 = Math.Max(a.Y, b.Y), x1 = Math.Min(a.X + a.W, b.X + b.W), y1 = Math.Min(a.Y + a.H, b.Y + b.H);
        float inter = Math.Max(0, x1 - x0) * Math.Max(0, y1 - y0), u = a.W * a.H + b.W * b.H - inter;
        return u <= 0 ? 0 : inter / u;
    }

    public static List<FaceDet> Nms(List<FaceDet> dets, float iou)
    {
        var keep = new List<FaceDet>();
        foreach (var d in dets.OrderByDescending(d => d.Score)) if (keep.All(k => Iou(k, d) <= iou)) keep.Add(d);
        return keep;
    }

    public static void L2(float[] v) { double s = 0; foreach (var x in v) s += x * x; s = Math.Sqrt(s); if (s > 0) for (int i = 0; i < v.Length; i++) v[i] = (float)(v[i] / s); }

    /// <summary>Scale so the long side is at most longSide, then pad (top-left, black) to multiples of 32 instead of a square: a 360x640 frame runs as 384x640, not 640x640. Returns the scale.</summary>
    public static (Rgb Img, float Scale) FitTo32(Rgb im, int longSide)
    {
        float s = Math.Min(1f, longSide / (float)Math.Max(im.W, im.H)); int nw = Math.Max(1, (int)Math.Round(im.W * s)), nh = Math.Max(1, (int)Math.Round(im.H * s));
        int pw = (nw + 31) / 32 * 32, ph = (nh + 31) / 32 * 32;
        var r = s == 1f ? im : Imaging.Resize(im, nw, nh); var o = Rgb.Blank(pw, ph, 0);
        for (int y = 0; y < nh; y++) Buffer.BlockCopy(r.Data, y * nw * 3, o.Data, y * pw * 3, nw * 3);
        return (o, s);
    }

    /// <summary>Letterbox the image into a size x size square (top-left, grey padding); returns the scale from the image to the square.</summary>
    public static (Rgb Img, float Scale) Letterbox(Rgb im, int size)
    {
        float s = size / (float)Math.Max(im.W, im.H); int nw = (int)Math.Round(im.W * s), nh = (int)Math.Round(im.H * s);
        var r = Imaging.Resize(im, nw, nh); var o = Rgb.Blank(size, size, 0);
        for (int y = 0; y < nh; y++) Buffer.BlockCopy(r.Data, y * nw * 3, o.Data, y * size * 3, nw * 3);
        return (o, s);
    }

    /// <summary>Roughly frontal: the nose near the middle between the eyes and the eye line near horizontal. Returns (yaw ratio, roll degrees).</summary>
    public static (double Yaw, double Roll) Pose(float[] k)
    {
        double ex = k[2] - k[0], ey = k[3] - k[1], eye = Math.Sqrt(ex * ex + ey * ey);
        if (eye < 1) return (9, 90);
        double mx = (k[0] + k[2]) / 2;
        return (Math.Abs(k[4] - mx) / eye, Math.Abs(Math.Atan2(ey, ex) * 180 / Math.PI));
    }
}

/// <summary>OpenCV zoo YuNet (Apache-2.0): 640x640 BGR 0..255 in; per stride 8/16/32: cls, obj, bbox, kps.</summary>
public sealed class YuNet : IFaceDetector
{
    private const int Side = 640;
    private readonly InferenceSession s;
    public YuNet(string path, int threads = 1) { s = OrtOptions.Session(path, threads, true); }

    public List<FaceDet> Detect(Rgb im)
    {
        var (lb, scale) = FaceMath.Letterbox(im, Side);
        var t = new DenseTensor<float>([1, 3, Side, Side]);
        for (int y = 0; y < Side; y++) for (int x = 0; x < Side; x++) { int o = (y * Side + x) * 3; t[0, 0, y, x] = lb.Data[o + 2]; t[0, 1, y, x] = lb.Data[o + 1]; t[0, 2, y, x] = lb.Data[o]; }
        using var r = s.Run([NamedOnnxValue.CreateFromTensor(s.InputMetadata.Keys.First(), t)]);
        var res = new List<FaceDet>();
        foreach (int st in new[] { 8, 16, 32 })
        {
            var cls = r.First(x => x.Name == "cls_" + st).AsTensor<float>().ToArray(); var obj = r.First(x => x.Name == "obj_" + st).AsTensor<float>().ToArray();
            var bb = r.First(x => x.Name == "bbox_" + st).AsTensor<float>().ToArray(); var kp = r.First(x => x.Name == "kps_" + st).AsTensor<float>().ToArray();
            int cols = Side / st;
            for (int i = 0; i < cols * cols; i++)
            {
                float sc = MathF.Sqrt(Math.Clamp(cls[i], 0, 1) * Math.Clamp(obj[i], 0, 1));
                if (sc < 0.3f) continue;
                int row = i / cols, col = i % cols;
                float cx = (col + bb[i * 4]) * st, cy = (row + bb[i * 4 + 1]) * st, w = MathF.Exp(bb[i * 4 + 2]) * st, h = MathF.Exp(bb[i * 4 + 3]) * st;
                var k = new float[10];
                for (int n = 0; n < 5; n++) { k[2 * n] = (kp[i * 10 + 2 * n] + col) * st / scale; k[2 * n + 1] = (kp[i * 10 + 2 * n + 1] + row) * st / scale; }
                res.Add(new FaceDet((cx - w / 2) / scale, (cy - h / 2) / scale, w / scale, h / scale, sc, k));
            }
        }
        return FaceMath.Nms(res, 0.3f);
    }

    public void Dispose() => s.Dispose();
}

/// <summary>InsightFace SCRFD (det_10g, buffalo_l; non-commercial weights): 640x640 RGB (x-127.5)/128, 2 anchors per cell.</summary>
public sealed class Scrfd : IFaceDetector
{
    private const int Side = 640;
    /// <summary>false: the old 640x640 letterbox (kept for comparison runs).</summary>
    public static bool NativeSize = Environment.GetEnvironmentVariable("INDEXNODE_FACE_SQUARE") != "1";
    private readonly InferenceSession s;
    public Scrfd(string path, int threads = 1) { s = OrtOptions.Session(path, threads, true); }

    public List<FaceDet> Detect(Rgb im)
    {
        var (lb, scale) = NativeSize ? FaceMath.FitTo32(im, Side) : FaceMath.Letterbox(im, Side);
        int W = lb.W, H = lb.H;
        var t = new DenseTensor<float>([1, 3, H, W]);
        for (int y = 0; y < H; y++) for (int x = 0; x < W; x++) for (int c = 0; c < 3; c++) t[0, c, y, x] = (lb.Data[(y * W + x) * 3 + c] - 127.5f) / 128f;
        using var r = s.Run([NamedOnnxValue.CreateFromTensor(s.InputMetadata.Keys.First(), t)]);
        var outs = r.Select(o => (Dims: o.AsTensor<float>().Dimensions.ToArray(), Data: o.AsTensor<float>().ToArray())).ToList();
        var res = new List<FaceDet>();
        foreach (int st in new[] { 8, 16, 32 })
        {
            int cols = W / st, n = (H / st) * cols * 2;
            var sc = outs.First(o => o.Dims[^1] == 1 && o.Dims[^2] == n).Data; var bb = outs.First(o => o.Dims[^1] == 4 && o.Dims[^2] == n).Data; var kp = outs.First(o => o.Dims[^1] == 10 && o.Dims[^2] == n).Data;
            for (int i = 0; i < n; i++)
            {
                if (sc[i] < 0.4f) continue;
                int cell = i / 2, row = cell / cols, col = cell % cols; float cx = col * st, cy = row * st;
                float x0 = cx - bb[i * 4] * st, y0 = cy - bb[i * 4 + 1] * st, x1 = cx + bb[i * 4 + 2] * st, y1 = cy + bb[i * 4 + 3] * st;
                var k = new float[10];
                for (int q = 0; q < 5; q++) { k[2 * q] = (cx + kp[i * 10 + 2 * q] * st) / scale; k[2 * q + 1] = (cy + kp[i * 10 + 2 * q + 1] * st) / scale; }
                res.Add(new FaceDet(x0 / scale, y0 / scale, (x1 - x0) / scale, (y1 - y0) / scale, sc[i], k));
            }
        }
        return FaceMath.Nms(res, 0.4f);
    }

    public void Dispose() => s.Dispose();
}

/// <summary>OpenCV zoo SFace (Apache-2.0): aligned 112x112 RGB 0..255 in, 128-d out.</summary>
public sealed class SFace : IFaceEmbedder
{
    private readonly InferenceSession s;
    public int Dim => 128;
    public SFace(string path, int threads = 1) { s = OrtOptions.Session(path, threads, true); }

    public float[] Embed(Rgb f)
    {
        var t = new DenseTensor<float>([1, 3, 112, 112]);
        for (int y = 0; y < 112; y++) for (int x = 0; x < 112; x++) for (int c = 0; c < 3; c++) t[0, c, y, x] = f.Data[(y * 112 + x) * 3 + c];
        using var r = s.Run([NamedOnnxValue.CreateFromTensor(s.InputMetadata.Keys.First(), t)]);
        var v = r.First().AsTensor<float>().ToArray(); FaceMath.L2(v); return v;
    }

    public void Dispose() => s.Dispose();
}

/// <summary>InsightFace ArcFace w600k_r50 (buffalo_l; non-commercial weights): aligned 112x112 RGB (x-127.5)/127.5 in, 512-d out.</summary>
public sealed class ArcFace : IFaceEmbedder
{
    private readonly InferenceSession s;
    public int Dim => 512;
    public ArcFace(string path, int threads = 1) { s = OrtOptions.Session(path, threads, true); }
    public const int MaxBatch = 64;

    /// <summary>The model's input has a dynamic batch dimension (the output is declared [1,512] but follows it): one run per up to 64 faces.</summary>
    public float[][] EmbedBatch(IReadOnlyList<Rgb> faces)
    {
        var res = new float[faces.Count][];
        for (int g = 0; g < faces.Count; g += MaxBatch)
        {
            int n = Math.Min(MaxBatch, faces.Count - g); var t = new DenseTensor<float>([n, 3, 112, 112]);
            for (int k = 0; k < n; k++) { var f = faces[g + k]; for (int y = 0; y < 112; y++) for (int x = 0; x < 112; x++) for (int c = 0; c < 3; c++) t[k, c, y, x] = (f.Data[(y * 112 + x) * 3 + c] - 127.5f) / 127.5f; }
            using var r = s.Run([NamedOnnxValue.CreateFromTensor(s.InputMetadata.Keys.First(), t)]);
            var flat = r.First().AsTensor<float>().ToArray();
            if (flat.Length != n * Dim) throw new InvalidOperationException($"face embedder returned {flat.Length} values for {n} faces");
            for (int k = 0; k < n; k++) { var v = flat.AsSpan(k * Dim, Dim).ToArray(); FaceMath.L2(v); res[g + k] = v; }
        }
        return res;
    }

    public float[] Embed(Rgb f)
    {
        var t = new DenseTensor<float>([1, 3, 112, 112]);
        for (int y = 0; y < 112; y++) for (int x = 0; x < 112; x++) for (int c = 0; c < 3; c++) t[0, c, y, x] = (f.Data[(y * 112 + x) * 3 + c] - 127.5f) / 127.5f;
        using var r = s.Run([NamedOnnxValue.CreateFromTensor(s.InputMetadata.Keys.First(), t)]);
        var v = r.First().AsTensor<float>().ToArray(); FaceMath.L2(v); return v;
    }

    public void Dispose() => s.Dispose();
}
