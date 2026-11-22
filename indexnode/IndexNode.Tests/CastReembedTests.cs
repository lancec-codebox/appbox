using System.Text;
using System.Text.Json;
using Xunit;

namespace IndexNode.Tests;

/// <summary>0.13.23: cast reembed aligns each stored face from its stored landmarks on the stored frame it came from, and writes the embeddings in faces.json order.</summary>
public class CastReembedTests
{
    static readonly Paths P = new("index/");
    const int W = 120, H = 160;

    /// <summary>A frame whose pixels depend on t, so a crop of the wrong frame or the wrong landmarks gives another embedding.</summary>
    static Rgb Frame(int seed) { var d = new byte[W * H * 3]; var r = new Random(seed); for (int i = 0; i < d.Length; i++) d[i] = (byte)(((i / 3) % W * 2 + (i / 3) / W * 3 + seed * 17 + r.Next(8)) & 255); return new Rgb(d, W, H); }

    /// <summary>The "embedder": a 8-d fingerprint of the aligned crop (mean of 8 regions), so it is a pure function of the crop.</summary>
    sealed class Fingerprint : IFaceEmbedder
    {
        public int Dim => 8; public static int Calls;
        public float[] Embed(Rgb a) { Interlocked.Increment(ref Calls); var v = new float[8]; for (int i = 0; i < 8; i++) { double s = 0; int n = 0; for (int y = i * 14; y < i * 14 + 14; y++) for (int x = 0; x < 112; x++) { s += a.Data[(y * 112 + x) * 3 + i % 3]; n++; } v[i] = (float)(s / n / 255.0); } FaceMath.L2(v); return v; }
        public float[][] EmbedBatch(IReadOnlyList<Rgb> f) => f.Select(Embed).ToArray();
        public void Dispose() { }
    }

    static float[] Kps(double cx, double cy) => [(float)(cx - 14), (float)(cy - 6), (float)(cx + 14), (float)(cy - 6), (float)cx, (float)(cy + 6), (float)(cx - 10), (float)(cy + 20), (float)(cx + 10), (float)(cy + 20)];

    /// <summary>Three frames in one mjpeg chunk (times 1, 2, 3), faces in file order: t=2, t=1 (two faces), t=3, i.e. not sorted by frame.</summary>
    static async Task<(FakeStore Fs, string Id, List<(double T, float[] K)> Faces, List<Rgb> Decoded)> Seed()
    {
        const string id = "re000000001"; var fs = new FakeStore(); var jpgs = new List<byte[]>();
        for (int s = 1; s <= 3; s++) jpgs.Add(Imaging.EncodeJpeg(Frame(s), 90));
        var all = jpgs.SelectMany(j => j).ToArray(); long off = 0; var rows = new List<ChunkFrame>();
        for (int s = 0; s < 3; s++) { rows.Add(new ChunkFrame(s, s + 1.0, s + 1.0, [], null, off, jpgs[s].Length)); off += jpgs[s].Length; }
        await fs.PutAsync(P.Video(id, "frames/0000.mjpeg"), all, null, null, false, default);
        await fs.PutAsync(P.Video(id, "frames/0000.json"), FrameChunks.JpegIndexJson("0000", 0, W, H, 3, rows), null, null, false, default);
        await fs.PutAsync(P.Video(id, "frames/manifest.json"), FrameChunks.ManifestJson("frames", W, H, 0, 3, [("0000", 0, 3, all.Length)], container: "mjpeg", codec: "jpeg"), null, null, false, default);
        var faces = new List<(double, float[])> { (2.0, Kps(60, 70)), (1.0, Kps(40, 60)), (1.0, Kps(85, 90)), (3.0, Kps(55, 80)) };
        var fj = faces.Select((f, i) => new { i, t = f.Item1, box = new[] { 10f, 10f, 80f, 100f }, score = 0.9, blur = 100.0, quality = 0.5, other = 0.0, sub = false, pitch = 0.0, eyes = 0.0, kps = f.Item2, cluster = 0, masked = (bool?)null });
        await fs.PutAsync(P.Video(id, "cast/faces.json"), JsonSerializer.SerializeToUtf8Bytes(new { v = 1, model = "buffalo_s", dim = 8, threshold = 0.65, keyframes = 3, source = new { width = W, height = H }, faces = fj }), null, null, false, default);
        return (fs, id, faces, jpgs.Select(j => Imaging.DecodeJpeg(j)).ToList());
    }

