using System.Text;
using Xunit;

namespace IndexNode.Tests;

/// <summary>0.13.6: the groups full check samples frames (groups.check-frames), the block cache is capped, the index job does not bootstrap groups, and caps its new uploads per build.</summary>
[Collection("console")]
public class GroupSampleTests
{
    static readonly Paths P = new("index/");
    const int D = CentersFile.Dim;
    const int N = 300;

    static float[] Gauss(Random r) { var v = new float[D]; for (int j = 0; j < D; j++) v[j] = (float)(Math.Sqrt(-2 * Math.Log(1 - r.NextDouble())) * Math.Cos(2 * Math.PI * r.NextDouble())); Vec.Normalize(v); return v; }
    /// <summary>n frames, each its own random direction (so a copy matches frame for frame and nothing else matches).</summary>
    static float[] Frames(int n, int seed) { var rng = new Random(seed); var x = new float[n * D]; for (int i = 0; i < n; i++) Gauss(rng).CopyTo(x, i * D); return x; }
    static byte[] F16(float[] x) { var b = new byte[x.Length * 2]; for (int i = 0; i < x.Length; i++) BitConverter.TryWriteBytes(b.AsSpan(i * 2, 2), (Half)x[i]); return b; }

    static async Task<FakeStore> Fixture(string cache, params (string Id, float[] X)[] ups)
    {
        FakeStore? fs = null;
        foreach (var (id, x) in ups)
        {
            fs ??= new FakeStore(); var wr = (StageZipStore)StageZipStore.Wrap(fs, cache + id); int n = x.Length / D;
            var frames = Enumerable.Range(0, n).Select(i => new ChunkFrame(i, i * 1.0, i * 1.0, [], null, i, 1)).ToList();
            await wr.PutAsync(P.Video(id, "frames/0000.json"), FrameChunks.JpegIndexJson("0000", 0, 360, 640, 85, frames), null, null, false, default);
            await wr.PutAsync(P.Video(id, "frames/manifest.json"), FrameChunks.ManifestJson("frames", 360, 640, 0, n, [("0000", 0, n, (long)n)], null, "mjpeg", "jpeg"), null, null, false, default);
            await wr.PutAsync(P.Video(id, "sscd/0000.f16"), F16(x), null, null, false, default);
            await wr.PutAsync(P.Video(id, "sscd/manifest.json"), Encoding.UTF8.GetBytes($"{{\"v\":1,\"step\":\"sscd\",\"chunks\":[{{\"chunk\":\"0000\",\"start\":0,\"rows\":{n}}}]}}"), null, null, false, default);
            foreach (var st in new[] { "frames", "sscd" }) Assert.True(await StageZipStore.Stage(wr)!.CommitStageAsync(id, st, "n1", false, false, default));
            await VideoJsonSeed.PutAsync(fs, P, id, 300);
            await CentersStep.RunAsync(wr, P, id, 2, null, default); Assert.True(await StageCommit.CommitAsync(wr, id, "centers", "n1", false, default));
        }
        return fs!;
    }

    static async Task<SearchData> Index(FakeStore fs, int version, params string[] ids)
    {
        var ups = (await SearchIndexBuild.ListCentersAsync(fs, P, default)).Where(u => ids.Contains(u.Id)).ToList();
        var idx = await SearchIndexBuild.BuildAsync(fs, P, ups, null, null, version, 1, 2, _ => { }, default);
        idx.BuiltAt = DateTimeOffset.UtcNow; return idx;
    }

    static string Tmp() => Path.Combine(Path.GetTempPath(), "gs-" + Guid.NewGuid().ToString("N")[..8]);

    [Fact]
    public void Sample_is_evenly_spaced_in_time_order_and_keeps_all_when_short_or_zero()
    {
        var f = Enumerable.Range(0, 1000).Select(i => ((float)i, new float[1])).ToList();
        var s = GroupBuild.Sample(f, 240);
        Assert.Equal(240, s.Count); Assert.True(s.Zip(s.Skip(1)).All(p => p.First.T < p.Second.T));
        Assert.All(s.Zip(s.Skip(1)), p => Assert.InRange(p.Second.T - p.First.T, 4, 5));
        Assert.Equal(1000, GroupBuild.Sample(f, 0).Count); Assert.Equal(1000, GroupBuild.Sample(f, 1000).Count); Assert.Equal(1000, GroupBuild.Sample(f, 5000).Count);
    }

