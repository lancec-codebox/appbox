using System.Text;
using System.Text.Json.Nodes;
using Xunit;

namespace IndexNode.Tests;

public class StageZipTests
{
    static readonly Paths P = new("index/");
    static string Tmp() { var d = Path.Combine(Path.GetTempPath(), "zt-" + Guid.NewGuid().ToString("N")[..8]); Directory.CreateDirectory(d); return d; }
    static byte[] B(string s) => Encoding.UTF8.GetBytes(s);

    [Fact]
    public async Task Zip_round_trips_with_real_crcs_and_stored_members()
    {
        var d = Tmp(); var z = Path.Combine(d, "a.zip");
        var data = Enumerable.Range(0, 5000).Select(i => (byte)(i * 7)).ToArray();
        ZipFormat.Write(z, [new("frames/0000.mjpeg", null, data), new("info.json", null, B("{}"))]);
        var es = await ZipFormat.ReadDirectoryAsync(new FileZipSource(z), default);
        Assert.Equal(["frames/0000.mjpeg", "info.json"], es.Select(e => e.Name).ToArray());
        Assert.All(es, e => Assert.Equal(0, e.Method));
        Assert.Equal(Crc32.Of(data), es[0].Crc); Assert.Equal(0xCBF43926u, Crc32.Of(B("123456789")));
        Assert.Equal(data, await ZipFormat.ReadMemberAsync(new FileZipSource(z), es[0], default));
        Directory.Delete(d, true);
    }

    [Fact]
    public async Task Zip64_is_used_past_the_boundary_and_reads_back()
    {
        var d = Tmp(); var z = Path.Combine(d, "a.zip"); var oldC = ZipFormat.Zip64Count; ZipFormat.Zip64Count = 2;
        try
        {
            ZipFormat.Write(z, Enumerable.Range(0, 4).Select(i => new ZipFormat.Member($"m{i}", null, B("x" + i))));
            var es = await ZipFormat.ReadDirectoryAsync(new FileZipSource(z), default);
            Assert.Equal(4, es.Count); Assert.Equal(B("x3"), await ZipFormat.ReadMemberAsync(new FileZipSource(z), es[3], default));
        }
        finally { ZipFormat.Zip64Count = oldC; Directory.Delete(d, true); }
    }

    sealed class Moving(IStore s) : IStore
    {
        public int Mismatches; public bool Rewrite; public string? Name;
        public Task<IReadOnlyList<BlobEntry>> ListAsync(string p, bool t, bool m, CancellationToken c) => s.ListAsync(p, t, m, c);
        public Task<IReadOnlyList<BlobEntry>> FindByTagsAsync(string w, CancellationToken c) => s.FindByTagsAsync(w, c);
        public Task<BlobContent?> GetAsync(string n, CancellationToken c) => s.GetAsync(n, c);
        public Task<ConditionalResult> GetIfChangedAsync(string n, string? e, CancellationToken c) => s.GetIfChangedAsync(n, e, c);
        public Task<IReadOnlyDictionary<string, string>?> GetTagsAsync(string n, CancellationToken c) => s.GetTagsAsync(n, c);
        public Task<bool> PutAsync(string n, byte[] d, IReadOnlyDictionary<string, string>? t, string? l, bool o, CancellationToken c, string ct = "application/json") => s.PutAsync(n, d, t, l, o, c, ct);
        public Task PutFileAsync(string n, string p, string ct, IProgress<long>? pr, CancellationToken c) => s.PutFileAsync(n, p, ct, pr, c);
        public Task DownloadToFileAsync(string n, string p, CancellationToken c) => s.DownloadToFileAsync(n, p, c);
        public Task DeleteAsync(string n, CancellationToken c) => s.DeleteAsync(n, c);
        public Task<string?> AcquireLeaseAsync(string n, int sec, CancellationToken c) => s.AcquireLeaseAsync(n, sec, c);
        public Task RenewLeaseAsync(string n, string l, CancellationToken c) => s.RenewLeaseAsync(n, l, c);
        public Task ReleaseLeaseAsync(string n, string l, CancellationToken c) => s.ReleaseLeaseAsync(n, l, c);
        public Task SetMetadataAsync(string n, IReadOnlyDictionary<string, string> m, string? l, CancellationToken c) => s.SetMetadataAsync(n, m, l, c);
        public Task SetTagsAsync(string n, IReadOnlyDictionary<string, string> t, string? l, CancellationToken c) => s.SetTagsAsync(n, t, l, c);
        public Task<bool> SetTagsIfAsync(string n, IReadOnlyDictionary<string, string> t, IReadOnlyDictionary<string, string> e, CancellationToken c) => s.SetTagsIfAsync(n, t, e, c);
        public Task<BlobProps?> HeadAsync(string n, CancellationToken c) => s.HeadAsync(n, c);
        public Task<RangeRead> GetRangeAsync(string n, long off, int len, string? m, CancellationToken c)
        {
            if (Rewrite) { Rewrite = false; Mismatches++; return Task.FromResult(new RangeRead(null, true)); }   // the zip was rewritten under the read: 412
            return s.GetRangeAsync(n, off, len, m, c);
        }
        public Task DeleteAsync(string n, string? l, CancellationToken c) => s.DeleteAsync(n, l, c);
        public Task<bool> PutZipAsync(string n, string p, IReadOnlyDictionary<string, string> m, IReadOnlyDictionary<string, string>? t, bool o, CancellationToken c) => s.PutZipAsync(n, p, m, t, o, c);
    }

