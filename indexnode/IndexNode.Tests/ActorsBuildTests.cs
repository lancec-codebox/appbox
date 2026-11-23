using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace IndexNode.Tests;

/// <summary>actors build: units (recursive split), the 0.75 / 0.70 bands, the r50 tiebreak (fake embedder), the AI skip, the same-production rule, and nothing written.</summary>
public class ActorsBuildTests
{
    static readonly Paths P = new("index/");
    const int W = 120, H = 160, Dim = 8;

    internal static float[] Unit(params (int D, double V)[] c) { var v = new float[Dim]; foreach (var (d, x) in c) v[d] = (float)x; FaceMath.L2(v); return v; }
    static float[] Noisy(float[] c, Random r, double amp = 0.03) { var v = (float[])c.Clone(); for (int i = 0; i < v.Length; i++) v[i] += (float)((r.NextDouble() - 0.5) * amp); FaceMath.L2(v); return v; }
    internal static float[] WithCos(double c, int k) => Unit((0, c), (k, Math.Sqrt(1 - c * c)));   // cos c with e0, in its own dimension k

    /// <summary>The "r50": the normalised mean colour of the centre of the aligned crop, so a drama of one solid colour gives that colour as its vector.</summary>
    internal sealed class ColourEmbedder : IFaceEmbedder
    {
        public int Dim => 3; public static int Faces;
        public float[] Embed(Rgb a) { Interlocked.Increment(ref Faces); var v = new float[3]; int n = 0; for (int y = 36; y < 76; y++) for (int x = 36; x < 76; x++) { for (int c = 0; c < 3; c++) v[c] += a.Data[(y * a.W + x) * 3 + c]; n++; } FaceMath.L2(v); return v; }
        public float[][] EmbedBatch(IReadOnlyList<Rgb> f) => f.Select(Embed).ToArray();
        public void Dispose() { }
    }

    static byte[] Solid((byte R, byte G, byte B) c) { var d = new byte[W * H * 3]; for (int i = 0; i < W * H; i++) { d[i * 3] = c.R; d[i * 3 + 1] = c.G; d[i * 3 + 2] = c.B; } return Imaging.EncodeJpeg(new Rgb(d, W, H), 95); }

    /// <summary>A drama of n faces with centre vector <paramref name="centre"/>, on three solid-colour frames (t=1..3); cast files and frames as the cast step writes them.</summary>
    internal static async Task Drama(FakeStore fs, string id, float[] centre, (byte, byte, byte) colour, int n = 16, bool ai = false, int seed = 1)
    {
        var rnd = new Random(seed); var jpgs = Enumerable.Range(0, 3).Select(_ => Solid(colour)).ToList(); var all = jpgs.SelectMany(j => j).ToArray(); long off = 0; var rows = new List<ChunkFrame>();
        for (int s = 0; s < 3; s++) { rows.Add(new ChunkFrame(s, s + 1.0, s + 1.0, [], null, off, jpgs[s].Length)); off += jpgs[s].Length; }
        await fs.PutAsync(P.Video(id, "frames/0000.mjpeg"), all, null, null, false, default);
        await fs.PutAsync(P.Video(id, "frames/0000.json"), FrameChunks.JpegIndexJson("0000", 0, W, H, 3, rows), null, null, false, default);
        await fs.PutAsync(P.Video(id, "frames/manifest.json"), FrameChunks.ManifestJson("frames", W, H, 0, 3, [("0000", 0, 3, all.Length)], container: "mjpeg", codec: "jpeg"), null, null, false, default);
        var kps = new[] { 46f, 64f, 74f, 64f, 60f, 76f, 50f, 90f, 70f, 90f };
        var fj = Enumerable.Range(0, n).Select(i => new { i, t = 1.0 + i % 3 + (i / 3) * 0.001 * 0, box = new[] { 10f, 10f, 80f, 100f }, score = 0.9, blur = 100.0, quality = 0.3 + 0.01 * i, other = 0.0, sub = false, pitch = 0.0, eyes = 0.0, cluster = 0, kps }).ToList();
        await fs.PutAsync(P.Video(id, "cast/faces.json"), JsonSerializer.SerializeToUtf8Bytes(new { v = 1, model = "buffalo_s", dim = Dim, threshold = 0.65, keyframes = 3, source = new { width = W, height = H }, faces = fj }), null, null, false, default);
        var f16 = new byte[n * Dim * 2]; for (int i = 0; i < n; i++) { var v = Noisy(centre, rnd); for (int d = 0; d < Dim; d++) BitConverter.TryWriteBytes(f16.AsSpan((i * Dim + d) * 2, 2), (Half)v[d]); }
        await fs.PutAsync(P.Video(id, "cast/faces.f16"), f16, null, null, false, default);
        await fs.PutAsync(P.Video(id, "cast/clusters.json"), JsonSerializer.SerializeToUtf8Bytes(new { v = 1, clusters = new[] { new { id = 0, faces = n, main = true } } }), null, null, false, default);
        if (ai) await fs.PutAsync(P.Video(id, "video.json"), JsonSerializer.SerializeToUtf8Bytes(new { v = 1, id, ai = new { score = 0.99, flag = true, source = "test" } }), null, null, false, default);
    }

