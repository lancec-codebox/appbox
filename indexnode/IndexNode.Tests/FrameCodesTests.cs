using System.Buffers.Binary;
using Xunit;

namespace IndexNode.Tests;

/// <summary>0.13.69: the frame codes log (PROTOCOL "The search index carries a code of every frame, not of the centers").</summary>
public class FrameCodesTests
{
    static readonly Paths P = new("index/");
    const int D = CentersFile.Dim;

    static float[] Rows(int n, int seed) { var rnd = new Random(seed); var x = new float[n * D]; for (int i = 0; i < x.Length; i++) x[i] = (float)rnd.NextDouble() - 0.5f; for (int i = 0; i < n; i++) Vec.Normalize(x.AsSpan(i * D, D)); return x; }
    static byte[] F16(float[] x) { var b = new byte[x.Length * 2]; for (int i = 0; i < x.Length; i++) BitConverter.TryWriteBytes(b.AsSpan(i * 2), (Half)x[i]); return b; }
    static byte[] Bin(string id, float[] x, int n) => CentersStep.Build(id, F16(x), Enumerable.Range(0, n).Select(i => i * 2.0).ToList(), 2).Bin;
    static string Tmp() => Path.Combine(Path.GetTempPath(), "fc-" + Guid.NewGuid().ToString("N")[..8]);

    /// <summary>n uploads of 60 frames each, their frames files in the store, and a built (not filled) index version.</summary>
    static async Task<(FakeStore F, SearchData Idx, Dictionary<string, float[]> X)> Seed(int n)
    {
        var f = new FakeStore(); var xs = new Dictionary<string, float[]>();
        for (int i = 0; i < n; i++) { var id = "u" + i; var x = Rows(60, 10 + i); xs[id] = x; await FramesFile.WriteAsync(f, P, id, Bin(id, x, 60), 0, false, default); }
        var ups = await SearchIndexBuild.ListCentersAsync(f, P, default);
        var idx = await SearchIndexBuild.BuildAsync(f, P, ups, null, null, 1, 1, 2, _ => { }, default);
        return (f, idx, xs);
    }

    [Fact]
    public void Auto_is_frames_only_when_every_upload_has_codes()
    {
        Assert.Equal("frames", PreCheckSettings.ResolveRoute("auto", 0)); Assert.Equal("centers", PreCheckSettings.ResolveRoute("auto", 3));
        Assert.Equal("centers", PreCheckSettings.ResolveRoute("auto", -1));   // a version from before the codes
        Assert.Equal("frames", PreCheckSettings.ResolveRoute(null, 0)); Assert.Equal("centers", PreCheckSettings.ResolveRoute("Centers", 0)); Assert.Equal("frames", PreCheckSettings.ResolveRoute("frames", 5));
        Assert.Equal("auto", new PreCheckSettings().SearchRoute);
    }

    [Fact]
    public void A_part_round_trips_and_ends_with_a_newline()
    {
        var p = new FrameCodes.Part("abc", "index/search/frames/abc.bin", "beef", 1, 0, 3, [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 22, 23, 24], [2144, 3168, 4192], [0.5f, 1.5f, 2.5f]);
        var b = FrameCodes.Encode(p);
        Assert.Equal((byte)'\n', b[^1]); Assert.Equal(b.Length, FrameCodes.LengthAt(b));
        var q = FrameCodes.Decode(b);
        Assert.Equal(p.Id, q.Id); Assert.Equal(p.Blob, q.Blob); Assert.Equal(p.Sha, q.Sha); Assert.Equal(p.Codes, q.Codes); Assert.Equal(p.Offsets, q.Offsets); Assert.Equal(p.Times, q.Times); Assert.Equal(3, q.Total);
        b[5] ^= 1; Assert.Throws<InvalidDataException>(() => FrameCodes.Decode(b));
        var pp = FrameCodes.ParsePointer("2:7,99")!.Value; Assert.Equal(2, pp.Segment); Assert.Equal([7L, 99L], pp.Offsets);
        Assert.Null(FrameCodes.ParsePointer(null)); Assert.Null(FrameCodes.ParsePointer("x"));
    }