    [Fact]
    public async Task A_sampled_check_still_joins_a_duplicate_and_flags_a_mixed_cut()
    {
        var cache = Tmp();
        try
        {
            var a = Frames(N, 1); var dup = (float[])a.Clone();
            var mixed = Frames(N, 2); Array.Copy(a, 0, mixed, 0, 120 * D);   // the first 120 frames (40%) are a's, the rest is other material
            var other = Frames(N, 3);
            var fs = await Fixture(cache, ("a1", a), ("dup", dup), ("mix", mixed), ("oth", other));
            var idx = await Index(fs, 1, "a1");
            var all = await SearchIndexBuild.ListCentersAsync(fs, P, default);
            SearchUpload U(string id) { var z = all.First(x => x.Id == id); return new SearchUpload(id, "", "", 1, 0, 1, N, 1, 0); }
            var s = new PreCheckSettings { GroupCheckFrames = 100 };
            var set = new GroupSet(); var cap = new BlockCache(64L << 20);
            var d = (await GroupBuild.FullCheckAsync(fs, P, idx, U("dup"), set, s, 2, default, cap))!;
            Assert.Equal("join", d.Kind); Assert.Equal("a1", d.JoinUpload); Assert.False(d.Mixed); Assert.InRange(d.Hits, 90, 100); Assert.InRange(d.Share, 0.9, 1.0);
            var m = (await GroupBuild.FullCheckAsync(fs, P, idx, U("mix"), set, s, 2, default, cap))!;
            Assert.True(m.Mixed, m.Kind + " share " + m.Share + " hits " + m.Hits); Assert.Equal("mixed", m.Kind); Assert.InRange(m.Share, 0.3, 0.5);
            var o = (await GroupBuild.FullCheckAsync(fs, P, idx, U("oth"), set, s, 2, default, cap))!;
            Assert.Equal("new", o.Kind);
        }
        finally { foreach (var id in new[] { "a1", "dup", "mix", "oth" }) try { Directory.Delete(cache + id, true); } catch { } }
    }

    [Fact]
    public void The_block_cache_holds_its_byte_cap_and_drops_the_least_recently_used()
    {
        (float[] Vec, float[] Times) Blk() => (new float[1000], new float[10]);   // (1000 + 10) * 4 + 64 bytes
        long one = (1000 + 10) * 4L + 64; var c = new BlockCache(one * 3 + 10);
        for (int i = 0; i < 3; i++) c.Put("u", i, Blk());
        Assert.True(c.TryGet("u", 0, out _));   // 0 is now the most recently used
        c.Put("u", 3, Blk());                  // over the cap: 1 (the least recently used) goes
        var st = c.Stats();
        Assert.True(st.Bytes <= st.MaxBytes, st.Bytes + " > " + st.MaxBytes); Assert.Equal(3, st.Entries); Assert.Equal(one * 3, st.Bytes);
        Assert.False(c.TryGet("u", 1, out _)); Assert.True(c.TryGet("u", 0, out _)); Assert.True(c.TryGet("u", 3, out _));
        for (int i = 10; i < 200; i++) c.Put("v", i, Blk());
        Assert.True(c.Stats().Bytes <= c.Stats().MaxBytes);
    }

    [Fact]
    public async Task The_index_job_with_no_groups_file_publishes_and_does_not_group_and_with_one_it_checks_only_new_uploads_capped()
    {
        var cache = Tmp(); var ids = new[] { "a1", "n1", "n2", "n3" };
        try
        {
            var a = Frames(N, 1);
            var fs = await Fixture(cache, ("a1", a), ("n1", (float[])a.Clone()), ("n2", Frames(N, 5)), ("n3", Frames(N, 6)));
            var say = new List<string>();
            // no groups file: no inline bootstrap, nothing written, no upload stamped
            var v1 = await Index(fs, 1, "a1", "n1", "n2", "n3");
            var none = await GroupBuild.ApplyAsync(fs, P, null, v1, default, say.Add, 2, new PreCheckSettings { GroupCheckFrames = 50 }, requireGroups: true);
            Assert.Empty(none.Groups); Assert.All(v1.Uploads, u => Assert.Null(u.Group));
            Assert.Contains(say, m => m.Contains("indexnode groups build")); Assert.Empty(fs.Names("index/groups/").ToList());
            // a groups file (v1: a1 only), then v2 has 3 new uploads and the cap is 2
            var v1a = await Index(fs, 1, "a1"); await GroupBuild.ApplyAsync(fs, P, null, v1a, default, null, 2, new PreCheckSettings { GroupCheckFrames = 50 });
            Assert.NotNull(await fs.GetAsync("index/groups/v1.json", default));
            var v2 = await Index(fs, 2, ids); say.Clear();
            var set = await GroupBuild.ApplyAsync(fs, P, v1a, v2, default, say.Add, 2, new PreCheckSettings { GroupCheckFrames = 50, GroupMaxNewPerBuild = 2 }, requireGroups: true);
            Assert.Contains(say, m => m.Contains("checking 2 now"));
            Assert.Equal(3, v2.Uploads.Count(u => u.Group != null));   // a1 (known) + 2 of the 3 new ones; the third waits for the next build
            Assert.Contains(say, m => m.Contains("2 new uploads placed")); Assert.Contains(say, m => m.StartsWith("groups: checked 2 of 2"));
        }
        finally { foreach (var id in ids) try { Directory.Delete(cache + id, true); } catch { } }
    }