    [Fact]
    public async Task Range_read_pins_the_etag_and_restarts_once_after_a_412()
    {
        var fake = new FakeStore(); var d = Tmp(); var cache = Path.Combine(d, "cache");
        var z = Path.Combine(d, "x.zip"); ZipFormat.Write(z, [new("info.json", null, B("{\"a\":1}"))]);
        await ((IStore)fake).PutZipAsync(P.Video("v", "download.zip"), z, StageLayout.Metadata("s", "i", "n", "{}"), null, false, default);
        var mv = new Moving(fake); var zs = new StageZipStore(mv, cache) { WholeZipLimit = 0 };   // 0: always range-read
        mv.Rewrite = true;
        var got = await zs.GetAsync(P.Video("v", "info.json"), default);
        Assert.Equal("{\"a\":1}", Encoding.UTF8.GetString(got!.Data)); Assert.Equal(1, mv.Mismatches);
        Directory.Delete(d, true);
    }

    [Fact]
    public void Metadata_is_ascii_under_8kb_and_oversized_summaries_fall_back()
    {
        var m = StageLayout.Metadata("sha", "in", "node-é", "{\"t\":\"" + "龙".PadRight(10) + "\"}");
        Assert.All(m.Values, v => Assert.All(v, ch => Assert.True(ch < 128)));
        Assert.Contains("\\u9f99", m["idx_summary"]);
        var big = StageLayout.Metadata("sha", "in", "n", "{\"t\":\"" + new string('a', 20000) + "\"}");
        Assert.Equal("{}", big["idx_summary"]); Assert.True(big.Sum(kv => kv.Key.Length + kv.Value.Length) < StageLayout.MaxMetadataBytes);
        Assert.Equal(["idx_at", "idx_inputs", "idx_node", "idx_sha256", "idx_summary", "idx_v"], m.Keys.OrderBy(k => k).ToArray());
    }

    static async Task<(FakeStore, StageZipStore, string)> Rig()
    {
        var fake = new FakeStore(); var d = Tmp(); var zs = new StageZipStore(fake, Path.Combine(d, "c")); return (fake, zs, d);
    }