    [Fact]
    public void The_codes_of_a_frames_file_are_the_index_codes_of_each_stored_vector_with_its_offset_and_time()
    {
        var x = Rows(50, 3); var bin = Bin("v", x, 50); var mean = Rows(1, 4); for (int d = 0; d < D; d++) mean[d] *= 0.1f;
        var parts = FrameCodes.FromFile("v", "b", "s", 1, mean, bin);
        var p = Assert.Single(parts); Assert.Equal(50, p.Count); Assert.Equal(50, p.Total);
        var seen = new HashSet<float>(); var v = new float[D]; var want = new byte[64];
        for (int i = 0; i < p.Count; i++)
        {
            Vec.FromHalf(bin.AsSpan((int)p.Offsets[i], D * 2), v);   // the offset points at the stored vector
            SearchData.Code(v, mean, want);
            var got = new byte[64]; for (int w = 0; w < 8; w++) BinaryPrimitives.WriteUInt64LittleEndian(got.AsSpan(w * 8), p.Codes[i * 8 + w]);
            Assert.Equal(want, got);   // the same bits as the centers' bits.bin
            int row = Array.FindIndex(Enumerable.Range(0, 50).ToArray(), r => Math.Abs(Vec.Dot(v, x.AsSpan(r * D, D)) - 1) < 1e-2);
            Assert.Equal(row * 2.0f, p.Times[i]); seen.Add(p.Times[i]);
        }
        Assert.Equal(50, seen.Count);
    }

