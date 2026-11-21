using System.Text;
using Xunit;

namespace IndexNode.Tests;

/// <summary>0.13.9: the lookup readers read index/search/frames/&lt;id&gt;.bin and never centers.zip.</summary>
[Collection("console")]
public class FramesFileTests
{
    static readonly Paths P = new("index/");
    const int D = CentersFile.Dim;
    static float[] Make(int n, int groups, int seed)
    {
        var rng = new Random(seed); var dirs = new float[groups][]; var x = new float[n * D];
        float[] G() { var v = new float[D]; for (int j = 0; j < D; j++) v[j] = (float)(Math.Sqrt(-2 * Math.Log(1 - rng.NextDouble())) * Math.Cos(2 * Math.PI * rng.NextDouble())); Vec.Normalize(v); return v; }
        for (int g = 0; g < groups; g++) dirs[g] = G();
        for (int i = 0; i < n; i++) { var d = dirs[i % groups]; var z = G(); var row = new float[D]; for (int j = 0; j < D; j++) row[j] = d[j] + 0.3f * z[j]; Vec.Normalize(row); row.CopyTo(x, i * D); }
        return x;
    }
    static byte[] F16(float[] x) { var b = new byte[x.Length * 2]; for (int i = 0; i < x.Length; i++) BitConverter.TryWriteBytes(b.AsSpan(i * 2, 2), (Half)x[i]); return b; }

    static async Task<(FakeStore Fs, StageZipStore Wr)> Drama(string id, byte[] f16, int n, string cache, FakeStore? fs = null)
    {
        fs ??= new FakeStore(); var wr = (StageZipStore)StageZipStore.Wrap(fs, cache);
        var frames = Enumerable.Range(0, n).Select(i => new ChunkFrame(i, i * 1.0, i * 1.0, [], null, i, 1)).ToList();
        await wr.PutAsync(P.Video(id, "frames/0000.json"), FrameChunks.JpegIndexJson("0000", 0, 360, 640, 85, frames), null, null, false, default);
        await wr.PutAsync(P.Video(id, "frames/manifest.json"), FrameChunks.ManifestJson("frames", 360, 640, 0, n, [("0000", 0, n, (long)n)], null, "mjpeg", "jpeg"), null, null, false, default);
        await wr.PutAsync(P.Video(id, "sscd/0000.f16"), f16, null, null, false, default);
        await wr.PutAsync(P.Video(id, "sscd/manifest.json"), Encoding.UTF8.GetBytes($"{{\"v\":1,\"step\":\"sscd\",\"chunks\":[{{\"chunk\":\"0000\",\"start\":0,\"rows\":{n}}}]}}"), null, null, false, default);
        foreach (var st in new[] { "frames", "sscd" }) Assert.True(await StageZipStore.Stage(wr)!.CommitStageAsync(id, st, "n1", false, false, default));
        await VideoJsonSeed.PutAsync(fs, P, id, 300);
        return (fs, wr);
    }
    static async Task Centers(StageZipStore wr, string id) { await CentersStep.RunAsync(new RecordingStore(wr), P, id, 2, null, default); Assert.True(await StageCommit.CommitAsync(wr, id, "centers", "n1", false, default)); }
    static string Tmp() => Path.Combine(Path.GetTempPath(), "ff-" + Guid.NewGuid().ToString("N")[..8]);

    [Fact]
    public async Task The_centers_step_writes_the_frames_file_and_it_holds_exactly_the_centers_bin_bytes_after_the_magic_and_seq()
    {
        var cache = Tmp();
        try
        {
            var (fs, wr) = await Drama("a1", F16(Make(400, 15, 3)), 400, cache); await Centers(wr, "a1");
            var ff = (await fs.GetAsync(P.FramesFile("a1"), default))!.Data;
            var z = await CentersZip.OpenAsync(fs, P.Video("a1", "centers.zip"), default);
            var bin = await z!.ReadAsync(0, (int)z.DataLength, default);
            Assert.Equal(bin.Length, ff.Length);
            Assert.Equal(CentersFile.FramesMagic, ff[..4]); Assert.Equal(bin[4..24], ff[4..24]); Assert.Equal(bin[32..], ff[32..]);
            Assert.Equal(400, CentersFile.ParseHeader(ff).Frames);
            // a re-run replaces it (create, then replace under the lease)
            await Centers(wr, "a1"); Assert.Equal(ff, (await fs.GetAsync(P.FramesFile("a1"), default))!.Data);
        }
        finally { try { Directory.Delete(cache, true); } catch { } }
    }