    [Fact]
    public async Task Faces_are_aligned_from_their_stored_landmarks_and_come_out_in_faces_json_order()
    {
        var (fs, id, faces, decoded) = await Seed(); var names = fs.Names("index/").ToList();
        var r = await CastReembed.RunAsync(fs, P, id, "fake", () => new Fingerprint(), Imaging.DecodeJpeg, 2, default);
        Assert.Equal(4, r.Faces); Assert.Equal(8, r.Dim); Assert.Equal(3, r.Frames); Assert.Equal(4 * 8 * 2, r.F16.Length);
        for (int i = 0; i < faces.Count; i++)
        {
            var expect = new Fingerprint().Embed(Imaging.AlignFace(decoded[(int)faces[i].T - 1], faces[i].K));   // the frame at t, the face's own landmarks, CastFrames' alignment
            for (int d = 0; d < 8; d++) Assert.Equal((float)(Half)expect[d], (float)BitConverter.ToHalf(r.F16, (i * 8 + d) * 2));
        }
        // the order is faces.json's, not the frame order: face 1 (t=1) differs from face 0 (t=2), and two faces of one frame differ by their landmarks
        Assert.NotEqual(r.F16.AsSpan(0, 16).ToArray(), r.F16.AsSpan(16, 16).ToArray()); Assert.NotEqual(r.F16.AsSpan(16, 16).ToArray(), r.F16.AsSpan(32, 16).ToArray());
        Assert.Equal(names, fs.Names("index/").ToList());   // read-only
        Assert.Null(r.Vs); Assert.Equal(4, r.Faces); Assert.True(r.Time.Wall >= 0);
        var js = CastReembed.Json(r); Assert.Equal(4, (int)js["faces"]!); Assert.Equal("fake", (string)js["model"]!);
    }

    [Fact]
    public async Task The_check_against_the_stored_faces_f16_reports_the_largest_difference()
    {
        var (fs, id, _, _) = await Seed();
        var same = await CastReembed.RunAsync(fs, P, id, "fake", () => new Fingerprint(), Imaging.DecodeJpeg, 1, default);
        await fs.PutAsync(P.Video(id, "cast/faces.f16"), same.F16, null, null, false, default);   // a stored file made by the same embedder
        var ok = await CastReembed.RunAsync(fs, P, id, "fake", () => new Fingerprint(), Imaging.DecodeJpeg, 3, default);
        Assert.NotNull(ok.Vs); Assert.Equal(0, ok.Vs!.MaxAbsDiffHalf); Assert.True(ok.Vs.MaxAbsDiff < 1e-3); Assert.Equal(0, ok.Vs.FacesOver); Assert.True(ok.Vs.MinCosine > 0.99999);
        var bad = same.F16.ToArray(); BitConverter.TryWriteBytes(bad.AsSpan(0, 2), (Half)(-0.9f));   // a stored value that differs
        await fs.PutAsync(P.Video(id, "cast/faces.f16"), bad, null, null, false, default);
        var off = await CastReembed.RunAsync(fs, P, id, "fake", () => new Fingerprint(), Imaging.DecodeJpeg, 1, default); Assert.True(off.Vs!.MaxAbsDiffHalf > 0.5); Assert.True(off.Vs.FacesOver >= 1);
    }

    [Fact]
    public async Task A_face_without_landmarks_or_a_frame_that_is_not_stored_is_refused()
    {
        var (fs, id, _, _) = await Seed();
        var doc = System.Text.Json.Nodes.JsonNode.Parse(Encoding.UTF8.GetString(fs.Blobs[P.Video(id, "cast/faces.json")].Data))!; doc["faces"]![0]!["t"] = 99.0;
        await fs.PutAsync(P.Video(id, "cast/faces.json"), Encoding.UTF8.GetBytes(doc.ToJsonString()), null, null, false, default);
        var e = await Assert.ThrowsAsync<InvalidOperationException>(() => CastReembed.RunAsync(fs, P, id, "fake", () => new Fingerprint(), Imaging.DecodeJpeg, 1, default)); Assert.Contains("no stored frame", e.Message);
        doc["faces"]![0]!["t"] = 2.0; doc["faces"]![0]!["kps"] = new System.Text.Json.Nodes.JsonArray();
        await fs.PutAsync(P.Video(id, "cast/faces.json"), Encoding.UTF8.GetBytes(doc.ToJsonString()), null, null, false, default);
        e = await Assert.ThrowsAsync<InvalidOperationException>(() => CastReembed.RunAsync(fs, P, id, "fake", () => new Fingerprint(), Imaging.DecodeJpeg, 1, default)); Assert.Contains("no landmarks", e.Message);
    }

