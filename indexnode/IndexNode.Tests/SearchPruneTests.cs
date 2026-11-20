using System.Reflection;

namespace IndexNode.Tests;

/// <summary>0.13.1: the search-index job keeps the head plus search.keepVersions (3) versions and deletes older vK/ folders; readers that 404 on a pruned version re-read head.yaml and retry once.</summary>
public class SearchPruneTests
{
    static readonly Paths P = new("index-test/");

    static SearchData Tiny(int v) => new() { Version = v, MeanVersion = 1, Mean = new float[CentersFile.Dim], Bits = [], RefUpload = [], RefCenter = [], Uploads = [], BuiltAt = DateTimeOffset.Parse("2026-11-20T08:00:00Z") };

    static async Task PublishAsync(IStore s, int v) => await Tiny(v).PublishAsync(s, P, default);

    [Fact]
    public async Task Pruning_after_head_moves_keeps_the_head_and_three_back_and_deletes_every_file_of_older_versions()
    {
        var fs = new FakeStore();
        for (int v = 1; v <= 6; v++) await PublishAsync(fs, v);
        var gone = await SearchData.PruneAsync(fs, P, 3, default);
        Assert.Equal([1, 2], gone);
        Assert.Empty(await fs.ListAsync(P.Prefix + "search/v1/", false, false, default));
        Assert.Empty(await fs.ListAsync(P.Prefix + "search/v2/", false, false, default));
        foreach (var v in new[] { 3, 4, 5, 6 }) Assert.NotEmpty(await fs.ListAsync(SearchData.Dir(P, v), false, false, default));
        Assert.Equal(6, SearchData.ParseHead((await fs.GetAsync(SearchData.HeadName(P), default))!.Data));
        Assert.NotNull(await SearchData.LoadAsync(fs, P, null, default));
        Assert.Empty(await SearchData.PruneAsync(fs, P, 3, default));   // nothing more to remove
    }

    [Fact]
    public async Task The_keep_count_is_a_setting_and_v10_is_not_mistaken_for_v1()
    {
        var fs = new FakeStore();
        for (int v = 1; v <= 11; v++) await PublishAsync(fs, v);
        var gone = await SearchData.PruneAsync(fs, P, 1, default);   // keep 11 and 10
        Assert.Equal(Enumerable.Range(1, 9).ToList(), gone);
        Assert.NotEmpty(await fs.ListAsync(SearchData.Dir(P, 10), false, false, default));
        Assert.Equal(0, new NodeConfig().Search?.KeepVersions ?? 0);   // unset: the job passes its default of 3
        Assert.Equal(3, new SearchSettings().KeepVersions);
    }

    /// <summary>A store whose first read of head.yaml says an old version (as a reader that read it just before the job pruned it); later reads are the real head.</summary>
    public class StaleHead : DispatchProxy
    {
        public IStore Inner = null!; public int StaleVersion; public int HeadReads; public bool Stale = true;
        protected override object? Invoke(MethodInfo? m, object?[]? args)
        {
            var r = m!.Invoke(Inner, args);
            if (Stale && args is { Length: > 0 } && args[0] is string n && n == SearchData.HeadName(P) && m.Name is "GetAsync" or "GetIfChangedAsync")
            {
                HeadReads++;
                if (m.Name == "GetAsync" && r is Task<BlobContent?> t) return Task.Run(async () => { var c = await t; Stale = false; return c == null ? null : new BlobContent(SearchData.HeadYaml(StaleVersion, SearchData.Dir(P, StaleVersion)), c.ETag); });
            }
            return r;
        }
    }

    [Fact]
    public async Task A_reader_that_gets_a_404_on_its_loaded_version_rereads_head_and_retries_once_on_the_current_one()
    {
        var fs = new FakeStore();
        for (int v = 1; v <= 5; v++) await PublishAsync(fs, v);
        await SearchData.PruneAsync(fs, P, 3, default);   // v1 is gone, head is v5
        var proxy = DispatchProxy.Create<IStore, StaleHead>(); var sh = (StaleHead)(object)proxy; sh.Inner = fs; sh.StaleVersion = 1;
        var d = await SearchData.LoadAsync(proxy, P, null, default);
        Assert.Equal(5, d!.Version);   // the first head read said v1 (pruned: 404), the second said v5
        Assert.True(sh.HeadReads >= 1);
    }

    [Fact]
    public async Task A_404_that_is_not_a_moved_head_still_fails()
    {
        var fs = new FakeStore();
        for (int v = 1; v <= 2; v++) await PublishAsync(fs, v);
        await fs.DeleteAsync(SearchData.Dir(P, 2) + "bits.bin", default);   // head v2, a file missing, head unchanged
        await Assert.ThrowsAsync<FileNotFoundException>(() => SearchData.LoadAsync(fs, P, null, default));
    }
}