    static FaceRec Face(float[] e, int i, double q = 0.5) => new() { T = i, Emb = e, Quality = q };

    [Fact]
    public void Units_are_split_recursively_while_the_halves_are_under_060_alike_and_have_20_faces()
    {
        var r = new Random(3);
        float[] a = Unit((0, 1)), b = Unit((0, 0.4), (1, Math.Sqrt(1 - 0.16))), c = Unit((0, 0.4), (1, 0.2619), (2, 0.8783));   // pairwise cosine 0.4: one raw cluster (distance 0.6 under 0.65)
        var faces = new List<FaceRec>(); int k = 0; foreach (var (cen, n) in new[] { (a, 20), (b, 20), (c, 20) }) for (int i = 0; i < n; i++) faces.Add(Face(Noisy(cen, r), k++));
        var one = ActorsBuild.MakeUnits(faces, 0.65);
        Assert.Equal(new[] { 20, 20, 20 }, one.Select(u => u.Count).OrderBy(x => x));   // split twice: 40|20, then 20|20
        // a tight blob is not split; halves under 20 faces are not split; a unit under 14 faces is dropped
        var tight = Enumerable.Range(0, 60).Select(i => Face(Noisy(a, r), i)).ToList(); Assert.Single(ActorsBuild.MakeUnits(tight, 0.65));
        var small = new List<FaceRec>(); for (int i = 0; i < 19; i++) small.Add(Face(Noisy(a, r), i)); for (int i = 0; i < 19; i++) small.Add(Face(Noisy(b, r), 100 + i));
        Assert.Single(ActorsBuild.MakeUnits(small, 0.65));
        Assert.Empty(ActorsBuild.MakeUnits(Enumerable.Range(0, 13).Select(i => Face(Noisy(a, r), i)).ToList(), 0.65));
    }

