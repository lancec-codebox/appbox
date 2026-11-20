using System.Text;
using Xunit;

namespace IndexNode.Tests;

public class CentersTests
{
    static readonly Paths P = new("index/");
    const int D = CentersFile.Dim;

    /// <summary>n unit vectors around `groups` random directions (noise 0.3), as f32 rows; deterministic.</summary>
    static float[] Make(int n, int groups, int seed, double noise = 0.3)
    {
        var rng = new Random(seed); var dirs = new float[groups][];
        for (int g = 0; g < groups; g++) { dirs[g] = Gauss(rng); }
        var x = new float[n * D];
        for (int i = 0; i < n; i++)
        {
            var d = dirs[i % groups]; var n2 = Gauss(rng); var row = new float[D];
            for (int j = 0; j < D; j++) row[j] = d[j] + (float)noise * n2[j];
            Vec.Normalize(row); row.CopyTo(x, i * D);
        }
        return x;
    }
    static float[] Gauss(Random r) { var v = new float[D]; for (int j = 0; j < D; j++) v[j] = (float)(Math.Sqrt(-2 * Math.Log(1 - r.NextDouble())) * Math.Cos(2 * Math.PI * r.NextDouble())); Vec.Normalize(v); return v; }
    static byte[] F16(float[] x) { var b = new byte[x.Length * 2]; for (int i = 0; i < x.Length; i++) BitConverter.TryWriteBytes(b.AsSpan(i * 2, 2), (Half)x[i]); return b; }
    static float[] Row(byte[] f16, int i) { var r = new float[D]; Vec.FromHalf(f16.AsSpan(i * D * 2, D * 2), r); return r; }
    static double[] Times(int n) => Enumerable.Range(0, n).Select(i => i * 1.0).ToArray();

    [Fact]
    public void Dot_matches_a_plain_loop()
    {
        var rng = new Random(1); var a = Gauss(rng); var b = Gauss(rng); double e = 0; for (int i = 0; i < D; i++) e += (double)a[i] * b[i];
        Assert.Equal(e, Vec.Dot(a, b), 4);
        Assert.Equal(0.5f + 1 + 1.5f + 1 + 1, Vec.Dot(new float[] { 1, 2, 3, 4, 5 }, new float[] { 0.5f, 0.5f, 0.5f, 0.25f, 0.2f }), 4);   // odd length: the scalar tail
    }

    [Fact]
    public void KMeans_is_deterministic_for_a_seed_and_independent_of_the_thread_count()
    {
        var x = Make(900, 12, 5); var a = KMeans.Run(x, 900, D, 16, 10, KMeans.SeedOf("abc"), 1); var b = KMeans.Run(x, 900, D, 16, 10, KMeans.SeedOf("abc"), 4); var c = KMeans.Run(x, 900, D, 16, 10, KMeans.SeedOf("abd"), 1);
        Assert.Equal(a.Assign, b.Assign); Assert.Equal(a.Centers, b.Centers); Assert.Equal(a.Sizes, b.Sizes);
        Assert.NotEqual(a.Centers, c.Centers);   // another upload id, another seed
        Assert.Equal(KMeans.SeedOf("abc"), KMeans.SeedOf("abc"));
    }

    [Fact]
    public void KMeans_puts_every_row_in_exactly_one_cluster_and_drops_empty_ones()
    {
        var x = Make(500, 7, 9);
        var r = KMeans.Run(x, 500, D, 40, 10, 3, 2);
        Assert.Equal(500, r.Assign.Length); Assert.Equal(500, r.Sizes.Sum()); Assert.All(r.Sizes, s => Assert.True(s > 0));
        Assert.Equal(r.K, r.Sizes.Length); Assert.All(r.Assign, a => Assert.InRange(a, 0, r.K - 1));
        for (int c = 0; c < r.K; c++) Assert.Equal(r.Sizes[c], r.Assign.Count(a => a == c));
        for (int c = 0; c < r.K; c++) { double s = 0; for (int j = 0; j < D; j++) s += r.Centers[c * D + j] * (double)r.Centers[c * D + j]; Assert.Equal(1.0, s, 3); }   // unit centers
        Assert.Equal(5, KMeans.Run(Make(5, 5, 1), 5, D, 256, 10, 1, 1).K);   // k = min(256, frames)
        Assert.Equal(0, KMeans.Run([], 0, D, 256, 10, 1, 1).K);
    }

