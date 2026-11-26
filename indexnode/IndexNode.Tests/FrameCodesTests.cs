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