    [Fact]
    public async Task Bands_tiebreak_ai_skip_same_production_and_no_store_writes()
    {
        var fs = new FakeStore();
        await Drama(fs, "dA", WithCos(1, 1), (200, 150, 100), seed: 1);        // the reference: e0
        await Drama(fs, "dB", WithCos(0.80, 1), (10, 10, 10), seed: 2);         // 0.80: direct
        await Drama(fs, "dC", WithCos(0.72, 2), (200, 150, 100), seed: 3);      // 0.72: tiebreak, same colour as A: r50 cosine ~1 -> linked
        await Drama(fs, "dD", WithCos(0.72, 3), (30, 30, 250), seed: 4);        // 0.72: tiebreak, another colour: r50 cosine ~0.5 -> not linked
        await Drama(fs, "dE", WithCos(0.65, 4), (200, 150, 100), seed: 5);      // 0.65: never, whatever the colour
        await Drama(fs, "dF", WithCos(1, 1), (250, 10, 10), seed: 6);               // same production as A (one group): never linked to A
        await Drama(fs, "dG", WithCos(0.99, 5), (200, 150, 100), ai: true, seed: 7);   // AI: skipped completely
        var names = fs.Names("index/").OrderBy(x => x).ToList();
        var groups = new GroupSet { Groups = [new DramaGroup { Id = "g1", Members = [new GroupMember { Upload = "dA" }, new GroupMember { Upload = "dF" }] }] };
        ColourEmbedder.Faces = 0;
        var res = await ActorsBuild.BuildAsync(fs, P, ["dA", "dB", "dC", "dD", "dE", "dF", "dG"], groups, () => new ColourEmbedder(), Imaging.DecodeJpeg, new ActorsBuild.Options(0.64, 2), default);
        Assert.Empty(res.Failed); Assert.True(res.Dramas.Single(d => d.Id == "dG").Ai); Assert.Empty(res.Dramas.Single(d => d.Id == "dG").Units);
        Assert.Equal(6, res.Dramas.Sum(d => d.Units.Count));
        ActorsBuild.Link L(string x, string y) => res.Links.SingleOrDefault(l => (l.A.Drama == x && l.B.Drama == y) || (l.A.Drama == y && l.B.Drama == x))!;
        Assert.Equal(("direct", true), (L("dA", "dB").Method, L("dA", "dB").Linked)); Assert.Null(L("dA", "dB").R50);
        Assert.Equal(("tiebreak", true), (L("dA", "dC").Method, L("dA", "dC").Linked)); Assert.True(L("dA", "dC").R50 > 0.99);
        Assert.Equal(("tiebreak", false), (L("dA", "dD").Method, L("dA", "dD").Linked)); Assert.True(L("dA", "dD").R50 < 0.64);
        Assert.Null(L("dA", "dE"));                      // 0.65: not even a candidate
        Assert.Null(L("dA", "dF"));                      // one production
        Assert.NotNull(L("dF", "dB"));                   // another production: linked as A is
        Assert.All(res.Links, l => Assert.NotEqual("dG", l.A.Drama));
        Assert.True(ColourEmbedder.Faces > 0 && ColourEmbedder.Faces <= 30 * 8, "r50 faces: " + ColourEmbedder.Faces);   // 30 per unit at most, only units in the band
        var actors = res.Actors.Select(a => string.Join("", a.Select(u => u.Drama).OrderBy(x => x))).OrderBy(x => x).ToList();
        Assert.Equal(new[] { "dAdBdCdF", "dD", "dE" }, actors);
        // the cut moves the tiebreak: at 0.4 the colour-far unit links too
        var low = await ActorsBuild.BuildAsync(fs, P, ["dA", "dD"], null, () => new ColourEmbedder(), Imaging.DecodeJpeg, new ActorsBuild.Options(0.4, 1), default);
        Assert.Single(low.Actors);
        Assert.Equal(names, fs.Names("index/").OrderBy(x => x).ToList());   // nothing written to the store
        var tsv = ActorsBuild.LinksTsv(res); Assert.Contains("tiebreak", tsv); Assert.Contains("direct", tsv);
        var js = ActorsBuild.ActorsJson(res); Assert.Equal(3, ((JsonArray)js["actors"]!).Count); Assert.Equal(4, (int)js["actors"]![0]!["dramas"]!);
        var sum = ActorsBuild.Summary(res, ["dA"], false, 0, null, 0.64); Assert.Contains("NO --child-probe", sum); Assert.Contains("1 skipped as AI", sum);
        var html = await ActorsBuild.SheetAsync(res, fs, P, Imaging.DecodeJpeg, 2, default);
        Assert.Contains("1 actors spanning 2+ dramas", html); Assert.Contains("https://www.youtube.com/watch?v=dA&amp;t=", html); Assert.Contains("<img src=\"data:image/jpeg;base64,", html); Assert.Contains("one person", html); Assert.Contains("mixed", html); Assert.Contains("Save", html);
        Assert.DoesNotContain("dG", html); Assert.Equal(names, fs.Names("index/").OrderBy(x => x).ToList());
    }

