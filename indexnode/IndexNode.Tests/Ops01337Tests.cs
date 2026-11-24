using System.Text.Json.Nodes;
using Xunit;
using Xunit.Abstractions;
using static IndexNode.Tests.ActorsBuildTests;

namespace IndexNode.Tests;

/// <summary>0.13.37: ops measured through the real RestStore and RestMediaTable over fake HTTP handlers (no network, no live resources).</summary>
[Collection("SeqBlobs")]
public class Ops01337Tests(ITestOutputHelper output)
{
    static readonly Paths P = new("index/");
    static readonly DateTimeOffset Srv = new(2026, 11, 23, 20, 15, 0, TimeSpan.Zero);

    [Fact]
    public async Task A_group_step_with_seq_costs_a_fixed_few_requests_and_a_repeat_costs_one_point_read()
    {
        var fs = new FakeStore { ServerClock = () => Srv }; var ft = new FakeMediaTable(); var m = new StoreMeter { Limits = new StorageLimits { MaxPerStep = 1000 } };
        var store = FakeBlobHttp.Rest(fs, m, out var bh); var table = FakeTableHttp.Rest(ft, m, out var th);
        await ft.MergeAsync("upload", "me", new JsonObject { ["precheck"] = "{}" }, default);   // the precheck wrote the row before the group step
        for (int i = 0; i < 40; i++) await SeqBlobs.AppendAsync(fs, P, "pred" + i, i % 9 == 0, default);   // 40 predecessors already in the list
        long b0 = bh.Requests, t0 = th.Requests;
        using (m.BeginStep(1000, "group seq")) { var (mine, before) = await SeqBlobs.AssignAsync(store, P, table, "me", false, default); Assert.Equal(40, before.Count); Assert.Equal(41, (int)(mine.Seq)); }
        long blob1 = bh.Requests - b0, tab1 = th.Requests - t0;
        // the group step's marks: start, done (partition + upload row)
        long t1 = th.Requests;
        await StageMarks.RunAsync(table, "group", "me", "running", 1, "full", "n", 1, Srv, default);
        await StageMarks.DoneAsync(table, "group", "me", 1, "full", "n", 1, Srv, default);
        long marks = th.Requests - t1;
        long b1 = bh.Requests, t2 = th.Requests;
        await SeqBlobs.AssignAsync(store, P, table, "me", false, default);   // a rerun: the row has its seq: one point read, then the blobs are read for the predecessors (no append)
        long blob2 = bh.Requests - b1, tab2 = th.Requests - t2;
        output.WriteLine($"group step with seq: first run {blob1} blob requests + {tab1} table requests (point read, append, read of the seq blob, merge); marks (start + done) {marks} table requests; rerun {blob2} blob + {tab2} table;");
        Assert.Equal(2, tab1);   // point read + merge
        Assert.True(blob1 <= 4, "blob requests " + blob1);
        Assert.Equal(3, marks);
        Assert.Equal(1, tab2); Assert.True(blob2 <= 2);
        Assert.DoesNotContain(bh.Log, l => l.Contains("video.json"));
    }