    [Fact]
    public async Task Fp32_output_is_unrounded_and_the_pair_cosine_change_is_reported()
    {
        var (fs, id, _, _) = await Seed();
        var first = await CastReembed.RunAsync(fs, P, id, "fake", () => new Fingerprint(), Imaging.DecodeJpeg, 1, default);
        Assert.Equal(4 * 8 * 4, first.F32.Length); Assert.NotNull(first.Pairs); Assert.Equal(10_000, first.Pairs!.Pairs);
        for (int i = 0; i < 32; i++) Assert.Equal((float)BitConverter.ToHalf(first.F16, i * 2), (float)(Half)BitConverter.ToSingle(first.F32, i * 4));   // f16 is the rounding of f32
        Assert.InRange(first.Pairs.MaxStorage, 0, 1e-3); Assert.InRange(first.Pairs.MedianStorage, 0, first.Pairs.MaxStorage); Assert.True(double.IsNaN(first.Pairs.MaxStored));   // no stored f16 yet
        await fs.PutAsync(P.Video(id, "cast/faces.f16"), first.F16, null, null, false, default);
        var again = await CastReembed.RunAsync(fs, P, id, "fake", () => new Fingerprint(), Imaging.DecodeJpeg, 1, default);
        Assert.Equal(again.Pairs!.MaxStorage, again.Pairs.MaxStored, 9); Assert.Equal(again.Pairs.MedianStorage, again.Pairs.MedianStored, 9);   // the stored file is our own rounding
        Assert.Contains("pairwise cosine change", CastReembed.Text(again)); Assert.NotNull(CastReembed.Json(again)["pairCosineChange"]);
        // the flip count sees a pair that straddles a bar
        var e = new[] { new float[] { 1, 0 }, new float[] { 0.7001f, 0.7140f } }; var s = new byte[2 * 2 * 2]; var other = new[] { new float[] { 1, 0 }, new float[] { 0.69f, 0.7236f } };
        for (int r = 0; r < 2; r++) for (int d = 0; d < 2; d++) BitConverter.TryWriteBytes(s.AsSpan((r * 2 + d) * 2, 2), (Half)other[r][d]);
        var p = CastReembed.PairChange(e, s, 2, 50); Assert.True(p!.FlipsStored > 0);
    }

    [Fact]
    public async Task Dump_crops_writes_the_aligned_crops_the_embedder_sees_spread_over_the_faces()
    {
        var (fs, id, faces, decoded) = await Seed();
        var want = CastReembed.SpreadIndices(4, 2); Assert.Equal(new[] { 0, 2 }, want.OrderBy(x => x));
        var got = new Dictionary<int, Rgb>();
        await CastReembed.RunAsync(fs, P, id, "fake", () => new Fingerprint(), Imaging.DecodeJpeg, 2, default, want, (i, im) => { lock (got) got[i] = im; });
        Assert.Equal(want.OrderBy(x => x), got.Keys.OrderBy(x => x));
        foreach (var i in want) Assert.Equal(Imaging.AlignFace(decoded[(int)faces[i].T - 1], faces[i].K).Data, got[i].Data);
        var npy = CastReembed.Npy(got[0]); Assert.Equal("\x93NUMPY", System.Text.Encoding.Latin1.GetString(npy, 0, 6));
        int hl = npy[8] | npy[9] << 8; Assert.Equal(0, (10 + hl) % 64); Assert.Equal(10 + hl + 112 * 112 * 3, npy.Length);
        Assert.Contains("(112, 112, 3)", System.Text.Encoding.ASCII.GetString(npy, 10, hl));
    }