        [Fact]
    public void The_probe_standardises_with_mu_and_sd_and_reads_its_cut()
    {
        var w = new float[Dim]; w[2] = 2; var mu = new float[Dim]; mu[2] = 0.5f; var sd = Enumerable.Repeat(1f, Dim).ToArray(); sd[2] = 0.25f;
        var p = new ActorsBuild.Probe(w, -1, mu, sd, 0.9); var x = new float[Dim]; x[2] = 0.75f;   // z = 1, logit = 1
        Assert.Equal(1 / (1 + Math.Exp(-1)), p.Score(x), 6);
        var path = Path.Combine(Path.GetTempPath(), "cp-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path, JsonSerializer.Serialize(new { mu, sd, w, b = -1.0, cut = 0.9 }));
        try { var q = ActorsBuild.LoadProbe(path)!; Assert.Equal(0.9, q.Cut); Assert.Equal(p.Score(x), q.Score(x), 9); } finally { File.Delete(path); }
    }

    [Fact]
    public async Task The_child_probe_leaves_out_child_units()
    {
        var fs = new FakeStore(); await Drama(fs, "dA", WithCos(1, 1), (9, 9, 9), seed: 1); await Drama(fs, "dK", WithCos(0.9, 2), (9, 9, 9), seed: 2);
        var probe = new ActorsBuild.Probe(Unit((2, 1)).Select(x => x * 40f).ToArray(), -2);   // fires on the e2 component: dK's centre has it, dA's does not
        var res = await ActorsBuild.BuildAsync(fs, P, ["dA", "dK"], null, () => new ColourEmbedder(), Imaging.DecodeJpeg, new ActorsBuild.Options(0.64, 1, probe), default);
        Assert.Single(res.Dramas.Single(d => d.Id == "dA").Units); Assert.Empty(res.Dramas.Single(d => d.Id == "dK").Units); Assert.Equal(1, res.Dramas.Single(d => d.Id == "dK").ChildUnits);
        Assert.Contains("1 units left out", ActorsBuild.Summary(res, ["dA", "dK"], true, 0, null, 0.64));
    }

    [Fact]
    public async Task The_probe_comes_from_the_flag_else_the_store_else_nothing()
    {
        var fs = new FakeStore(); var P2 = new Paths("index/");
        Assert.Null(await ActorsBuild.ResolveProbeAsync(fs, P2, null, default));   // none anywhere: the node must not claim actors
        var w = new float[Dim]; w[1] = 1;
        await fs.PutAsync(ActorsBuild.StoreProbe(P2), System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { w, b = 0.0, cut = 0.8 })), null, null, false, default);
        Assert.Equal("index/models/child-probe.json", ActorsBuild.StoreProbe(P2));
        Assert.Equal(0.8, (await ActorsBuild.ResolveProbeAsync(fs, P2, null, default))!.Cut);
        var path = Path.Combine(Path.GetTempPath(), "cp-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path, JsonSerializer.Serialize(new { w, b = 0.0, cut = 0.5 }));
        try { Assert.Equal(0.5, (await ActorsBuild.ResolveProbeAsync(fs, P2, path, default))!.Cut); } finally { File.Delete(path); }   // the flag wins
    }

    [Fact]
    public async Task The_op_pacer_holds_a_job_at_80_percent_of_the_breaker()
    {
        long ops = 0; var t = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc); var slept = TimeSpan.Zero;
        var pacer = new OpPacer(() => ops, 10000, 0.8, () => t, (d, _) => { slept += d; t += d; return Task.CompletedTask; });
        Assert.Equal(8000, pacer.Budget);
        await pacer.WaitAsync(default); Assert.Equal(0, pacer.Waits);          // quiet: no wait
        ops = 7999; await pacer.WaitAsync(default); Assert.Equal(0, pacer.Waits);   // under 8,000 in the window
        ops = 8100; await pacer.WaitAsync(default); Assert.True(pacer.Waits > 0);   // over: it waits until the minute rolls past the burst
        Assert.True(slept >= TimeSpan.FromSeconds(59) && slept <= TimeSpan.FromSeconds(62), slept.ToString());
    }
}