    [Fact]
    public void KMeans_with_duplicate_rows_gives_fewer_centers_without_failing()
    {
        var one = Make(1, 1, 2); var x = new float[60 * D]; for (int i = 0; i < 60; i++) one.CopyTo(x, i * D);
        var r = KMeans.Run(x, 60, D, 16, 10, 1, 1);
        Assert.Equal(60, r.Sizes.Sum()); Assert.True(r.K >= 1 && r.K <= 16);
    }

    static (byte[] Bin, byte[] F16, KMeansResult R, double[] T, float[] Sum) Build(int n, int groups, int k = 24)
    {
        var x = Make(n, groups, 11); var f16 = F16(x); var t = Times(n); var xr = new float[n * D]; Vec.FromHalf(f16, xr);
        var r = KMeans.Run(xr, n, D, k, 10, 7, 2); var sum = new float[D]; for (int i = 0; i < n; i++) Vec.Add(sum, xr.AsSpan(i * D, D));
        return (CentersFile.Write(r, f16, t.Select(v => (float)v).ToArray(), Enumerable.Range(0, n).ToArray(), CentersFile.HashOf(f16), sum), f16, r, t, sum);
    }

    [Fact]
    public void Centers_bin_round_trips_header_centers_table_and_members()
    {
        var (bin, f16, r, t, sum) = Build(700, 20);
        var h = CentersFile.ParseHeader(bin);
        Assert.Equal((1, r.K, 512, 700, CentersFile.Build), (h.Version, h.K, h.Dim, h.Frames, h.Build));
        Assert.Equal(CentersFile.HashOf(f16), h.InputHash); Assert.Equal(sum, h.Sum); Assert.Equal(bin.Length, h.TotalLength);
        Assert.Equal((long)CentersFile.HeaderSize, h.CentersOff); Assert.Equal(h.CentersOff + (long)r.K * 1024, h.TableOff); Assert.Equal(h.TableOff + (long)r.K * 16, h.MembersOff);
        var cs = CentersFile.ParseCenters(bin.AsSpan((int)h.CentersOff, r.K * 1024), r.K);
        for (int i = 0; i < cs.Length; i++) Assert.Equal((float)(Half)r.Centers[i], cs[i]);
        var table = CentersFile.ParseTable(bin.AsSpan((int)h.TableOff, r.K * 16), r.K);
        Assert.Equal(r.Sizes, table.Select(b => b.Count).ToArray());
        long expect = h.MembersOff; foreach (var b in table) { Assert.Equal(expect, b.Offset); Assert.Equal(CentersFile.BlockLength(b.Count), b.Length); expect += b.Length; }
        Assert.Equal(bin.Length, expect);
        var seen = new HashSet<int>();
        for (int c = 0; c < r.K; c++)
        {
            var m = CentersFile.ParseBlock(bin.AsSpan((int)table[c].Offset, table[c].Length), table[c].Count);
            var inMem = Enumerable.Range(0, 700).Where(i => r.Assign[i] == c).ToArray();   // the in-memory members of cluster c, in row order
            Assert.Equal(inMem, m.Rows);
            for (int i = 0; i < m.Count; i++) { Assert.Equal((float)t[m.Rows[i]], m.Times[i]); Assert.Equal(f16.AsSpan(m.Rows[i] * 1024, 1024).ToArray(), m.Vectors.AsSpan(i * 1024, 1024).ToArray()); Assert.True(seen.Add(m.Rows[i])); }
        }
        Assert.Equal(700, seen.Count);   // every frame in exactly one block
        Assert.Throws<InvalidDataException>(() => CentersFile.ParseHeader(new byte[CentersFile.HeaderSize]));
    }

