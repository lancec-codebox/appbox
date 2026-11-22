using System.Text;
using System.Text.Json;
using Xunit;

namespace IndexNode.Tests;

/// <summary>0.13.23: cast recluster re-clusters a drama's stored faces (read-only): a two-person group is proposed for a split, a one-person group is not, and the merge threshold is respected.</summary>
public class CastReclusterTests
{
    static readonly Paths P = new("index/");
    const int Dim = 16;

    static float[] Unit(float[] v) { double n = Math.Sqrt(v.Sum(x => (double)x * x)); return v.Select(x => (float)(x / n)).ToArray(); }
    /// <summary>A person: base direction (cos to person 0 = sim) plus a small deterministic noise per face.</summary>
    static float[] Face(double simTo0, Random r, int person)
    {
        var b = new float[Dim]; b[0] = (float)(person == 0 ? 1 : simTo0); if (person != 0) b[1] = (float)Math.Sqrt(1 - simTo0 * simTo0);
        for (int i = 0; i < Dim; i++) b[i] += (float)((r.NextDouble() - 0.5) * 0.12);
        return Unit(b);
    }

    /// <summary>Writes a stored cast (faces.json, faces.f16, clusters.json) for the given embeddings, all labelled one cluster, into the fake store.</summary>
    static async Task<(FakeStore Fs, IJobSource Src)> Store(string id, List<float[]> embs)
    {
        var fs = new FakeStore(); var f16 = new byte[embs.Count * Dim * 2];
        for (int i = 0; i < embs.Count; i++) for (int d = 0; d < Dim; d++) BitConverter.TryWriteBytes(f16.AsSpan((i * Dim + d) * 2, 2), (Half)embs[i][d]);
        var faces = embs.Select((e, i) => new { i, t = 2.0 * i, box = new[] { 10f, 10f, 80f, 100f }, score = 0.9, blur = 120.0, quality = 0.5 + (i % 10) / 100.0, other = 0.0, sub = false, pitch = 0.0, eyes = 0.0, kps = new float[0], cluster = 0, masked = (bool?)null }).ToList();
        await fs.PutAsync(P.Video(id, "cast/faces.json"), JsonSerializer.SerializeToUtf8Bytes(new { v = 1, model = "buffalo_s", dim = Dim, threshold = 0.65, keyframes = embs.Count, source = new { width = 360, height = 640 }, faces }), null, null, false, default);
        await fs.PutAsync(P.Video(id, "cast/faces.f16"), f16, null, null, false, default);
        await fs.PutAsync(P.Video(id, "cast/clusters.json"), Encoding.UTF8.GetBytes("""{"v":1,"model":"buffalo_s","threshold":0.65,"mergeAt":0.45,"merges":[],"clusters":[{"id":0,"faces":1,"main":true}]}"""), null, null, false, default);
        return (fs, new StoreJob(fs, P, id));
    }

    static List<float[]> People(int n0, int n1, double sim) { var r = new Random(7); var l = new List<float[]>(); for (int i = 0; i < n0; i++) l.Add(Face(sim, r, 0)); for (int i = 0; i < n1; i++) l.Add(Face(sim, r, 1)); return l; }

    [Fact]
    public async Task A_two_person_group_is_proposed_for_a_split_and_a_one_person_group_is_not()
    {
        var (fs, src) = await Store("two00000001", People(40, 30, 0.50));
        var before = fs.Names("index/").ToList(); var o = new CastRecluster.Options(Split: true, SplitSim: 0.60, SplitMin: 10);
        var r = await CastRecluster.RunDramaAsync(src, o, default);
        Assert.Single(r.New.Where(g => g.Main));   // average linkage at 0.65 joins two people that are 0.50 alike: the problem
        var s = Assert.Single(r.Splits); Assert.True(s.Split); Assert.Equal(new[] { 30, 40 }, new[] { s.FacesA, s.FacesB }.OrderBy(x => x).ToArray());
        Assert.InRange(s.CentreSim, 0.4, 0.6); Assert.True(s.FitOwn > s.FitOther + 0.3, $"{s.FitOwn} vs {s.FitOther}");
        Assert.Equal(before, fs.Names("index/").ToList());   // nothing was written
        var (_, src1) = await Store("one00000001", People(70, 0, 0.50));
        var r1 = await CastRecluster.RunDramaAsync(src1, o, default); var s1 = Assert.Single(r1.Splits); Assert.False(s1.Split); Assert.True(s1.CentreSim > 0.8, $"{s1.CentreSim}");
        // each half under N faces is not proposed either
        var rN = await CastRecluster.RunDramaAsync(src, o with { SplitMin = 35 }, default); Assert.False(Assert.Single(rN.Splits).Split);
    }

    [Fact]
    public async Task The_merge_threshold_is_respected_and_the_report_lists_merges_and_changed_faces()
    {
        var (_, src) = await Store("mrg00000001", People(40, 30, 0.50));   // centres 0.50 apart; a tight clustering threshold keeps the two people apart
        var apart = await CastRecluster.RunDramaAsync(src, new CastRecluster.Options(Threshold: 0.2, MergeAt: 0.40), default);
        Assert.Equal(2, apart.New.Count(g => g.Main)); Assert.Empty(apart.Merges); Assert.Equal(30, apart.Changed);   // the stored label had everything in one group; 30 faces left it
        var joined = await CastRecluster.RunDramaAsync(src, new CastRecluster.Options(Threshold: 0.2, MergeAt: 0.60), default);
        Assert.Single(joined.New.Where(g => g.Main)); var m = Assert.Single(joined.Merges); Assert.InRange(m.Dist, 0.4, 0.6); Assert.Equal(0, joined.Changed);
        Assert.Contains("merge: into", CastRecluster.Text(joined)); Assert.Equal(70, joined.New.Sum(g => g.Faces)); Assert.Contains("mrg00000001", CastRecluster.SummaryTable([apart, joined], false));
        Assert.Equal(0.4, apart.MergeAt); Assert.Equal(0.60, joined.MergeAt); Assert.Equal(0.45, (await CastRecluster.RunDramaAsync(src, new CastRecluster.Options(), default)).MergeAt);   // default: the model's 0.45
    }
}