    [Fact]
    public async Task Every_center_block_is_bit_identical_from_the_frames_file_and_from_centers_zip()
    {
        var cache = Tmp();
        try
        {
            var (fs, wr) = await Drama("a1", F16(Make(400, 15, 4)), 400, cache); await Centers(wr, "a1");
            var z = (await CentersZip.OpenAsync(fs, P.Video("a1", "centers.zip"), default))!; var f = (await FramesReader.OpenAsync(fs, P.FramesFile("a1"), default))!;
            var all = Enumerable.Range(0, CentersStep.K).ToList();
            var a = await z.ReadBlocksAsync(all, CenterSearch.MergeGap, CenterSearch.MaxMerged, default); var b = await f.ReadBlocksAsync(all, CenterSearch.MergeGap, CenterSearch.MaxMerged, default);
            Assert.Equal(a.Count, b.Count);
            foreach (var (x, y) in a.OrderBy(t => t.Center).Zip(b.OrderBy(t => t.Center))) { Assert.Equal(x.Center, y.Center); Assert.Equal(x.Members.Vectors, y.Members.Vectors); Assert.Equal(x.Members.Times, y.Members.Times); Assert.Equal(x.Members.Rows, y.Members.Rows); }
        }
        finally { try { Directory.Delete(cache, true); } catch { } }
    }

    [Fact]
    public async Task Index_build_and_search_never_open_centers_zip_and_answer_as_before()
    {
        var cache = Tmp();
        try
        {
            var xs = new[] { Make(300, 12, 11), Make(300, 12, 12), Make(300, 12, 13) }; var fs = new FakeStore(); StageZipStore? wr = null;
            for (int i = 0; i < 3; i++) { (fs, wr) = await Drama("d" + i, F16(xs[i]), 300, cache + i, fs); await Centers(wr, "d" + i); }
            // the zips are the processing archive: with them gone, nothing in the lookup path may notice
            var zipsBefore = (await fs.ListAsync(P.Prefix + "videos/", false, false, default)).Where(b => b.Name.EndsWith("/centers.zip")).Select(b => b.Name).ToList(); Assert.Equal(3, zipsBefore.Count);
            var withZips = await Query(fs, xs[1].AsSpan(7 * D, D).ToArray());
            foreach (var n in zipsBefore) await fs.DeleteAsync(n, default);
            fs.Calls.Clear();
            var without = await Query(fs, xs[1].AsSpan(7 * D, D).ToArray());
            Assert.Equal(withZips, without); Assert.StartsWith("d1@", without[0]);
            Assert.DoesNotContain(fs.Calls, c => c.Contains("centers.zip"));
        }
        finally { foreach (var d in Directory.GetDirectories(Path.GetTempPath(), "ff-*")) try { Directory.Delete(d, true); } catch { } }
    }
    static async Task<List<string>> Query(FakeStore fs, float[] q)
    {
        var ups = await SearchIndexBuild.ListCentersAsync(fs, P, default);
        var idx = await SearchIndexBuild.BuildAsync(fs, P, ups, null, null, 1, 1, 2, _ => { }, default);
        Assert.All(idx.Uploads, u => Assert.Equal(P.FramesFile(u.Id), u.File));
        var yaml = idx.UploadsYaml(); var back = SearchData.ParseUploadsYaml(yaml).Uploads; Assert.Equal(idx.Uploads.Select(u => (u.File, u.TableOff)), back.Select(u => (u.File, u.TableOff)));
        var (hits, _) = await CenterSearch.QueryAsync(fs, P, idx, q, 8, 5, 2, default);
        var (best, _) = await PreCheckSearch.BestPerFrameAsync(fs, P, idx, [q], 8, 2, default);
        return hits.Select(h => $"{h.Upload}@{h.Time}:{h.Score:R}").Concat([$"{best[0]!.Upload}:{best[0]!.Time}:{best[0]!.Cosine:R}"]).ToList();
    }