    [Fact]
    public async Task An_actors_job_with_marks_adds_only_the_three_table_merges_to_its_blob_ops()
    {
        VideoAggregate.ClearCache(); ActorsStage.ForgetSeen();
        var fs = new FakeStore { ServerClock = () => Srv }; var ft = new FakeMediaTable(); var m = new StoreMeter { Limits = new StorageLimits { Lanes = 2 } };
        var store = FakeBlobHttp.Rest(fs, m, out var bh); var table = FakeTableHttp.Rest(ft, m, out var th);
        const int N = 8; var ids = Enumerable.Range(0, N).Select(i => "d" + i.ToString("D3")).ToList();
        for (int i = 0; i < N; i++)
        {
            await Drama(fs, ids[i], WithCos(1 - (i % 7) * 0.05, i % 5 + 1), ((byte)(10 + i), 50, 90), n: 4, seed: i + 1);
            await VideoJson.UpdateAsync(fs, P, ids[i], r => r["grouping"] = new JsonObject { ["v"] = 1, ["proposal"] = new JsonObject { ["kind"] = "new", ["mixed"] = false }, ["seq"] = i + 1 }, default);
        }
        VideoAggregate.ClearCache();
        var stage = new ActorsStage(store, null, 0.64) { MakeR50 = () => new ColourEmbedder(), Decode = Imaging.DecodeJpeg };
        var io = new ActorsIO(store, P); var perJob = new List<long>(); var withMarks = new List<long>();
        for (int i = 0; i < N; i++)
        {
            using (m.BeginStep(m.Limits.StepLimitFor("cast"), ids[i] + " repair")) await stage.RepairAsync(io, ids[i], false, 2, default, null, new Dictionary<string, string>());
            long before = m.Total; long tb = th.Requests;
            using (m.BeginStep(m.Limits.StepLimitFor("actors"), ids[i]))
            {
                await StageMarks.RunAsync(table, "actors", ids[i], "running", 1, "full", "n", 1, Srv, default);
                await stage.RunAsync(io, ids[i], false, 1, default, ids.Take(i).ToList(), null, up => false, new Dictionary<string, string>(), 0, up => ActorsStage.PredKind.UnitsFile, true, up => true);
                await StageMarks.DoneAsync(table, "actors", ids[i], 1, "full", "n", 1, Srv, default);
            }
            withMarks.Add(m.Total - before); perJob.Add(th.Requests - tb);
        }
        var steady = withMarks.Skip(N / 2).ToList();
        output.WriteLine($"actors job with marks: ops/job (blob + table, metered) max={steady.Max()} avg={steady.Average():F1}; table requests per job {perJob.Max()}; per-site: " + string.Join("; ", m.Table().Take(12).Select(x => $"{x.Site} {x.Op} x{x.Count}")));
        Assert.All(perJob, n => Assert.Equal(3, n));
        Assert.True(steady.Max() <= 18 && steady.Average() <= 13, "steady ops/job with marks " + steady.Max());
        Assert.False(m.Tripped);
    }

    [Fact]
    public async Task An_sscd_step_reads_a_full_size_frames_zip_with_one_whole_GET_not_one_range_read_per_chunk()
    {
        var fs = new FakeStore(); var m = new StoreMeter { Limits = new StorageLimits { MaxPerStep = 1000 } }; var rest = FakeBlobHttp.Rest(fs, m, out var http);
        var ms = new MemoryStream(); var rnd = new Random(3); const int chunks = 43;
        using (var z = new System.IO.Compression.ZipArchive(ms, System.IO.Compression.ZipArchiveMode.Create, true))
            for (int i = 0; i < chunks; i++) { var e = z.CreateEntry($"frames/{i:D4}.mp4", System.IO.Compression.CompressionLevel.NoCompression); var d = new byte[512 * 1024]; rnd.NextBytes(d); using var w = e.Open(); w.Write(d); }
        await fs.PutAsync(P.Video("fz1", "frames.zip"), ms.ToArray(), null, null, false, default);
        var dir = Path.Combine(Path.GetTempPath(), "zs-" + Guid.NewGuid().ToString("N")[..6]);
        try
        {
            var store = StageZipStore.Wrap(rest, dir); int read = 0;
            using (m.BeginStep(1000, "sscd"))
            using (StageZipStore.WholeZips())
                for (int i = 0; i < chunks; i++) { var b = await FrameChunks.ReadAsync(store, P.Video("fz1", $"frames/{i:D4}.mp4"), default, true); Assert.Equal(512 * 1024, b!.Length); read++; }
            var rows = m.Table().Where(r => r.Example != null && r.Example.EndsWith("fz1/frames.zip")).ToList();
            var gets = rows.Where(r => r.Op == "get").Sum(r => r.Count); var ranges = rows.Where(r => r.Op == "range").Sum(r => r.Count);
            output.WriteLine($"sscd on a {ms.Length / 1048576.0:F0} MB frames.zip, {read} chunks read: {gets} whole GET, {ranges} range reads, {http.Requests} requests in all: {m.TopLine(8)}");
            Assert.Equal(1, gets); Assert.Equal(0, ranges);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
}