    [Fact]
    public async Task Each_stage_commits_one_zip_with_its_members_and_metadata_each_time()
    {
        var (fake, zs, d) = await Rig();
        await zs.PutAsync(P.Video("v", "info.json"), B("{}"), null, null, false, default);
        await zs.PutAsync(P.Video("v", "downloaded.json"), B("{\"durationSeconds\":42}"), null, null, false, default);
        await zs.PutAsync(P.Video("v", "subs/en.vtt"), B("WEBVTT"), null, null, false, default);
        Assert.False(fake.Exists(P.Video("v", "download.zip")));   // nothing before the commit
        Assert.True(await zs.CommitStageAsync("v", "download", "n1", false, false, default));
        Assert.True(fake.Exists(P.Video("v", "download.zip"))); Assert.False(fake.Exists(P.Video("v", "info.json")));
        var listed = (await zs.ListAsync(P.VideoDir("v"), false, false, default)).Select(b => b.Name[P.VideoDir("v").Length..]).ToList();
        Assert.Contains("info.json", listed); Assert.Contains("subs/en.vtt", listed); Assert.Contains("download.zip", listed);
        Assert.Contains("42", Encoding.UTF8.GetString((await zs.GetAsync(P.Video("v", "downloaded.json"), default))!.Data));
        Assert.Contains("idx_summary", fake[P.Video("v", "download.zip")].Meta.Keys);
        // a merge keeps what the round did not rewrite
        await zs.PutAsync(P.Video("v", "info.json"), B("{\"x\":1}"), null, null, false, default);
        await zs.CommitStageAsync("v", "download", "n1", true, false, default);
        Assert.Equal("WEBVTT", Encoding.UTF8.GetString((await zs.GetAsync(P.Video("v", "subs/en.vtt"), default))!.Data));
        Assert.Equal("{\"x\":1}", Encoding.UTF8.GetString((await zs.GetAsync(P.Video("v", "info.json"), default))!.Data));
        Directory.Delete(d, true);
    }