    [Fact]
    public async Task Convert_counts_on_a_dry_run_is_create_only_skips_a_claimed_drama_and_matches_what_the_centers_step_writes()
    {
        var cache = Tmp();
        try
        {
            var fs = new FakeStore(); StageZipStore? wr = null;
            for (int i = 0; i < 3; i++) { (fs, wr) = await Drama("c" + i, F16(Make(300, 12, 20 + i)), 300, cache + i, fs); await Centers(wr, "c" + i); }
            var want = new Dictionary<string, byte[]>(); foreach (var id in new[] { "c0", "c1", "c2" }) { want[id] = (await fs.GetAsync(P.FramesFile(id), default))!.Data; await fs.DeleteAsync(P.FramesFile(id), default); }
            // c1 is under a claim: a live lease on its active file
            await fs.PutAsync(P.Active("c1"), [1], null, null, false, default); Assert.NotNull(await fs.AcquireLeaseAsync(P.Active("c1"), 60, default));
            var dry = await FramesCommand.ConvertAsync(fs, P, true, 2, int.MaxValue, null, default);
            Assert.Equal((3, 0, 2, 1), (dry.Zips, dry.Have, dry.Converted, dry.Claimed)); Assert.False(fs.Exists(P.FramesFile("c0")));
            var run = await FramesCommand.ConvertAsync(fs, P, false, 2, int.MaxValue, null, default);
            Assert.Equal((2, 1), (run.Converted, run.Claimed)); Assert.Equal(["c1"], run.ClaimedIds); Assert.False(fs.Exists(P.FramesFile("c1")));
            Assert.Equal(want["c0"], (await fs.GetAsync(P.FramesFile("c0"), default))!.Data); Assert.Equal(want["c2"], (await fs.GetAsync(P.FramesFile("c2"), default))!.Data);
            // create-only: a second run leaves existing files alone and a changed one stays changed
            await fs.PutAsync(P.FramesFile("c0"), [9, 9], null, null, false, default);
            var again = await FramesCommand.ConvertAsync(fs, P, false, 2, int.MaxValue, null, default);
            Assert.Equal(0, again.Converted); Assert.Equal(2, again.Have); Assert.Equal(new byte[] { 9, 9 }, (await fs.GetAsync(P.FramesFile("c0"), default))!.Data);
        }
        finally { foreach (var d in Directory.GetDirectories(Path.GetTempPath(), "ff-*")) try { Directory.Delete(d, true); } catch { } }
    }

    [Fact]
    public async Task The_local_cache_fetches_each_frames_file_once_serves_every_read_from_disk_with_identical_answers_and_evicts_the_least_recently_used()
    {
        var cache = Tmp(); var dir = Tmp();
        try
        {
            var xs = new[] { Make(300, 12, 31), Make(300, 12, 32), Make(300, 12, 33) }; var fs = new FakeStore(); StageZipStore? wr = null;
            for (int i = 0; i < 3; i++) { (fs, wr) = await Drama("l" + i, F16(xs[i]), 300, cache + i, fs); await Centers(wr, "l" + i); }
            var q = xs[2].AsSpan(5 * D, D).ToArray();
            var remote = await Query(fs, q);   // no local cache: range requests
            var size = (await fs.GetAsync(P.FramesFile("l0"), default))!.Data.Length;
            FramesCache.Current = new FramesCache(dir, size * 10L);   // room for all three
            try
            {
                var local = await Query(fs, q); Assert.Equal(remote, local);
                await FramesCache.Current.WaitFillsAsync();
                Assert.Equal(3, FramesCache.Current.Downloads);   // one GET per file, however many reads follow
                var again = await Query(fs, q); Assert.Equal(remote, again);
                Assert.Equal(3, FramesCache.Current.Downloads);   // warm: nothing more fetched
                Assert.Empty(Directory.GetFiles(dir, "*.part"));
                // bounded: a cache with room for two evicts the least recently used
                var small = new FramesCache(Tmp(), size * 5L / 2);
                var trace = new List<string>(); foreach (var id in new[] { "l0", "l1", "l2" }) { await small.EnsureAsync(fs, P.FramesFile(id), null, size, default); trace.Add(string.Join('|', Directory.GetFiles(small.Dir).Select(Path.GetFileName))); }
                var sizes = string.Join(',', Directory.GetFiles(small.Dir, "*.bin").Select(f => new FileInfo(f).Length)); Assert.True(Directory.GetFiles(small.Dir, "*.bin").Length == 2, $"{string.Join(';', trace)} max {small.MaxBytes}"); Assert.False(File.Exists(small.PathOf(P.FramesFile("l0"), null, size)));
                Directory.Delete(small.Dir, true);
            }
            finally { FramesCache.Current = null; }
        }
        finally { foreach (var d in Directory.GetDirectories(Path.GetTempPath(), "ff-*")) try { Directory.Delete(d, true); } catch { } }
    }