    [Fact]
    public async Task Frames_that_live_only_in_frames_zip_are_read_through_the_stage_zip_layer_by_range()
    {
        var (loose, id, faces, decoded) = await Seed(); var dir = Path.Combine(Path.GetTempPath(), "reembed-zip-" + Guid.NewGuid().ToString("N"));
        try
        {
            var fs = new FakeStore(); var build = StageZipStore.Wrap(fs, Path.Combine(dir, "build"));
            foreach (var m in new[] { "frames/0000.mjpeg", "frames/0000.json", "frames/manifest.json", "cast/faces.json" }) await build.PutAsync(P.Video(id, m), (await loose.GetAsync(P.Video(id, m), default))!.Data, null, null, false, default);
            Assert.True(await StageZipStore.Stage(build)!.CommitStageAsync(id, "frames", "n1", false, false, default)); Assert.True(await StageZipStore.Stage(build)!.CommitStageAsync(id, "cast", "n1", false, false, default));
            Assert.DoesNotContain(fs.Names("index/"), n => n.EndsWith("0000.mjpeg"));
            var read = StageZipStore.Wrap(fs, Path.Combine(dir, "read-cache")); ((StageZipStore)read).WholeZipLimit = 0;   // force ranged reads inside the zip
            var r = await CastReembed.RunAsync(read, P, id, "fake", () => new Fingerprint(), Imaging.DecodeJpeg, 2, default);
            Assert.Equal(4, r.Faces);
            for (int i = 0; i < faces.Count; i++)
            {
                var expect = new Fingerprint().Embed(Imaging.AlignFace(decoded[(int)faces[i].T - 1], faces[i].K));
                for (int d = 0; d < 8; d++) Assert.Equal((float)(Half)expect[d], (float)BitConverter.ToHalf(r.F16, (i * 8 + d) * 2));
            }
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public async Task Several_models_share_one_decode_and_one_whole_chunk_fetch()
    {
        var (fs, id, faces, decoded) = await Seed(); fs.RangeLatency = TimeSpan.FromMilliseconds(1); int decodes = 0;
        Rgb Dec(byte[] j) { Interlocked.Increment(ref decodes); return Imaging.DecodeJpeg(j); }
        var rs = await CastReembed.RunManyAsync(fs, P, id, [new("a", () => new Fingerprint()), new("b", () => new Fingerprint())], Dec, 2, default);
        Assert.Equal(2, rs.Length); Assert.Equal("a", rs[0].Model); Assert.Equal("b", rs[1].Model); Assert.Equal(rs[0].F16, rs[1].F16);
        Assert.Equal(3, decodes);      // three stored frames, decoded once each, whatever the number of faces or models
        Assert.Equal(0, fs.RangeReads);   // the chunk is fetched whole: no ranged reads at all
        var one = await CastReembed.RunAsync(fs, P, id, "fake", () => new Fingerprint(), Imaging.DecodeJpeg, 1, default); Assert.Equal(one.F16, rs[0].F16);
    }

    [Fact]
    public void The_fast_decoder_gives_the_same_picture_as_libjpeg_on_average()
    {
        var jpg = Imaging.EncodeJpeg(Frame(5), 90); var a = CastReembed.DecodeYuv(jpg); var b = Imaging.DecodeJpeg(jpg);
        Assert.Equal((b.W, b.H), (a.W, a.H)); double sum = 0; for (int i = 0; i < a.Data.Length; i++) sum += Math.Abs(a.Data[i] - b.Data[i]);   // chroma upsampling differs at hard edges: the mean, not the worst pixel
        Assert.True(sum / a.Data.Length < 3, "mean level difference " + sum / a.Data.Length);
    }

    [Fact]
    public void Pick_per_group_spreads_over_time_favours_quality_and_is_deterministic()
    {
        var fs = Enumerable.Range(0, 20).Select(i => (I: i, G: i < 12 ? 1 : 2, T: i * 10.0, Q: i == 3 ? 0.9 : 0.5)).ToList();   // group 1: 12 faces over 0..110 s, group 2: 8 faces
        var pick = CastReembed.PickPerGroup(fs, 3);
        Assert.Equal(pick, CastReembed.PickPerGroup(fs.AsEnumerable().Reverse(), 3));
        var g1 = pick.Where(x => x < 12).ToArray(); var g2 = pick.Where(x => x >= 12).ToArray();
        Assert.Equal(3, g1.Length); Assert.Equal(3, g2.Length); Assert.Contains(3, g1);   // the best-quality face of the first third wins its bin
        Assert.True(g1.Max() >= 8 && g1.Min() < 4);   // spread over the span
        Assert.Equal(new[] { 0, 1 }, CastReembed.PickPerGroup(fs.Take(2), 5));   // a small group is kept whole
    }
}