    [Fact]
    public async Task Needs_are_stage_zips_and_a_job_waits_for_them()
    {
        Assert.Equal(["frames.zip", "sscd.zip"], Protocol.NeedsOf("cast"));
        Assert.Equal(["transcribe.zip", "ocrsubs.zip", "cast.zip"], Protocol.NeedsOf("describe"));
        Assert.Equal(["describe.zip"], Protocol.NeedsOf("translate"));
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Cast_crops_are_joined_per_chunk_and_read_back_under_their_old_names()
    {
        var (fake, zs, d) = await Rig();
        var a = new byte[] { 0xFF, 0xD8, 1, 2, 0xFF, 0xD9 }; var b = new byte[] { 0xFF, 0xD8, 9, 0xFF, 0xD9 }; var c = new byte[] { 0xFF, 0xD8, 7, 7, 7, 0xFF, 0xD9 };
        await zs.PutAsync(P.Video("v", "cast/cand_0.json"), B("[{\"k\":0,\"t\":5.0},{\"k\":1,\"t\":50.0},{\"k\":2,\"t\":700.0}]"), null, null, false, default);
        await zs.PutAsync(P.Video("v", "cast/clusters.json"), B("[]"), null, null, false, default);
        await zs.PutAsync(P.Video("v", "cast/cand_0_0.jpg"), a, null, null, false, default);
        await zs.PutAsync(P.Video("v", "cast/cand_0_1.jpg"), b, null, null, false, default);
        await zs.PutAsync(P.Video("v", "cast/cand_0_2.jpg"), c, null, null, false, default);
        await zs.CommitStageAsync("v", "cast", "n", false, false, default);
        var zip = Path.Combine(d, "cast.zip"); await fake.DownloadToFileAsync(P.Video("v", "cast.zip"), zip, default);
        var names = (await ZipFormat.ReadDirectoryAsync(new FileZipSource(zip), default)).Select(e => e.Name).ToList();
        Assert.Contains("cast/crops/0000.jpgs", names); Assert.Contains("cast/crops/0001.jpgs", names); Assert.Contains("cast/crops/0000.json", names);
        Assert.DoesNotContain(names, n => n.EndsWith(".jpg"));
        var zs2 = new StageZipStore(fake, Path.Combine(d, "c2"));
        Assert.Equal(a, (await zs2.GetAsync(P.Video("v", "cast/cand_0_0.jpg"), default))!.Data);
        Assert.Equal(b, (await zs2.GetAsync(P.Video("v", "cast/cand_0_1.jpg"), default))!.Data);
        Assert.Equal(c, (await zs2.GetAsync(P.Video("v", "cast/cand_0_2.jpg"), default))!.Data);
        Directory.Delete(d, true);
    }

    // ------------------------------------------------------------ repack
    static async Task Loose(FakeStore s, string id)
    {
        await s.PutAsync(P.Video(id, "video.mp4"), [1, 2, 3], null, null, false, default);
        await s.PutAsync(P.Video(id, "info.json"), B("{}"), null, null, false, default);
        await s.PutAsync(P.Video(id, "downloaded.json"), B("{\"durationSeconds\":9}"), null, null, false, default);
        await s.PutAsync(P.Video(id, "processed.json"), B("{}"), null, null, false, default);
        await s.PutAsync(P.Video(id, "frames/manifest.json"), B("{\"v\":1}"), null, null, false, default);
        await s.PutAsync(P.Video(id, "frames/0000.mjpeg"), [5, 6, 7, 8], null, null, false, default);
        await s.PutAsync(P.Video(id, "cast/cand_0.json"), B("[{\"k\":0,\"t\":1.0}]"), null, null, false, default);
        await s.PutAsync(P.Video(id, "cast/cand_0_0.jpg"), [0xFF, 0xD8, 1, 0xFF, 0xD9], null, null, false, default);
        await s.PutAsync(P.Video(id, "dino/manifest.json"), B("{}"), null, null, false, default);
    }

    static async Task<int> Go(FakeStore s, bool dry, bool del, string[]? only = null)
    {
        var lines = new List<string>();
        return await Repack.RunAsync(s, P, "rn", only, dry, del, 2, lines.Add, default);
    }

    [Fact]
    public async Task Dry_run_changes_nothing()
    {
        var s = new FakeStore(); await Loose(s, "a"); var before = s.Blobs.Keys.OrderBy(k => k).ToList();
        Assert.Equal(0, await Go(s, true, true));
        Assert.Equal(before, s.Blobs.Keys.OrderBy(k => k).ToList());
    }

    [Fact]
    public async Task Repack_writes_verified_zips_and_keeps_loose_files_unless_asked()
    {
        var s = new FakeStore(); await Loose(s, "a");
        Assert.Equal(0, await Go(s, false, false));
        Assert.True(s.Exists(P.Video("a", "download.zip"))); Assert.True(s.Exists(P.Video("a", "frames.zip"))); Assert.True(s.Exists(P.Video("a", "cast.zip")));
        Assert.True(s.Exists(P.Video("a", "info.json"))); Assert.True(s.Exists(P.Video("a", "video.mp4")));
        var c = await Repack.CountAsync(s, P, default); Assert.Equal(1, c.Mixed);
        Assert.Equal(0, await Go(s, false, true));   // resumable: the zips are already there and verified, then the loose files go
        Assert.False(s.Exists(P.Video("a", "info.json"))); Assert.False(s.Exists(P.Video("a", "dino/manifest.json"))); Assert.False(s.Exists(P.Video("a", "processed.json")));
        Assert.True(s.Exists(P.Video("a", "video.mp4")));
        c = await Repack.CountAsync(s, P, default); Assert.Equal((0, 1, 0), (c.Loose, c.Repacked, c.Mixed));
        Assert.Equal(0, await Go(s, false, true));   // nothing left to do
    }

    [Fact]
    public async Task A_zip_that_does_not_verify_deletes_nothing()
    {
        var s = new FakeStore(); await Loose(s, "a");
        var bad = new Corrupting(s);
        var lines = new List<string>();
        var rc = await Repack.RunAsync(bad, P, "rn", null, false, true, 1, lines.Add, default);
        Assert.Equal(1, rc); Assert.True(s.Exists(P.Video("a", "info.json"))); Assert.True(s.Exists(P.Video("a", "dino/manifest.json")));
        Assert.Contains(lines, l => l.Contains("NOT repacked"));
    }

    sealed class Corrupting(FakeStore fs) : IStore
    {
        IStore s => fs;
        // delegates everything, but the zips it is handed arrive with another hash in their metadata
        public Task<IReadOnlyList<BlobEntry>> ListAsync(string p, bool t, bool m, CancellationToken c) => s.ListAsync(p, t, m, c);
        public Task<IReadOnlyList<BlobEntry>> FindByTagsAsync(string w, CancellationToken c) => s.FindByTagsAsync(w, c);
        public Task<BlobContent?> GetAsync(string n, CancellationToken c) => s.GetAsync(n, c);
        public Task<ConditionalResult> GetIfChangedAsync(string n, string? e, CancellationToken c) => s.GetIfChangedAsync(n, e, c);
        public Task<IReadOnlyDictionary<string, string>?> GetTagsAsync(string n, CancellationToken c) => s.GetTagsAsync(n, c);
        public Task<bool> PutAsync(string n, byte[] d, IReadOnlyDictionary<string, string>? t, string? l, bool o, CancellationToken c, string ct = "application/json") => s.PutAsync(n, d, t, l, o, c, ct);
        public Task PutFileAsync(string n, string p, string ct, IProgress<long>? pr, CancellationToken c) => s.PutFileAsync(n, p, ct, pr, c);
        public Task DownloadToFileAsync(string n, string p, CancellationToken c) => s.DownloadToFileAsync(n, p, c);
        public Task DeleteAsync(string n, CancellationToken c) => s.DeleteAsync(n, c);
        public Task<string?> AcquireLeaseAsync(string n, int sec, CancellationToken c) => s.AcquireLeaseAsync(n, sec, c);
        public Task RenewLeaseAsync(string n, string l, CancellationToken c) => s.RenewLeaseAsync(n, l, c);
        public Task ReleaseLeaseAsync(string n, string l, CancellationToken c) => s.ReleaseLeaseAsync(n, l, c);
        public Task SetMetadataAsync(string n, IReadOnlyDictionary<string, string> m, string? l, CancellationToken c) => s.SetMetadataAsync(n, m, l, c);
        public Task SetTagsAsync(string n, IReadOnlyDictionary<string, string> t, string? l, CancellationToken c) => s.SetTagsAsync(n, t, l, c);
        public Task<bool> SetTagsIfAsync(string n, IReadOnlyDictionary<string, string> t, IReadOnlyDictionary<string, string> e, CancellationToken c) => s.SetTagsIfAsync(n, t, e, c);
        public Task<BlobProps?> HeadAsync(string n, CancellationToken c) => s.HeadAsync(n, c);
        public Task<RangeRead> GetRangeAsync(string n, long off, int len, string? m, CancellationToken c) => s.GetRangeAsync(n, off, len, m, c);
        public Task DeleteAsync(string n, string? l, CancellationToken c) => s.DeleteAsync(n, l, c);
        public Task<bool> PutZipAsync(string n, string p, IReadOnlyDictionary<string, string> m, IReadOnlyDictionary<string, string>? t, bool o, CancellationToken c) =>
            s.PutZipAsync(n, p, m.ToDictionary(k => k.Key, k => k.Key == "idx_sha256" ? new string('0', 64) : k.Value), t, o, c);
    }

    [Fact]
    public async Task A_loose_or_mixed_drama_is_refused_by_processing_and_counted()
    {
        var s = new FakeStore(); await Loose(s, "a");
        var zs = new StageZipStore(s, Path.Combine(Tmp(), "c"));
        Assert.NotEmpty(await zs.LooseFilesAsync("index/", "a", default));
        Assert.Equal(1, (await Repack.CountAsync(s, P, default)).Loose);
        await Go(s, false, true);
        Assert.Empty(await zs.LooseFilesAsync("index/", "a", default));
    }
}