    [Fact]
    public async Task A_cold_lookup_reads_by_range_with_no_whole_file_GET_then_the_background_fill_makes_the_next_lookup_all_hits()
    {
        var cache = Tmp(); var dir = Tmp();
        try
        {
            var fs = new FakeStore(); StageZipStore? wr = null; var all = new List<float[]>();
            for (int i = 0; i < 6; i++) { var x = Make(300, 12, 200 + i); all.Add(x); (fs, wr) = await Drama("k" + i, F16(x), 300, cache + i, fs); await Centers(wr, "k" + i); }
            var ups = await SearchIndexBuild.ListCentersAsync(fs, P, default); var idx = await SearchIndexBuild.BuildAsync(fs, P, ups, null, null, 1, 1, 4, _ => { }, default);
            var qs = Enumerable.Range(0, 20).Select(i => all[5].AsSpan(i * 10 * D, D).ToArray()).ToList();
            FramesCache.Current = new FramesCache(dir, 1L << 30) { PauseMs = 100 };
            try
            {
                int gets0 = fs.WholeGets;
                var (b1, st1) = await PreCheckSearch.BestPerFrameAsync(fs, P, idx, qs, 8, 4, default, -1);
                Assert.Equal(gets0, fs.WholeGets);   // the lookup returned with no whole-file GET
                Assert.True(st1.Requests > 0); Assert.True(FramesCache.Current.RangedMisses > 0); Assert.True(FramesCache.Current.FillsQueued > 0);
                await FramesCache.Current.WaitFillsAsync();
                Assert.Equal(FramesCache.Current.FillsQueued, FramesCache.Current.Downloads);
                int hits0 = FramesCache.Current.Hits, miss0 = FramesCache.Current.RangedMisses;
                var (b2, st2) = await PreCheckSearch.BestPerFrameAsync(fs, P, idx, qs, 8, 4, default, -1);
                Assert.Equal(0, st2.Requests); Assert.Equal(miss0, FramesCache.Current.RangedMisses); Assert.True(FramesCache.Current.Hits > hits0);
                Assert.Equal(b1.Length, b2.Length);
            }
            finally { FramesCache.Current = null; }
        }
        finally { foreach (var d in Directory.GetDirectories(Path.GetTempPath(), "ff-*")) try { Directory.Delete(d, true); } catch { } }
    }

    [Fact]
    public async Task A_cold_lookup_across_300_uncached_uploads_takes_under_5_seconds()
    {
        var cache = Tmp(); var dir = Tmp();
        try
        {
            var fs = new FakeStore(); StageZipStore? wr = null; var all = new List<float[]>();
            for (int i = 0; i < 300; i++) { var x = Make(30, 4, 500 + i); all.Add(x); (fs, wr) = await Drama("m" + i, F16(x), 30, cache + i, fs); await Centers(wr, "m" + i); }
            var ups = await SearchIndexBuild.ListCentersAsync(fs, P, default); var idx = await SearchIndexBuild.BuildAsync(fs, P, ups, null, null, 1, 1, 4, _ => { }, default);
            var qs = Enumerable.Range(0, 60).Select(i => all[i * 5].AsSpan((i % 30) * D, D).ToArray()).ToList();
            fs.RangeLatency = TimeSpan.FromMilliseconds(2);
            FramesCache.Current = new FramesCache(dir, 1L << 30);
            try
            {
                int g0 = fs.WholeGets; var sw = System.Diagnostics.Stopwatch.StartNew();
                await PreCheckSearch.BestPerFrameAsync(fs, P, idx, qs, 64, 4, default, -1);
                Assert.Equal(g0, fs.WholeGets);
                var t = sw.Elapsed.TotalSeconds; Console.WriteLine($"BENCH cold lookup, 300 uncached uploads: {t:F2}s, {FramesCache.Current.RangedMisses} ranged misses, {FramesCache.Current.FillsQueued} fills queued");
                Assert.True(t < 5, $"{t:F2}s");
                Assert.True(FramesCache.Current.RangedMisses >= 100);
            }
            finally { FramesCache.Current = null; }
        }
        finally { foreach (var d in Directory.GetDirectories(Path.GetTempPath(), "ff-*")) try { Directory.Delete(d, true); } catch { } }
    }

