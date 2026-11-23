using System.Text.Json.Nodes;
using Xunit;
using static IndexNode.Tests.ActorsBuildTests;

namespace IndexNode.Tests;

/// <summary>The actors stage on fixtures: the end to end run (links, tiebreak once, merge keeps the older id, the index and its carry-forward), the join's retained faces, the AI rule (no units, no reads of an AI predecessor) and the dry run.</summary>
public class ActorsStageTests
{
    static readonly Paths P = new("index/");
    static ActorsStage Stage(bool dry = false) => new(new FakeStore(), null, 0.64, dry) { MakeR50 = () => new ColourEmbedder(), Decode = Imaging.DecodeJpeg };
    static ActorsStage StageOn(FakeStore fs) => new(fs, null, 0.64) { MakeR50 = () => new ColourEmbedder(), Decode = Imaging.DecodeJpeg };

    static async Task Group(FakeStore fs, string id, string kind = "new", string? join = null, string? retained = null, bool? ai = null, long? seq = null)
    {
        await VideoJson.UpdateAsync(fs, P, id, r =>
        {
            r["grouping"] = new JsonObject { ["v"] = 1, ["proposal"] = new JsonObject { ["kind"] = kind, ["joinUpload"] = join, ["mixed"] = false }, ["retainedFile"] = retained, ["seq"] = seq };
            if (ai != null) r["ai"] = new JsonObject { ["score"] = 0.9, ["flag"] = ai, ["source"] = "test" };
        }, default);
    }
    static async Task SetAi(FakeStore fs, string id, bool flag) => await VideoJson.UpdateAsync(fs, P, id, r => r["ai"] = new JsonObject { ["score"] = 0.9, ["flag"] = flag, ["source"] = "test" }, default);
    static float[] Sum(float[] a, float[] b) { var v = new float[a.Length]; for (int i = 0; i < v.Length; i++) v[i] = a[i] + b[i]; FaceMath.L2(v); return v; }
    static async Task<UnitDoc> Units(FakeStore fs, string id) => UnitDoc.FromBytes((await fs.GetAsync(ActorsData.UnitFile(P, id), default))!.Data, id);
    static async Task<ActorState> State(FakeStore fs) => ActorState.FromBytes((await fs.GetAsync(ActorsData.StateFile(P), default))!.Data);