    [Fact]
    public async Task Fill_adds_at_most_max_per_build_the_next_build_carries_the_pointers_and_the_view_finds_a_stored_frame_exactly()
    {
        var (f, idx, xs) = await Seed(3);
        Assert.Equal(3, idx.CodesMissing);
        Assert.Equal(2, await FrameCodes.FillAsync(f, P, idx, 2, 0, _ => { }, default));
        Assert.Equal(1, idx.CodesMissing); Assert.Equal(2, idx.Uploads.Count(u => u.Codes != null));
        await idx.PublishAsync(f, P, default);
        var head = (await SearchData.LoadAsync(f, P, null, default))!;
        Assert.Equal(1, head.CodesMissing); Assert.Equal(idx.Uploads.Select(u => u.Codes), head.Uploads.Select(u => u.Codes));   // uploads.yaml keeps them

        // the next build reuses the head's rows (same sha): the pointers come along, only the last upload is filled
        var ups = await SearchIndexBuild.ListCentersAsync(f, P, default);
        var next = await SearchIndexBuild.BuildAsync(f, P, ups, head.Mean, head, 2, head.MeanVersion, 2, _ => { }, default);
        Assert.Equal(head.Uploads.Where(u => u.Codes != null).Select(u => u.Codes), next.Uploads.Where(u => u.Codes != null).Select(u => u.Codes));
        int appends = f.Ops.GetValueOrDefault("append");
        Assert.Equal(1, await FrameCodes.FillAsync(f, P, next, 60, 0, _ => { }, default));
        Assert.Equal(0, next.CodesMissing); Assert.Equal(1, f.Ops.GetValueOrDefault("append") - appends);
        Assert.False(SearchData.Unchanged(head, next, new GroupSet(), new GroupSet()));   // a filled upload is a change: the build publishes

        var dir = Tmp();
        try
        {
            var cache = new FrameCodesCache(dir);
            var view = await cache.ViewAsync(f, P, next, default);
            Assert.Equal(0, view.Missing); Assert.Equal(180, view.Frames);
            int reads = cache.Reads; Assert.Same(view, await cache.ViewAsync(f, P, next, default)); Assert.Equal(reads, cache.Reads);   // the same version: no read
            // a stored frame is its own nearest (distance 0), and the search reads it exactly
            var q = xs["u1"].AsSpan(17 * D, D).ToArray();
            var near = view.Nearest(FrameCodes.Code(q, next.Mean), 5, 2);
            Assert.Equal(0, near[0].Dist); Assert.Equal("u1", next.Uploads[near[0].Upload].Id);
            var (hits, tm) = await FrameCodeSearch.QueryAsync(f, P, view, q, 10, 3, 2, default);
            Assert.Equal("u1", hits[0].Upload); Assert.Equal(34.0, hits[0].Time, 3); Assert.True(hits[0].Score > 0.999f);
            Assert.True(tm.Requests >= 1);

            // a reader with a copy fetches only the new bytes
            var cache2 = new FrameCodesCache(dir); await cache2.ViewAsync(f, P, next, default); Assert.Equal(0, cache2.Reads);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public async Task The_per_frame_lookup_on_codes_finds_each_query_frame_in_its_own_upload_and_leaves_the_excluded_one_out()
    {
        var (f, idx, xs) = await Seed(3);
        await FrameCodes.FillAsync(f, P, idx, 60, 0, _ => { }, default);
        var dir = Tmp();
        try
        {
            var view = await new FrameCodesCache(dir).ViewAsync(f, P, idx, default);
            var qs = new[] { ("u0", 3), ("u2", 40), ("u1", 59) }.Select(x => xs[x.Item1].AsSpan(x.Item2 * D, D).ToArray()).ToList();
            var (best, st) = await PreCheckSearch.BestPerFrameCodesAsync(f, P, view, qs, 5, 2, default);
            Assert.Equal(["u0", "u2", "u1"], best.Select(b => idx.Uploads[b!.Upload].Id).ToArray());
            Assert.Equal([6.0, 80.0, 118.0], best.Select(b => Math.Round(b!.Time, 3)).ToArray());
            Assert.All(best, b => Assert.True(b!.Cosine > 0.999f));
            int u2 = idx.Uploads.FindIndex(u => u.Id == "u2");
            var (ex, _) = await PreCheckSearch.BestPerFrameCodesAsync(f, P, view, qs, 5, 2, default, u2);
            Assert.NotEqual("u2", idx.Uploads[ex[1]!.Upload].Id); Assert.True(ex[1]!.Cosine < 0.9f);
            // the batched scan is the single scan, query by query
            var codes = qs.Select(q => FrameCodes.Code(q, idx.Mean)).ToList(); var many = view.NearestMany(codes, 7, 3);
            for (int i = 0; i < qs.Count; i++) Assert.Equal(view.Nearest(codes[i], 7, 2), many[i]);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public async Task Another_writer_appending_in_between_moves_the_entry_but_never_tears_it()
    {
        var (f, idx, _) = await Seed(2);
        var w = new FrameCodes.Writer(f, P, idx.MeanVersion);
        Assert.Equal(1, await FrameCodes.FillAsync(f, P, idx, 1, 0, _ => { }, default, w));
        // someone else appends a whole part before our next append: our position is stale, the entry is written again at the new end
        var other = FrameCodes.Encode(new FrameCodes.Part("zz", "b", "s", 1, 0, 0, [], [], []));
        f.BeforeAppend = name => { var info = f.GetAppendInfoAsync(name, default).Result!; f.AppendBlockAsync(name, other, info.Length, default).Wait(); };
        Assert.Equal(1, await FrameCodes.FillAsync(f, P, idx, 1, 0, _ => { }, default, w));
        var dir = Tmp();
        try
        {
            var view = await new FrameCodesCache(dir).ViewAsync(f, P, idx, default);
            Assert.Equal(0, view.Missing); Assert.Equal(120, view.Frames);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public async Task The_check_lookup_goes_to_the_codes_only_when_every_upload_has_them()
    {
        var (f, idx, xs) = await Seed(2);
        var q = new List<float[]> { xs["u1"].AsSpan(5 * D, D).ToArray() };
        var s = new PreCheckSettings(); Assert.Equal("auto", s.CheckRoute); var dir = Tmp(); var cache = new FrameCodesCache(dir);
        var (b0, st0) = await PreCheckSearch.LookupAsync(f, P, idx, q, s, 2, default, codes: cache);   // no codes yet: the centers (they vote)
        Assert.Equal("u1", idx.Uploads[b0[0]!.Upload].Id); Assert.True(st0.VotedUploads >= 1);
        await FrameCodes.FillAsync(f, P, idx, 60, 0, _ => { }, default); Assert.Equal(0, idx.CodesMissing);
        var (b1, st1) = await PreCheckSearch.LookupAsync(f, P, idx, q, s, 2, default, codes: cache);   // now the codes: one vector per nearest frame, no votes
        Assert.Equal("u1", idx.Uploads[b1[0]!.Upload].Id); Assert.Equal(10.0, b1[0]!.Time, 3); Assert.Equal(0, st1.WinnerVoteRank);
        var (b2, _) = await PreCheckSearch.LookupAsync(f, P, idx, q, new PreCheckSettings { CheckRoute = "centers" }, 2, default, codes: cache);
        Assert.Equal("u1", idx.Uploads[b2[0]!.Upload].Id);
        // a local copy that is not this store's log: the upload counts as missing once, the copy goes, the next view is whole again
        var path = Path.Combine(dir, "m1", "log-0000.bin"); using (var jf = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete)) jf.Write(Enumerable.Repeat((byte)7, 64).ToArray());   // in place: the file is mapped
        var other = new FrameCodesCache(dir); var v1 = await other.ViewAsync(f, P, idx, default); Assert.Equal(idx.Uploads.Count, v1.Missing);   // the next generation's file is used (a mapped file cannot always be deleted on Windows)
        idx.Uploads[0] = idx.Uploads[0] with { }; var idx2 = new SearchData { Version = idx.Version, MeanVersion = idx.MeanVersion, Mean = idx.Mean, Bits = idx.Bits, RefUpload = idx.RefUpload, RefCenter = idx.RefCenter, Uploads = idx.Uploads, CodesMissing = 0 };
        var v2 = await other.ViewAsync(f, P, idx2, default); Assert.Equal(0, v2.Missing); Assert.Equal(120, v2.Frames);
        Assert.True(File.Exists(Path.Combine(dir, "m1", "log-0000.g1.bin")));
        // a new cache on the same folder takes the newest generation and needs no read
        var third = new FrameCodesCache(dir); var v3 = await third.ViewAsync(f, P, idx2, default); Assert.Equal(0, v3.Missing); Assert.Equal(0, third.Reads);
        try { Directory.Delete(dir, true); } catch { }
    }

    [Fact]
    public async Task A_new_version_appends_to_the_mapped_local_copy_and_the_old_view_still_reads()
    {
        var (f, idx, xs) = await Seed(2);
        await FrameCodes.FillAsync(f, P, idx, 1, 0, _ => { }, default);
        var dir = Tmp();
        try
        {
            var cache = new FrameCodesCache(dir);
            var v1 = await cache.ViewAsync(f, P, idx, default); Assert.Equal(1, v1.Missing);
            var idx2 = new SearchData { Version = 2, MeanVersion = idx.MeanVersion, Mean = idx.Mean, Bits = idx.Bits, RefUpload = idx.RefUpload, RefCenter = idx.RefCenter, Uploads = idx.Uploads.ToList() };
            await FrameCodes.FillAsync(f, P, idx2, 60, 0, _ => { }, default);   // the second upload's entry: appended to the store, then to the mapped copy
            var v2 = await cache.ViewAsync(f, P, idx2, default); Assert.Equal(0, v2.Missing); Assert.Equal(120, v2.Frames);
            // the old view's map still reads (its upload, its time)
            var u0 = v1.Uploads.First(u => u != null)!; Assert.Equal(60, u0.Count); Assert.Equal(0f, Enumerable.Range(0, 60).Select(r => u0.Time(r)).Min());
            var q = xs["u1"].AsSpan(9 * D, D).ToArray(); var near = v2.Nearest(FrameCodes.Code(q, idx.Mean), 3, 2);
            Assert.Equal("u1", idx2.Uploads[near[0].Upload].Id); Assert.Equal(0, near[0].Dist); Assert.Equal(18f, v2.Uploads[near[0].Upload]!.Time(near[0].Row));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public async Task Small_parallel_ranges_that_cut_parts_still_give_the_whole_log()
    {
        var (f, idx, _) = await Seed(5);
        await FrameCodes.FillAsync(f, P, idx, 60, 0, _ => { }, default);
        var dir = Tmp();
        try
        {
            var small = new FrameCodesCache(dir) { RangeBytes = 1000, Streams = 3 };   // a part is ~4.6 KB: every part is cut across ranges and windows
            var view = await small.ViewAsync(f, P, idx, default);
            Assert.Equal(0, view.Missing); Assert.Equal(300, view.Frames);
            Assert.True(small.Reads > 20);
            var len = (await f.GetAppendInfoAsync(FrameCodes.Segment(P, idx.MeanVersion, 0), default))!.Length;
            Assert.Equal(len, new FileInfo(Path.Combine(dir, "m1", "log-0000.bin")).Length);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public async Task A_pointer_to_another_uploads_entry_counts_as_missing()
    {
        var (f, idx, _) = await Seed(2);
        await FrameCodes.FillAsync(f, P, idx, 60, 0, _ => { }, default);
        idx.Uploads[1] = idx.Uploads[1] with { Codes = idx.Uploads[0].Codes };   // wrong entry
        var dir = Tmp();
        try { var view = await new FrameCodesCache(dir).ViewAsync(f, P, idx, default); Assert.Equal(1, view.Missing); Assert.Null(view.Uploads[1]); }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
}