    [Fact]
    public async Task A_member_block_range_read_through_the_zip_equals_the_in_memory_members()
    {
        var (bin, f16, r, _, _) = Build(600, 18); var dir = Path.Combine(Path.GetTempPath(), "ct-" + Guid.NewGuid().ToString("N")[..8]); Directory.CreateDirectory(dir);
        try
        {
            var z = Path.Combine(dir, "centers.zip"); ZipFormat.Write(z, [new(CentersFile.Member, null, bin), new(CentersFile.ManifestMember, null, Encoding.UTF8.GetBytes("{}"))]);
            var fs = new FakeStore(); await fs.PutZipAsync(P.Video("u1", "centers.zip"), z, new Dictionary<string, string>(), null, false, default);
            var cz = (await CentersZip.OpenAsync(fs, P.Video("u1", "centers.zip"), default))!;
            var h = await cz.ReadHeaderAsync(default); Assert.Equal(r.K, h.K);
            var table = await cz.ReadTableAsync(h, default);
            for (int c = 0; c < r.K; c++)
            {
                var m = await cz.ReadBlockAsync(table[c], default);
                var rows = Enumerable.Range(0, 600).Where(i => r.Assign[i] == c).ToArray(); Assert.Equal(rows, m.Rows);
                for (int i = 0; i < m.Count; i++) Assert.Equal(f16.AsSpan(rows[i] * 1024, 1024).ToArray(), m.Vectors.AsSpan(i * 1024, 1024).ToArray());
            }
            // merged reads give the same blocks in fewer requests
            var cz2 = CentersZip.FromIndex(fs, P.Video("u1", "centers.zip"), new SearchUpload("u1", "", cz.ETag!, cz.ZipSize, cz.DataStart, cz.DataLength, 600, r.K, 0));
            var many = await cz2.ReadBlocksAsync([3, 4, 5, 9], 1 << 20, 1 << 22, default);
            Assert.Equal([3, 4, 5, 9], many.Select(x => x.Center).ToArray()); Assert.Equal(4, many.Count);
            Assert.Equal(Enumerable.Range(0, 600).Where(i => r.Assign[i] == 4).ToArray(), many[1].Members.Rows);
            Assert.True(cz2.Requests <= 4, "requests " + cz2.Requests);   // header, table, then the blocks in one merged read
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Bit_codes_and_the_asymmetric_score_match_a_straightforward_reference()
    {
        var rng = new Random(4); var mean = Gauss(rng); for (int i = 0; i < D; i++) mean[i] *= 0.3f;
        var cs = Enumerable.Range(0, 50).Select(_ => Gauss(rng)).ToArray(); var q = Gauss(rng); var qm = CenterSearch.MinusMean(q, mean); var lut = SearchData.Lut(qm);
        foreach (var c in cs)
        {
            var code = new byte[64]; SearchData.Code(c, mean, code);
            double reference = 0;
            for (int d = 0; d < D; d++) { bool bit = c[d] - mean[d] > 0; reference += bit ? qm[d] : -qm[d]; }   // plain +-1 reading of the bits
            for (int d = 0; d < D; d++) Assert.Equal(c[d] - mean[d] > 0, (code[d / 8] >> (d % 8) & 1) == 1);
            Assert.Equal(reference, SearchData.ScoreCode(lut, code), 3);
        }
    }

    static SearchData Data(float[] mean, float[][] centers, int uploads = 1) => new()
    {
        Mean = mean, Uploads = Enumerable.Range(0, uploads).Select(u => new SearchUpload("u" + u, "s", "e", 1, 0, 1, 1, centers.Length / uploads, u * (centers.Length / uploads))).ToList(),
        Bits = centers.SelectMany(c => { var b = new byte[64]; SearchData.Code(c, mean, b); return b; }).ToArray(),
        RefUpload = Enumerable.Range(0, centers.Length).Select(i => i / (centers.Length / uploads)).ToArray(), RefCenter = Enumerable.Range(0, centers.Length).Select(i => i % (centers.Length / uploads)).ToArray(),
    };

    [Fact]
    public void Scan_returns_the_rows_a_brute_force_score_ranks_highest_whatever_the_thread_count()
    {
        var rng = new Random(8); var mean = Gauss(rng); for (int i = 0; i < D; i++) mean[i] *= 0.2f;
        var cs = Enumerable.Range(0, 20000).Select(_ => Gauss(rng)).ToArray(); var idx = Data(mean, cs, 1); var q = CenterSearch.MinusMean(Gauss(rng), mean); var lut = SearchData.Lut(q);
        var expect = Enumerable.Range(0, cs.Length).OrderByDescending(i => SearchData.ScoreCode(lut, idx.Bits.AsSpan(i * 64, 64))).ThenBy(i => i).Take(100).ToArray();
        Assert.Equal(expect, idx.Scan(q, 100, 1)); Assert.Equal(expect, idx.Scan(q, 100, 4));
        Assert.Equal(cs.Length, idx.Scan(q, 99999, 2).Length);
    }

    [Fact]
    public void Uploads_yaml_head_and_files_round_trip()
    {
        var idx = Data(Gauss(new Random(2)), Enumerable.Range(0, 4).Select(i => Gauss(new Random(i))).ToArray(), 2); idx.Version = 3; idx.MeanVersion = 2; idx.MeanFrames = 12345;
        idx.Uploads[1] = idx.Uploads[1] with { Id = "we\"ird:id", ETag = "\"0x8DC\"" };
        var (v, mv, mf, ups) = SearchData.ParseUploadsYaml(idx.UploadsYaml());
        Assert.Equal((3, 2, 12345L), (v, mv, mf)); Assert.Equal(idx.Uploads, ups);
        Assert.Equal(7, SearchData.ParseHead(SearchData.HeadYaml(7, "index/search/v7/")));
        Assert.Equal(idx.Mean, SearchData.ParseMean(SearchData.MeanBytes(idx.Mean)));
    }

    /// <summary>The real pipeline on a FakeStore: sscd vectors in sscd.zip -> centers step -> centers.zip -> search-index build/update -> a planted frame is the top hit.</summary>
    static async Task<(FakeStore Fs, StageZipStore Wr)> Drama(string id, byte[] f16, int n, string cache, FakeStore? fs = null)
    {
        fs ??= new FakeStore(); var wr = (StageZipStore)StageZipStore.Wrap(fs, cache);
        var frames = Enumerable.Range(0, n).Select(i => new ChunkFrame(i, i * 1.0, i * 1.0, [], null, i, 1)).ToList();
        await wr.PutAsync(P.Video(id, "frames/0000.json"), FrameChunks.JpegIndexJson("0000", 0, 360, 640, 85, frames), null, null, false, default);
        await wr.PutAsync(P.Video(id, "frames/manifest.json"), FrameChunks.ManifestJson("frames", 360, 640, 0, n, [("0000", 0, n, (long)n)], null, "mjpeg", "jpeg"), null, null, false, default);
        await wr.PutAsync(P.Video(id, "sscd/0000.f16"), f16, null, null, false, default);
        await wr.PutAsync(P.Video(id, "sscd/manifest.json"), Encoding.UTF8.GetBytes($"{{\"v\":1,\"step\":\"sscd\",\"chunks\":[{{\"chunk\":\"0000\",\"start\":0,\"rows\":{n}}}]}}"), null, null, false, default);
        foreach (var st in new[] { "frames", "sscd" }) Assert.True(await StageZipStore.Stage(wr)!.CommitStageAsync(id, st, "n1", false, false, default));
        return (fs, wr);
    }

    [Fact]
    public async Task The_centers_step_writes_a_stored_centers_zip_through_the_stage_zip_store_and_marks_it_stale_when_sscd_changes()
    {
        var cache = Path.Combine(Path.GetTempPath(), "cz-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var x = Make(400, 15, 21); var f16 = F16(x);
            var (fs, wr) = await Drama("up1", f16, 400, cache);
            var wst = new RecordingStore(wr);
            Assert.False(await CentersStep.StaleAsync(wr, P, "up1", default));   // no centers.zip yet: not stale, just missing
            var msg = await CentersStep.RunAsync(wst, P, "up1", 2, null, default);
            Assert.StartsWith("400 frames -> ", msg);
            Assert.True(await StageCommit.CommitAsync(wr, "up1", "centers", "n1", false, default));
            Assert.True(fs.Exists(P.Video("up1", "centers.zip")));
            var es = await ZipFormat.ReadDirectoryAsync(new FileZipSource(WriteOut(fs, P.Video("up1", "centers.zip"), cache)), default);
            Assert.Equal(new[] { CentersFile.ManifestMember, CentersFile.Member }.Order().ToArray(), es.Select(e => e.Name).Order().ToArray()); Assert.All(es, e => Assert.Equal(0, e.Method));   // stored
            var meta = fs[P.Video("up1", "centers.zip")].Meta; Assert.Equal(StageLayout.Inputs([fs[P.Video("up1", "sscd.zip")].Meta["idx_sha256"]]), meta["idx_inputs"]);
            Assert.False(await CentersStep.StaleAsync(wr, P, "up1", default));
            var man = System.Text.Json.JsonDocument.Parse((await wr.GetAsync(P.Video("up1", CentersFile.ManifestMember), default))!.Data).RootElement;
            Assert.Equal("centers", man.GetProperty("step").GetString()); Assert.Equal(400, man.GetProperty("frames").GetInt32());
            // sscd.zip rewritten -> the step is due again
            var (fs2, wr2) = await Drama("up1", F16(Make(400, 15, 22)), 400, cache + "b", fs);
            Assert.True(await CentersStep.StaleAsync(fs2, P, "up1", default));
            Assert.Equal(["up1"], await CentersCommands.PendingAsync(fs2, P, null, null, default));
            Assert.Empty(await CentersCommands.PendingAsync(fs2, P, new HashSet<string> { "other" }, null, default));
        }
        finally { try { Directory.Delete(cache, true); } catch { } try { Directory.Delete(cache + "b", true); } catch { } }
    }

/// <summary>Local-only flow (0.11): fake uploads with sscd.zip in a plain folder -> centers backfill -> search-index build -> search finds the planted frame. No SAS, no network.</summary>
    [Fact]
    public async Task Local_folder_flow_backfill_then_search_index_build_then_search_finds_the_planted_frame()
    {
        var root = Path.Combine(Path.GetTempPath(), "cl-" + Guid.NewGuid().ToString("N")[..8]); Directory.CreateDirectory(root);
        var oldOut = Console.Out; var sw = new StringWriter();
        try
        {
            var local = new LocalStore(root); var wr = (StageZipStore)StageZipStore.Wrap(local, Path.Combine(root, ".indexnode-zips"));
            var planted = new float[D];
            foreach (var (id, seed) in new[] { ("a1", 41), ("b2", 42), ("c3", 43) })
            {
                var x = Make(300, 20, seed, 0.5); if (id == "b2") planted = x.AsSpan(77 * D, D).ToArray();
                var n = 300; var f16 = F16(x);
                var frames = Enumerable.Range(0, n).Select(i => new ChunkFrame(i, i * 1.0, i * 1.0, [], null, i, 1)).ToList();
                await wr.PutAsync(P.Video(id, "frames/0000.json"), FrameChunks.JpegIndexJson("0000", 0, 360, 640, 85, frames), null, null, false, default);
                await wr.PutAsync(P.Video(id, "frames/manifest.json"), FrameChunks.ManifestJson("frames", 360, 640, 0, n, [("0000", 0, n, (long)n)], null, "mjpeg", "jpeg"), null, null, false, default);
                await wr.PutAsync(P.Video(id, "sscd/0000.f16"), f16, null, null, false, default);
                await wr.PutAsync(P.Video(id, "sscd/manifest.json"), Encoding.UTF8.GetBytes($"{{\"v\":1,\"step\":\"sscd\",\"chunks\":[{{\"chunk\":\"0000\",\"start\":0,\"rows\":{n}}}]}}"), null, null, false, default);
                foreach (var st in new[] { "frames", "sscd" }) Assert.True(await StageZipStore.Stage(wr)!.CommitStageAsync(id, st, "n1", false, false, default));
            }
            Console.SetOut(sw);
            // each command opens the folder afresh, as separate CLI runs do
            Assert.Equal(0, await CentersCommands.CentersAsync(Args.Parse(["backfill", "--local", root, "--threads", "2"])));
            Assert.Equal(0, await CentersCommands.SearchIndexAsync(Args.Parse(["build", "--local", root, "--threads", "2"])));
            var q = planted.ToArray(); var rng = new Random(5); for (int d = 0; d < D; d++) q[d] += 0.02f * (float)(rng.NextDouble() - 0.5); Vec.Normalize(q);
            var vf = Path.Combine(root, "q.f32"); var qb = new byte[D * 4]; Buffer.BlockCopy(q, 0, qb, 0, qb.Length); File.WriteAllBytes(vf, qb);
            Assert.Equal(0, await CentersCommands.SearchAsync(Args.Parse(["--vector", vf, "--local", root, "--top", "3", "--threads", "2"])));
            Console.SetOut(oldOut); var o = sw.ToString();
            Assert.Contains("3 upload(s) to build", o); Assert.Contains("published v1", o);
            var hit = o.Split('\n').First(l => l.StartsWith("a1 ") || l.StartsWith("b2 ") || l.StartsWith("c3 "));
            Assert.StartsWith("b2 ", hit); Assert.Contains("77.0s", hit);
            Assert.True(File.Exists(Path.Combine(root, "index", "search", "head.yaml")));
            Assert.True(File.Exists(Path.Combine(root, "index", "videos", "b2", "centers.zip")));
            // a second backfill has nothing left to do (the sidecar metadata kept idx_inputs)
            Console.SetOut(sw = new StringWriter());
            Assert.Equal(0, await CentersCommands.CentersAsync(Args.Parse(["backfill", "--local", root, "--dry"])));
            Console.SetOut(oldOut); Assert.Contains("0 upload(s) to build", sw.ToString());
        }
        finally { Console.SetOut(oldOut); try { Directory.Delete(root, true); } catch { } }
    }

    /// <summary>Split mode: input = a fake remote store (read-only), output = a local folder; centers.zip and the index land in the folder, the store is never written, and search then runs locally without the store.</summary>
    [Fact]
    public async Task Input_from_a_remote_store_output_to_a_local_folder_then_search_locally_and_the_remote_is_never_written()
    {
        var cache = Path.Combine(Path.GetTempPath(), "cr-" + Guid.NewGuid().ToString("N")[..8]); var root = Path.Combine(Path.GetTempPath(), "co-" + Guid.NewGuid().ToString("N")[..8]);
        var oldOut = Console.Out; var sw = new StringWriter();
        try
        {
            FakeStore? fs = null; var planted = new float[D];
            foreach (var (id, seed) in new[] { ("a1", 51), ("b2", 52) })
            {
                var x = Make(300, 20, seed, 0.5); if (id == "b2") planted = x.AsSpan(200 * D, D).ToArray();
                (fs, _) = await Drama(id, F16(x), 300, cache + id, fs);
            }
            var before = fs!.Names("").ToList(); var sizes = before.ToDictionary(n => n, n => fs[n].Data.Length);
            Console.SetOut(sw);
            Assert.Equal(0, await CentersCommands.CentersAsync(Args.Parse(["backfill", "--output-local", root, "--threads", "2"]), fs));
            Assert.Equal(0, await CentersCommands.SearchIndexAsync(Args.Parse(["build", "--output-local", root, "--threads", "2"]), fs));
            var q = planted.ToArray(); var rng = new Random(6); for (int d = 0; d < D; d++) q[d] += 0.02f * (float)(rng.NextDouble() - 0.5); Vec.Normalize(q);
            var vf = Path.Combine(Path.GetTempPath(), "q-" + Guid.NewGuid().ToString("N")[..6] + ".f32"); var qb = new byte[D * 4]; Buffer.BlockCopy(q, 0, qb, 0, qb.Length); File.WriteAllBytes(vf, qb);
            // search reads the local index and local centers.zip: no remote store given at all
            Assert.Equal(0, await CentersCommands.SearchAsync(Args.Parse(["--vector", vf, "--output-local", root, "--top", "3", "--threads", "2"])));
            Console.SetOut(oldOut); var o = sw.ToString(); File.Delete(vf);
            Assert.Contains("2 upload(s) to build", o);
            var hit = o.Split('\n').First(l => l.StartsWith("a1 ") || l.StartsWith("b2 ")); Assert.StartsWith("b2 ", hit); Assert.Contains("200.0s", hit);
            Assert.True(File.Exists(Path.Combine(root, "index", "videos", "b2", "centers.zip"))); Assert.True(File.Exists(Path.Combine(root, "index", "search", "head.yaml")));
            Assert.False(File.Exists(Path.Combine(root, "index", "videos", "b2", "sscd.zip")));   // inputs stay remote
            Assert.Equal(before, fs.Names("").ToList()); foreach (var n in before) Assert.Equal(sizes[n], fs[n].Data.Length);   // the store is untouched
        }
        finally { Console.SetOut(oldOut); foreach (var d in new[] { root, cache + "a1", cache + "b2" }) try { Directory.Delete(d, true); } catch { } }
    }

    static string WriteOut(FakeStore fs, string blob, string dir) { Directory.CreateDirectory(dir); var p = Path.Combine(dir, "x-" + Guid.NewGuid().ToString("N")[..6] + ".zip"); File.WriteAllBytes(p, fs[blob].Data); return p; }

    [Fact]
    public async Task Search_finds_a_planted_frame_as_the_top_hit_and_update_appends_new_uploads()
    {
        var cache = Path.Combine(Path.GetTempPath(), "cs-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            FakeStore? fs = null; var planted = (Id: "", Row: 0, Vec: new float[D]);
            foreach (var (id, seed) in new[] { ("a1", 31), ("b2", 32), ("c3", 33) })
            {
                var x = Make(500, 30, seed, 0.5); var (f, wr) = await Drama(id, F16(x), 500, cache + id, fs); fs = f;
                await CentersStep.RunAsync(wr, P, id, 2, null, default); Assert.True(await StageCommit.CommitAsync(wr, id, "centers", "n1", false, default));
                if (id == "b2") { planted = (id, 123, x.AsSpan(123 * D, D).ToArray()); }
                if (id == "c3") break;
            }
            var raw = fs!; var ups = await SearchIndexBuild.ListCentersAsync(raw, P, default);
            Assert.Equal(["a1", "b2", "c3"], ups.Select(u => u.Id).ToArray());
            var idx = await SearchIndexBuild.BuildAsync(raw, P, ups.Where(u => u.Id != "c3").ToList(), null, null, 1, 1, 2, _ => { }, default);   // c3 is added later by update
            Assert.Equal(2, idx.Uploads.Count); Assert.Equal(idx.Count, idx.Bits.Length / 64);
            Assert.Equal(1000, idx.MeanFrames);
            await idx.PublishAsync(raw, P, default);
            var loaded = (await SearchData.LoadAsync(raw, P, null, default))!;
            Assert.Equal(idx.Bits, loaded.Bits); Assert.Equal(idx.Mean, loaded.Mean); Assert.Equal(idx.RefUpload, loaded.RefUpload); Assert.Equal(idx.Uploads, loaded.Uploads);
            // the mean is (sum of the uploads' sums) / (total frames)
            var all = new float[2 * 500 * D]; int o = 0; foreach (var id in new[] { "a1", "b2" }) { var f16 = (await new StoreJob(StageZipStore.Wrap(raw, cache + "rd"), P, id).ReadAsync("sscd/0000.f16", default)) ?? throw new Exception("no sscd"); Vec.FromHalf(f16, all.AsSpan(o, 500 * D)); o += 500 * D; }
            for (int d = 0; d < D; d += 37) { double s = 0; for (int i = 0; i < 1000; i++) s += all[i * D + d]; Assert.Equal(s / 1000, idx.Mean[d], 4); }

            var query = planted.Vec.ToArray(); var rng = new Random(77); for (int d = 0; d < D; d++) query[d] += 0.02f * (float)(rng.NextDouble() - 0.5); Vec.Normalize(query);
            var (hits, tm) = await CenterSearch.QueryAsync(raw, P, loaded, query, 60, 5, 2, default);
            Assert.Equal(planted.Id, hits[0].Upload); Assert.Equal(planted.Row, hits[0].Row); Assert.Equal(planted.Row, hits[0].Time); Assert.True(hits[0].Score > 0.95f, "score " + hits[0].Score);
            Assert.True(hits.Zip(hits.Skip(1)).All(p => p.First.Score >= p.Second.Score)); Assert.True(tm.Requests > 0);
            // more centers never lose the best one; the exact score is the plain cosine
            var (hits2, _) = await CenterSearch.QueryAsync(raw, P, loaded, query, 300, 1, 1, default); Assert.Equal(hits[0].Row, hits2[0].Row);

            // update: c3 appended with the same mean; the old rows are copied, not rebuilt
            var upd = await SearchIndexBuild.BuildAsync(raw, P, ups, loaded.Mean, loaded, 2, loaded.MeanVersion, 2, _ => { }, default);
            Assert.Equal(3, upd.Uploads.Count); Assert.Equal(loaded.Mean, upd.Mean); Assert.Equal(loaded.Bits, upd.Bits.AsSpan(0, loaded.Bits.Length).ToArray()); Assert.Equal(upd.Count, upd.Bits.Length / 64);
            Assert.Equal(loaded.Count, upd.Uploads[2].RowStart);
            await upd.PublishAsync(raw, P, default);
            Assert.Equal(2, SearchData.ParseHead((await raw.GetAsync(SearchData.HeadName(P), default))!.Data));
            await Assert.ThrowsAsync<InvalidOperationException>(() => upd.PublishAsync(raw, P, default));   // v2 exists already: a second publisher loses
            // a new mean rebuilds every code from the stored center vectors
            var nm = await SearchIndexBuild.BuildAsync(raw, P, ups, null, null, 3, 2, 2, _ => { }, default);
            Assert.NotEqual(loaded.Mean, nm.Mean); Assert.Equal(3, nm.Uploads.Count); Assert.Equal(1500, nm.MeanFrames);
        }
        finally { foreach (var d in Directory.GetDirectories(Path.GetTempPath(), "cs-*")) try { Directory.Delete(d, true); } catch { } }
    }

    [Fact]
    public async Task Process_job_steps_centers_works_on_a_store_that_has_only_stage_zips_and_replaces_centers_zip()
    {
        var cache = Path.Combine(Path.GetTempPath(), "cp-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var (fs, _) = await Drama("up9", F16(Make(300, 10, 5)), 300, cache);
            await fs.PutAsync(P.Video("up9", "download.zip"), [1], null, null, false, default);
            var wr0 = (StageZipStore)StageZipStore.Wrap(fs, cache + "d"); await wr0.PutAsync(P.Video("up9", "downloaded.json"), Encoding.UTF8.GetBytes("{\"durationSeconds\":300}"), null, null, false, default);
            await wr0.PutAsync(P.Video("up9", "info.json"), Encoding.UTF8.GetBytes("{}"), null, null, false, default); Assert.True(await StageZipStore.Stage(wr0)!.CommitStageAsync("up9", "download", "n", false, false, default));
            Assert.DoesNotContain(fs.Names("index/videos/up9/"), n => n.EndsWith(".json"));
            var store = SiteCommands.ProcessStore(fs); ProcessStage.Ai = new AiConfig();
            for (int round = 0; round < 2; round++)
            {
                var ctx = new JobContext { Job = new JobDescriptor { Stage = "process", JobId = "up9", Steps = ["centers"] }, Stage = "process", BlobName = "", LeaseId = "", Worker = 0, Store = store, Paths = P, NodeId = "t", Token = default, ThreadShare = () => 1, Report = (_, _) => { } };
                await new ProcessStage(new Tools(new HttpClient()), store, new NodeConfig(), new HttpClient()) { Redo = true }.RunAsync(ctx);
                Assert.True(fs.Exists(P.Video("up9", "centers.zip"))); Assert.Equal(round + 1, fs[P.Video("up9", "centers.zip")].Version);   // 2nd round replaces it
            }
            var man = System.Text.Json.JsonDocument.Parse((await store.GetAsync(P.Video("up9", CentersFile.ManifestMember), default))!.Data).RootElement;
            Assert.Equal(300, man.GetProperty("frames").GetInt32());
        }
        finally { foreach (var d in Directory.GetDirectories(Path.GetTempPath(), "cp-*")) try { Directory.Delete(d, true); } catch { } }
    }

    [Fact]
    public void Timing_of_the_centers_step_on_a_synthetic_6700_frame_upload()
    {
        var x = Make(6700, 300, 3, 0.6); var f16 = F16(x); var t = Times(6700);
        CentersStep.Build("warm", F16(Make(300, 20, 1)), Times(300), 1);   // JIT
        var s1 = System.Diagnostics.Stopwatch.StartNew(); var a = CentersStep.Build("timing", f16, t, 1); s1.Stop();
        int th = Environment.ProcessorCount; var sn = System.Diagnostics.Stopwatch.StartNew(); var b = CentersStep.Build("timing", f16, t, th); sn.Stop();
        Assert.Equal(a.Bin, b.Bin);   // byte-identical whatever the thread count
        Assert.InRange(a.Header.K, 200, 256);   // empty centers are dropped, so a few fewer than 256 is normal
        var line = ($"CENTERS-TIMING frames=6700 k={a.Header.K} bytes={a.Bin.Length} single-thread={s1.Elapsed.TotalSeconds:F3}s threads={th} node-share={sn.Elapsed.TotalSeconds:F3}s (kmeans only: {a.Result.TotalSeconds:F3}s / {b.Result.TotalSeconds:F3}s)");
        var o = Environment.GetEnvironmentVariable("HEXAD_SCRATCH"); if (o != null) File.WriteAllText(Path.Combine(o, "centers-timing.txt"), line);
    }

    [Fact]
    public void The_centers_step_is_a_pass_step_after_sscd_that_needs_sscd_zip()
    {
        Assert.Contains("centers", Protocol.StepStages); Assert.Contains("centers", Protocol.IngestStepStages);
        Assert.Equal(["sscd.zip"], JobDescriptor.NewStep("centers", "v").Needs);
        var po = StageOrder.PassOrder; Assert.True(Array.IndexOf(po, "centers") == Array.IndexOf(po, "sscd") + 1);
        Assert.Equal("centers", StageLayout.StageOf(CentersFile.Member)); Assert.Equal(["sscd"], StageLayout.InputStages["centers"]);
        Assert.Contains("centers", ProcessStage.KnownSteps); Assert.Contains("centers", ProcessStage.FullSteps);
    }
}