    [Fact]
    public async Task End_to_end_links_tiebreak_once_merge_keeps_the_older_id_and_the_index_carries_forward()
    {
        var fs = new FakeStore(); var st = StageOn(fs); var io = new ActorsIO(fs, P);
        float[] a = WithCos(1, 1), f = WithCos(0.5, 6);
        await Drama(fs, "dA", a, (200, 150, 100), seed: 1); await Drama(fs, "dB", WithCos(0.80, 1), (10, 10, 10), seed: 2); await Drama(fs, "dF", f, (30, 30, 250), seed: 3);
        await Drama(fs, "dC", WithCos(0.72, 2), (200, 150, 100), seed: 4); await Drama(fs, "dE", Sum(a, f), (90, 90, 90), seed: 5);
        foreach (var id in new[] { "dA", "dB", "dF", "dC", "dE" }) await Group(fs, id);
        var rA = await st.RunAsync(io, "dA", false, 2, default, []); var rB = await st.RunAsync(io, "dB", false, 2, default, ["dA"]); var rF = await st.RunAsync(io, "dF", false, 2, default, ["dA", "dB"]);
        Assert.Equal(1, rA.Units); Assert.Equal(1, rB.Links); Assert.Equal(0, rF.Links); Assert.Equal(1, rB.Predecessors);   // dA's units come from its cast files (its own units file is not read)
        ColourEmbedder.Faces = 0; var rC = await st.RunAsync(io, "dC", false, 2, default, ["dA", "dB", "dF"]);   // 0.72 to A (and 0.57 to B): the tiebreak, same colour as A
        Assert.Equal(1, rC.Band); Assert.Equal(1, rC.BandLinked); Assert.Equal(1, rC.Links); Assert.InRange(ColourEmbedder.Faces, 1, 60); int after = ColourEmbedder.Faces;
        Assert.NotNull(await fs.GetAsync(ActorsData.R50File(P, "dA"), default)); Assert.NotNull(await fs.GetAsync(ActorsData.R50File(P, "dC"), default));
        var rC2 = await st.RunAsync(io, "dC", false, 2, default, ["dA", "dB", "dF"]); Assert.Equal(0, rC2.R50Units); Assert.Equal(after, ColourEmbedder.Faces);   // a unit's r50 vectors are stored: never computed twice
        Assert.Null(await fs.GetAsync(ActorsData.StateFile(P), default));   // a stage run never touches state.json: no actor ids yet
        var rep1 = await ActorsIndex.BuildAsync(io, default); Assert.False(rep1.UpToDate); Assert.Equal(1, rep1.Version); Assert.Equal(4, rep1.Uploads); Assert.Equal(4, rep1.Units); Assert.True(rep1.Vectors >= 4 * 4);
        var (i1, _) = await ActorsIndex.LoadAsync(io, default); string Act(ActorsIndex x, string k) => x.Uploads.SelectMany(u => u.Units).First(u => u.Key == k).Actor;
        var xa = Act(i1!, "dA#0"); var xf = Act(i1!, "dF#0"); Assert.Equal(xa, Act(i1!, "dB#0")); Assert.Equal(xa, Act(i1!, "dC#0")); Assert.NotEqual(xa, xf);
        var again = await ActorsIndex.BuildAsync(io, default); Assert.True(again.UpToDate); Assert.Contains("up to date", again.Note);
        var rE = await st.RunAsync(io, "dE", false, 2, default, []);   // links A's and F's actors through the index: hamming routing, then the exact re-check
        Assert.True(rE.IndexRouted >= 2); Assert.Equal(0, rE.Predecessors); Assert.True(rE.Links >= 2);
        fs.Gets.Clear(); var rep2 = await ActorsIndex.BuildAsync(io, default); Assert.Equal((2, 4, 1), (rep2.Version, rep2.Reused, rep2.Read));   // four uploads carried forward by ETag, one file read
        Assert.DoesNotContain(ActorsData.UnitFile(P, "dA"), fs.Gets);
        var (idx, _) = await ActorsIndex.LoadAsync(io, default); Assert.Equal(2, idx!.Version); Assert.Equal(rep2.Vectors, idx.Count);
        Assert.Equal(new[] { "dA", "dB", "dC", "dE", "dF" }, idx.Uploads.Select(u => u.Id).ToArray());
        Assert.All(new[] { "dA#0", "dB#0", "dC#0", "dE#0", "dF#0" }, k => Assert.Equal(xa, Act(idx, k)));   // E links both actors: the older id wins
        var state = await State(fs); Assert.Equal(xa, state.Resolve(xf)); Assert.Equal(xa, state.Redirects[xf].To); Assert.Equal("dE#0", state.Redirects[xf].By); Assert.True(string.CompareOrdinal(xa, xf) < 0);
        Assert.True((await ActorsIndex.BuildAsync(io, default)).UpToDate);
    }

    static async Task<Dictionary<string, string>> ActorsOf(ActorsIO io) => (await ActorsIndex.LoadAsync(io, default)).Idx!.Uploads.SelectMany(u => u.Units).ToDictionary(u => u.Key, u => u.Actor);