    [Fact]
    public async Task Group_joins_and_mixed_cuts_are_identical_in_scan_mode_exact_and_fast()
    {
        var cache = Tmp(); bool was = SearchData.ScanFast;
        try
        {
            var a = Frames(N, 1); var mixed = Frames(N, 2); Array.Copy(a, 0, mixed, 0, 120 * D);
            var fs = await Fixture(cache, ("a1", a), ("b9", Frames(N, 7)), ("dup", (float[])a.Clone()), ("mix", mixed), ("oth", Frames(N, 3)));
            var idx = await Index(fs, 1, "a1", "b9");
            var s = new PreCheckSettings { GroupCheckFrames = 100 };
            async Task<List<string>> Run(bool fast)
            {
                SearchData.Configure(fast ? "fast" : "exact", 2000); var o = new List<string>();
                foreach (var id in new[] { "dup", "mix", "oth" })
                { var g = (await GroupBuild.FullCheckAsync(fs, P, idx, new SearchUpload(id, "", "", 1, 0, 1, N, 1, 0), new GroupSet(), s, 2, default, new BlockCache(64L << 20)))!; o.Add($"{id}:{g.Kind}:{g.JoinUpload}:{g.Hits}:{g.Mixed}:{g.Share:F3}:{g.Offset:F1}"); }
                return o;
            }
            var exact = await Run(false); var fast = await Run(true);
            Assert.Equal(exact, fast); Assert.StartsWith("dup:join:a1:", exact[0]); Assert.StartsWith("mix:mixed:", exact[1]); Assert.StartsWith("oth:new:", exact[2]);
        }
        finally { SearchData.Configure(was ? "fast" : "exact", 2000); foreach (var id in new[] { "a1", "b9", "dup", "mix", "oth" }) try { Directory.Delete(cache + id, true); } catch { } }
    }

    static SearchData Table(int n, int seed, out float[][] rows, out float[] mean)
    {
        var rng = new Random(seed); int nd = Math.Max(1, n / 20); var dirs = Enumerable.Range(0, nd).Select(_ => Gauss(rng)).ToArray();
        rows = new float[n][]; mean = new float[D]; var rr = rows;
        for (int i = 0; i < n; i++) { var d = dirs[rng.Next(nd)]; var g = Gauss(rng); var v = new float[D]; for (int j = 0; j < D; j++) v[j] = d[j] + 0.5f * g[j]; Vec.Normalize(v); rows[i] = v; }
        foreach (var r in rows) for (int j = 0; j < D; j++) mean[j] += r[j] / n;
        var bits = new byte[(long)n * SearchData.CodeBytes]; for (int i = 0; i < n; i++) SearchData.Code(rows[i], mean, bits.AsSpan(i * SearchData.CodeBytes, SearchData.CodeBytes));
        return new SearchData { Mean = mean, Bits = bits, RefUpload = Enumerable.Range(0, n).Select(i => i % 3).ToArray(), RefCenter = Enumerable.Range(0, n).ToArray(), Uploads = [] };
    }

    [Fact]
    public void The_fast_scan_is_the_exact_scan_when_the_shortlist_covers_the_table_and_keeps_the_best_hit_otherwise()
    {
        var idx = Table(9000, 11, out var rows, out var mean); var rng = new Random(3);
        var qs = Enumerable.Range(0, 20).Select(_ => { var b = rows[rng.Next(rows.Length)]; var g = Gauss(rng); var v = new float[D]; for (int j = 0; j < D; j++) v[j] = b[j] + 0.3f * g[j] - mean[j]; return v; }).ToList();
        foreach (var ex in new[] { -1, 1 })
        {
            // shortlist >= table: stage 1 keeps everything, so the result is the exact scan's, order included (and the excluded upload never appears)
            var all = idx.ScanBatch(qs, 100, 3, ex, 100000);
            for (int i = 0; i < qs.Count; i++) { Assert.Equal(idx.ScanExact(qs[i], 100, 1, ex), all[i]); if (ex >= 0) Assert.All(all[i], r => Assert.NotEqual(ex, idx.RefUpload[r])); }
            // a real shortlist: one pass or many threads give the same rows, the best exact row stays, and most of the exact top C is found
            var one = idx.ScanBatch(qs, 100, 1, ex, 2000); var many = idx.ScanBatch(qs, 100, 4, ex, 2000); double ov = 0;
            for (int i = 0; i < qs.Count; i++)
            {
                Assert.Equal(one[i], many[i]); var e = idx.ScanExact(qs[i], 100, 1, ex);
                Assert.Equal(e[0], one[i][0]); ov += e.Intersect(one[i]).Count() / 100.0;
                if (ex >= 0) Assert.All(one[i], r => Assert.NotEqual(ex, idx.RefUpload[r]));
            }
            Assert.True(ov / qs.Count >= 0.85, "overlap " + ov / qs.Count);
            Assert.Equal(idx.ScanBatch([qs[0]], 100, 1, ex, 2000)[0], one[0]);   // a batch of one = the query alone
        }
        Assert.Equal(20, idx.ScanBatch(qs, 20, 2, -1, 3)[0].Length);   // the shortlist is at least 20 x topC
    }

    [Fact]
    public void Progress_reads_checked_n_of_m_with_rate_and_eta()
    {
        Assert.Equal("groups: checked 10 of 50 (5.0/min, ETA 00:08)", GroupBuild.Progress(10, 50, TimeSpan.FromMinutes(2)));
    }
}