    [Fact]
    public async Task Benchmark_cold_download_and_warm_lookups_of_a_drama_group_check()
    {
        var cache = Tmp(); var dir = Tmp();
        try
        {
            var fs = new FakeStore(); StageZipStore? wr = null; var all = new List<float[]>();
            for (int i = 0; i < 12; i++) { var x = Make(1500, 40, 100 + i); all.Add(x); (fs, wr) = await Drama("b" + i, F16(x), 1500, cache + i, fs); await Centers(wr, "b" + i); }
            var ups = await SearchIndexBuild.ListCentersAsync(fs, P, default); var idx = await SearchIndexBuild.BuildAsync(fs, P, ups, null, null, 1, 1, 4, _ => { }, default);
            var qs = Enumerable.Range(0, 60).Select(i => all[11].AsSpan(i * 25 * D, D).ToArray()).ToList();   // 60 sampled frames of the last drama
            FramesCache.Current = new FramesCache(dir, 1L << 30);
            try
            {
                var sw = System.Diagnostics.Stopwatch.StartNew(); await PreCheckSearch.BestPerFrameAsync(fs, P, idx, qs, 8, 4, default, 11); var cold = sw.Elapsed.TotalSeconds; await FramesCache.Current.WaitFillsAsync();
                sw.Restart(); var (best, st) = await PreCheckSearch.BestPerFrameAsync(fs, P, idx, qs, 8, 4, default, 11); var warm = sw.Elapsed.TotalSeconds;
                Console.WriteLine($"BENCH frames-cache: 12 dramas x 1500 frames, 60-frame group check: cold {cold:F2}s ({FramesCache.Current.Downloads} GETs, {FramesCache.Current.DownloadedBytes / 1024} KB), warm {warm:F3}s, {st.Requests} range requests");
                Assert.Equal(0, st.Requests); Assert.True(warm < 10);
            }
            finally { FramesCache.Current = null; }
        }
        finally { foreach (var d in Directory.GetDirectories(Path.GetTempPath(), "ff-*")) try { Directory.Delete(d, true); } catch { } }
    }

    /// <summary>The guard: no lookup or index-build source opens centers.zip. Only the zip class itself, the centers step's own staleness check, the converter and the old backfill listing may name it.</summary>
    [Fact]
    public void No_lookup_or_index_build_code_opens_centers_zip()
    {
        var root = AppContext.BaseDirectory; while (root != null && !Directory.Exists(Path.Combine(root, "IndexNode", "Centers"))) root = Path.GetDirectoryName(root);
        Assert.NotNull(root);
        var allowed = new[] { "CentersFile.cs", "FramesCommand.cs", "CentersStep.cs", "CentersCommands.cs", "VideosBackfill.cs" };
        var bad = Directory.GetFiles(Path.Combine(root!, "IndexNode"), "*.cs", SearchOption.AllDirectories).Where(f => !f.Contains("/obj/") && !allowed.Contains(Path.GetFileName(f)))
            .Where(f => { var t = File.ReadAllText(f); return System.Text.RegularExpressions.Regex.IsMatch(t, @"CentersZip\.(OpenAsync|FromIndex)|Video\([^)]*\""centers\.zip\"""); }).Select(Path.GetFileName).ToList();
        Assert.Empty(bad);
        foreach (var f in new[] { "SearchIndex.cs", "PreCheckSearch.cs", "GroupBuild.cs", "Catalog.cs" })
            Assert.DoesNotContain("CentersZip", File.ReadAllText(Path.Combine(root!, "IndexNode", "Centers", f)).Replace("CentersZip/", "").Replace("0.11.0 SearchData/CentersZip", ""));
    }
}