    [Fact]
    public async Task A_predecessor_whose_stage_never_ran_still_links_from_its_cast_file_and_the_order_of_the_runs_does_not_matter()
    {
        async Task<(FakeStore, ActorsIO, ActorsStage)> Fresh() { var fs = new FakeStore(); var io = new ActorsIO(fs, P); await Drama(fs, "dA", WithCos(1, 1), (200, 150, 100), seed: 1); await Drama(fs, "dB", WithCos(0.9, 1), (10, 10, 10), seed: 2); await Group(fs, "dA"); await Group(fs, "dB"); return (fs, io, StageOn(fs)); }
        var (f1, io1, s1) = await Fresh(); var rB = await s1.RunAsync(io1, "dB", false, 2, default, ["dA"]);   // dA's stage has not run at all: no units file
        Assert.Equal(1, rB.Links); Assert.Empty(((JsonArray)(await Units(f1, "dA")).Links));   // dA computed from its cast file and kept as its units file (create-only, no links yet); its own run replaces it
        Assert.Contains("computed 1", rB.Timing); Assert.Equal(1, rB.Predecessors);
        Assert.Equal("dA#0", ((string)(await Units(f1, "dB")).Links[0]!["other"]!));   // the key is the one dA's own run makes
        await s1.RunAsync(io1, "dA", false, 2, default, []); await ActorsIndex.BuildAsync(io1, default); var order1 = await ActorsOf(io1);
        var (f2, io2, s2) = await Fresh(); await s2.RunAsync(io2, "dA", false, 2, default, []); await s2.RunAsync(io2, "dB", false, 2, default, ["dA"]); await ActorsIndex.BuildAsync(io2, default); var order2 = await ActorsOf(io2);
        Assert.Equal(order1, order2); Assert.Equal(order1["dA#0"], order1["dB#0"]);   // either order: one actor, the same ids
        Assert.Equal((await Units(f1, "dA")).Units[0].Vec[0], (await Units(f2, "dA")).Units[0].Vec[0]);
    }

    [Fact]
    public async Task Predecessors_are_every_upload_with_a_lower_grouping_seq_whatever_the_queue_says_and_their_units_are_reused()
    {
        var fs = new FakeStore(); var io = new ActorsIO(fs, P);
        foreach (var (id, c, rgb, sd) in new[] { ("dA", 1.0, ((byte)200, (byte)150, (byte)100), 1), ("dB", 0.9, ((byte)10, (byte)10, (byte)10), 2), ("dC", 0.8, ((byte)50, (byte)60, (byte)70), 3) }) { await Drama(fs, id, WithCos(c, 1), rgb, seed: sd); }
        await Group(fs, "dA", seq: 1); await Group(fs, "dB", seq: 2); await Group(fs, "dC", seq: 3);   // no queue at all
        var seqA = ActorsStage.SeqOf((await VideoJson.ReadAsync(fs, P, "dA", default))!.Root["grouping"] as JsonObject); var seqC = ActorsStage.SeqOf((await VideoJson.ReadAsync(fs, P, "dC", default))!.Root["grouping"] as JsonObject);
        var preds = await ActorsStage.PredecessorsAsync(fs, P, "dC", seqC, null, default);
        Assert.Equal(new[] { "dA", "dB" }, preds.ToArray()); Assert.True(seqA < seqC);
        var st = StageOn(fs); var r1 = await st.RunAsync(io, "dC", false, 2, default);   // preds from the aggregate, not passed
        Assert.Equal(2, r1.Predecessors); Assert.Contains("computed 2", r1.Timing); Assert.Contains("written 2", r1.Timing);
        var r2 = await st.RunAsync(io, "dC", false, 2, default);
        Assert.Contains("computed 0", r2.Timing); Assert.Contains("reused from units files 2", r2.Timing);   // the units files written by the first run
    }

    [Fact]
    public async Task Out_local_runs_in_one_process_are_read_by_the_index_build_and_the_next_run_in_another()
    {
        var fs = new FakeStore(); var dir = Path.Combine(Path.GetTempPath(), "actors-ol-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            await Drama(fs, "dA", WithCos(1, 1), ((byte)200, (byte)150, (byte)100), seed: 1); await Drama(fs, "dB", WithCos(0.9, 1), ((byte)10, (byte)10, (byte)10), seed: 2);
            await Group(fs, "dA", seq: 1); await Group(fs, "dB", seq: 2);
            var st = StageOn(fs);
            await st.RunAsync(new ActorsIO(fs, P, false, dir), "dA", false, 2, default); await st.RunAsync(new ActorsIO(fs, P, false, dir), "dB", false, 2, default);   // two separate overlays, as two processes
            Assert.True(File.Exists(Path.Combine(dir, ActorsData.UnitFile(P, "dA").Replace('/', Path.DirectorySeparatorChar))));
            Assert.Null(await fs.GetAsync(ActorsData.UnitFile(P, "dA"), default));   // the store itself was only read
            var rep = await ActorsIndex.BuildAsync(new ActorsIO(fs, P, false, dir), default);   // a third process: the index reads the units under --out-local
            Assert.Equal(2, rep.Uploads); Assert.True(rep.Units >= 2, rep.Note);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public async Task A_join_uses_only_the_faces_on_its_retained_frames()
    {
        var fs = new FakeStore(); var st = StageOn(fs); var io = new ActorsIO(fs, P);
        await Drama(fs, "dJ", WithCos(1, 1), (200, 150, 100), n: 48, seed: 1); await Group(fs, "dJ");   // 48 faces, 16 on each of t=1,2,3
        var f16 = new byte[CentersFile.Dim * 2]; for (int i = 0; i < CentersFile.Dim; i++) BitConverter.TryWriteBytes(f16.AsSpan(i * 2, 2), (Half)(i == 0 ? 1f : 0f));
        var bin = FramesFile.FromCenters(CentersStep.Build("dJ", f16, [1.0], 1).Bin); await fs.PutAsync(P.RetainedFile("dJ"), bin, null, null, false, default);
        await Group(fs, "dJ", "join", "dX", P.RetainedFile("dJ"));
        var r = await st.RunAsync(io, "dJ", false, 2, default); Assert.Equal(16, r.FacesUsed); Assert.Equal(1, r.Units); Assert.Equal("join", r.Kind);
        var doc = await Units(fs, "dJ"); Assert.Equal(16, doc.Units[0].N); Assert.Equal("dX", doc.Prod);   // a join counts as its target's production
        await Group(fs, "dJ", "new"); var all = await st.RunAsync(io, "dJ", false, 2, default); Assert.Equal(48, all.FacesUsed);   // brand new: every face
    }

    [Fact]
    public async Task An_AI_flagged_drama_is_skipped_and_an_AI_predecessor_is_ignored_and_never_read_until_added_by_hand()
    {
        var fs = new FakeStore(); var st = StageOn(fs); var io = new ActorsIO(fs, P);
        await Drama(fs, "dG", WithCos(0.99, 5), (200, 150, 100), seed: 1); await Drama(fs, "dH", WithCos(0.99, 5), (200, 150, 100), seed: 2); await Drama(fs, "dK", WithCos(0.99, 5), (200, 150, 100), seed: 3);
        foreach (var id in new[] { "dG", "dH", "dK" }) await Group(fs, id);
        await st.RunAsync(io, "dG", false, 2, default); Assert.NotNull(await fs.GetAsync(ActorsData.UnitFile(P, "dG"), default));   // dG was real when its units were written
        await SetAi(fs, "dG", true);   // the probe flags it afterwards
        fs.Gets.Clear(); var rH = await st.RunAsync(io, "dH", false, 2, default, ["dG"]);
        Assert.Equal(0, rH.Links); Assert.Equal(1, rH.PredecessorsAi); Assert.Equal(0, rH.Predecessors);
        Assert.DoesNotContain(fs.Gets, n => n.Contains("dG/cast/") || n == ActorsData.UnitFile(P, "dG") || n == ActorsData.R50File(P, "dG"));   // its cast data, units and vectors were never read
        // the index never routes to it either, and an index build leaves it out
        var rep = await ActorsIndex.BuildAsync(io, default, async id => ActorsStage.AiFlag((await VideoJson.ReadAsync(fs, P, id, default))?.Root)); Assert.Equal(new[] { 1 }, new[] { rep.Uploads });
        var (idx, _) = await ActorsIndex.LoadAsync(io, default); Assert.Equal(new[] { "dH" }, idx!.Uploads.Select(u => u.Id).ToArray());
        // an AI-flagged upload is skipped completely, and a leftover file of it is removed
        await SetAi(fs, "dK", true); var rK = await st.RunAsync(io, "dK", false, 2, default); Assert.NotNull(rK.Skipped); Assert.Null(await fs.GetAsync(ActorsData.UnitFile(P, "dK"), default));
        var rG = await st.RunAsync(io, "dG", false, 2, default); Assert.NotNull(rG.Skipped); Assert.Null(await fs.GetAsync(ActorsData.UnitFile(P, "dG"), default));
        // added by hand: it counts again (and stays in on later runs), and is a predecessor again
        var rM = await st.RunAsync(io, "dG", true, 2, default); Assert.Null(rM.Skipped); Assert.Equal(1, rM.Units);
        Assert.Contains(await io.ListAsync(ActorsData.UnitsDir(P), default), e => e.Name == ActorsData.UnitFile(P, "dG") && e.Manual);
        var again = await st.RunAsync(io, "dG", false, 2, default); Assert.Null(again.Skipped);
        var rK2 = await st.RunAsync(io, "dK", true, 2, default, ["dG"]); Assert.Equal(0, rK2.PredecessorsAi); Assert.True(rK2.Links >= 1);
        Assert.Equal(0, (await new ActorsIO(fs, P, true).ListAsync(ActorsData.UnitsDir(P), default)).Count(e => e.Name.Contains("zzz")));
    }

    [Fact]
    public async Task Dry_run_and_out_local_write_nothing_to_the_store_and_the_queue_never_gets_an_AI_upload()
    {
        var fs = new FakeStore(); await Drama(fs, "dA", WithCos(1, 1), (200, 150, 100), seed: 1); await Group(fs, "dA"); var names = fs.Names("index/").OrderBy(x => x).ToList();
        var dry = new ActorsIO(fs, P, true); var r = await StageOn(fs).RunAsync(dry, "dA", false, 2, default);
        Assert.Equal(names, fs.Names("index/").OrderBy(x => x).ToList()); Assert.Contains(dry.Log, l => l.StartsWith("put index/actors/units/dA.bin")); Assert.Equal(1, r.Units);
        var rep = await ActorsIndex.BuildAsync(dry, default); Assert.StartsWith("would write", rep.Note); Assert.Equal(names, fs.Names("index/").OrderBy(x => x).ToList());
        var dir = Path.Combine(Path.GetTempPath(), "actors-" + Guid.NewGuid().ToString("N"));
        try
        {
            var loc = new ActorsIO(fs, P, false, dir); await StageOn(fs).RunAsync(loc, "dA", false, 2, default); await ActorsIndex.BuildAsync(loc, default);
            Assert.True(File.Exists(Path.Combine(dir, "index", "actors", "units", "dA.bin"))); Assert.True(File.Exists(Path.Combine(dir, "index", "actors", "v1", "codes.bin"))); Assert.Equal(names, fs.Names("index/").OrderBy(x => x).ToList());
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
        // queueing: a flagged upload is not queued, a manual add is, marked as added by hand
        Assert.True(ActorsAdd.AiFlagged(new JsonObject { ["ai"] = new JsonObject { ["flag"] = true } })); Assert.False(ActorsAdd.AiFlagged(new JsonObject { ["ai"] = new JsonObject { ["flag"] = false } }));
        var line = ActorsAdd.ManualLine("dA", null, 3); Assert.Equal("actors", line.Stage); Assert.Equal(ActorsStage.ManualReason, line.Job!.Reason);
    }

    static async Task<JobQueue> Queue(FakeStore fs, params (string Id, long? Seq, string? Actors, string Group)[] subs)
    {
        var q = new JobQueue(fs, P, "t"); await q.LoadAsync(default); var lines = new List<QLine>();
        foreach (var x in subs)
        {
            lines.Add(QLine.Add(x.Id, "group", new JobDescriptor { Stage = "group", JobId = x.Id }, "2026-11-20T00:00:00Z", null, null, x.Group));
            if (x.Seq != null) lines.Add(new QLine { T = "seq", S = x.Id, Seq = x.Seq });
            if (x.Actors != null) lines.Add(QLine.Add(x.Id, "actors", new JobDescriptor { Stage = "actors", JobId = x.Id }, "2026-11-20T00:00:00Z", null, null, x.Actors));
        }
        await q.AppendLinesAsync(lines, default); return q;
    }

    [Fact]
    public async Task Preds_listed_keeps_only_the_listed_lower_seq_uploads_in_seq_order()
    {
        var fs = new FakeStore();
        foreach (var (id, sd) in new[] { ("dA", 1), ("dB", 2), ("dC", 3), ("dD", 4) }) { await Drama(fs, id, WithCos(1 - sd * 0.05, 1), ((byte)(sd * 40), (byte)10, (byte)20), seed: sd); await Group(fs, id, seq: sd); }
        var got = await ActorsCommandSupport.ListedAsync(fs, P, "dD", new[] { "dC", "dA", "dD", "dX" }, default);
        Assert.Equal(new[] { "dA", "dC" }, got.ToArray());   // dB is not listed, dD is itself, dX has no record
    }

    [Fact]
    public async Task The_actors_stage_and_the_index_build_fit_the_storage_budget_and_the_build_reads_only_the_new_units_files()
    {
        var fs = new FakeStore(); var m = new StoreMeter { Limits = new StorageLimits { MaxPerMin = 1000000, WarnPerMin = 1000000, MaxFailuresPerMin = 1000000 } }; var ms = new MeteredStore(fs, m);   // the steps run back to back here: only the per-step budget (1,000) is under test
        var ids = Enumerable.Range(0, 30).Select(i => "u" + i.ToString("D2")).ToList();
        for (int i = 0; i < ids.Count; i++) { await Drama(fs, ids[i], WithCos(0.30 + (i % 3) * 0.05, 1 + i % 7), ((byte)(i * 8), 40, 90), seed: i + 1); await Group(fs, ids[i], seq: i + 1); }
        var st = new ActorsStage(ms, null, 0.64) { MakeR50 = () => new ColourEmbedder(), Decode = Imaging.DecodeJpeg }; var io = new ActorsIO(ms, P);
        long worst = 0; string worstId = "";
        for (int i = 0; i < ids.Count; i++)
        {
            using (m.BeginStep()) { var before = m.Total; await st.RunAsync(io, ids[i], false, 2, default, ids.Take(i).ToList()); var ops = m.Total - before; if (ops > worst) { worst = ops; worstId = ids[i]; } }
        }
        Console.WriteLine($"BUDGET actors stage: worst step {worst} ops ({worstId}), predecessors up to 29");
        Assert.True(worst <= 1000, $"an actors step made {worst} ops");
        var b0 = m.Total; var rep = await ActorsIndex.BuildAsync(io, default); var buildOps = m.Total - b0;
        Console.WriteLine($"BUDGET actors-index first build of 30 uploads: {buildOps} ops, read {rep.Read} units files");
        await Drama(fs, "uNew", WithCos(0.5, 7), (9, 9, 9), seed: 99); await Group(fs, "uNew", seq: 31); await st.RunAsync(io, "uNew", false, 2, default, []);
        var b1 = m.Total; var rep2 = await ActorsIndex.BuildAsync(io, default); var inc = m.Total - b1;
        Console.WriteLine($"BUDGET actors-index next build (+1 upload): {inc} ops, reused {rep2.Reused}, read {rep2.Read}");
        Assert.Equal(1, rep2.Read); Assert.Equal(30, rep2.Reused); Assert.True(inc <= 200, $"the incremental build made {inc} ops");
    }
}
