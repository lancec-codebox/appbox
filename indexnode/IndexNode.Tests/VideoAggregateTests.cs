using System.Text.Json.Nodes;

namespace IndexNode.Tests;

/// <summary>The aggregate video.json (index/data/videos.agg): versioned block ids, commit by Get Block List + Put Block List If-Match, readers by Get Block List + GET If-Match. FakeStore has real block semantics.</summary>
[Collection("console")]
public class VideoAggregateTests
{
    static readonly Paths P = new("index/");
    static string Agg => VideoAggregate.Name(P);
    public VideoAggregateTests() { VideoAggregate.Enabled = true; VideoAggregate.Backoff = _ => TimeSpan.Zero; VideoAggregate.Stats.Reset(); VideoJson.ClearCache(new FakeStore()); }

    static Task<JsonObject> Put(FakeStore s, string id, string key, string val) => VideoJson.UpdateAsync(s, P, id, r => r[key] = val, default);
    static async Task<Dictionary<string, VideoJson.Doc>> All(FakeStore s) => await VideoAggregate.ReadManyAsync(s, P, null, default) ?? [];

    [Fact]
    public async Task Concurrent_writers_on_different_uploads_all_land()
    {
        var s = new FakeStore(); var ids = Enumerable.Range(0, 40).Select(i => "vid" + i.ToString("D8")).ToList();
        await Task.WhenAll(ids.Select(id => Task.Run(() => Put(s, id, "speech", "zh-" + id))));
        var all = await All(s);
        Assert.Equal(ids.Count, all.Count);
        foreach (var id in ids) Assert.Equal("zh-" + id, (string)all[id].Root["speech"]!);
        Assert.Empty(s.StagedIds(Agg));   // every block committed
    }

    [Fact]
    public async Task A_writer_whose_block_was_already_committed_by_another_commit_skips_its_own_commit()
    {
        var s = new FakeStore(); await Put(s, "aaaaaaaaaaa", "k", "1");
        // B's Put Block lands, then A's commit (which names every pending block) goes first: B then finds its id committed and does not commit
        s.BeforeGetBlockList = _ => { };   // (hook point exists; the interleaving is forced below)
        var commitsBefore = VideoAggregate.Stats.Commits;
        var rootB = new JsonObject { ["id"] = "bbbbbbbbbbb", ["rev"] = 1 };
        await s.PutBlockAsync(Agg, VideoAggregate.BlockId("bbbbbbbbbbb", 1), VideoAggregate.BlockBytes(rootB), default);   // B staged, not yet committed
        await Put(s, "aaaaaaaaaaa", "k", "2");   // A commits: the list it builds includes B's pending block
        long before = VideoAggregate.Stats.Commits, skipped0 = VideoAggregate.Stats.AlreadyCommitted;
        Assert.True(await VideoAggregate.WriteAsync(s, P, "bbbbbbbbbbb", 1, rootB, default));
        Assert.Equal(before, VideoAggregate.Stats.Commits);   // no commit of its own
        Assert.Equal(skipped0 + 1, VideoAggregate.Stats.AlreadyCommitted);
        Assert.Contains("bbbbbbbbbbb", (await All(s)).Keys);
    }

    [Fact]
    public async Task A_commit_race_gets_a_412_and_retries_to_the_end()
    {
        var s = new FakeStore(); await Put(s, "aaaaaaaaaaa", "k", "1");
        // between my Get Block List and my Put Block List another writer commits: my If-Match fails (412), I list again and commit on top
        s.BeforePutBlockList = _ => Put(s, "ccccccccccc", "k", "c").GetAwaiter().GetResult();
        await Put(s, "bbbbbbbbbbb", "k", "b");
        Assert.True(s.BlockListConflicts >= 1); Assert.True(VideoAggregate.Stats.CommitConflicts >= 1);
        var all = await All(s); Assert.Equal(3, all.Count); Assert.Equal("c", (string)all["ccccccccccc"].Root["k"]!); Assert.Equal("b", (string)all["bbbbbbbbbbb"].Root["k"]!);
    }

    [Fact]
    public async Task A_late_stale_put_with_a_lower_rev_loses()
    {
        var s = new FakeStore(); await Put(s, "aaaaaaaaaaa", "k", "v1"); await Put(s, "aaaaaaaaaaa", "k", "v2");   // rev 2 committed
        var stale = new JsonObject { ["id"] = "aaaaaaaaaaa", ["rev"] = 1, ["k"] = "stale" };
        await s.PutBlockAsync(Agg, VideoAggregate.BlockId("aaaaaaaaaaa", 1), VideoAggregate.BlockBytes(stale), default);   // a slow writer's rev-1 block arrives late
        await Put(s, "bbbbbbbbbbb", "k", "x");   // the next commit keeps one block per upload: the highest rev
        var all = await All(s);
        Assert.Equal("v2", (string)all["aaaaaaaaaaa"].Root["k"]!); Assert.Equal(2, VideoAggregate.RevOf(all["aaaaaaaaaaa"].Root));
        Assert.Empty(s.StagedIds(Agg));
    }

    [Fact]
    public async Task A_discarded_block_is_put_again()
    {
        var s = new FakeStore(); await Put(s, "aaaaaaaaaaa", "k", "1");
        var root = new JsonObject { ["id"] = "bbbbbbbbbbb", ["rev"] = 1, ["k"] = "b" };
        // my Put Block lands, then a commit that did not name it discards it (e.g. one that listed before my put): the next Get Block List shows it nowhere
        s.BeforeGetBlockList = _ => s.Drop(Agg);
        Assert.True(await VideoAggregate.WriteAsync(s, P, "bbbbbbbbbbb", 1, root, default));
        Assert.True(VideoAggregate.Stats.Reputs >= 1);
        Assert.Equal("b", (string)(await All(s))["bbbbbbbbbbb"].Root["k"]!);
    }

    [Fact]
    public async Task Two_writers_of_the_same_upload_end_on_the_higher_rev()
    {
        var s = new FakeStore();
        await Task.WhenAll(Enumerable.Range(0, 6).Select(i => Task.Run(() => VideoJson.UpdateAsync(s, P, "aaaaaaaaaaa", r => r["n" + i] = i, default))));
        var fd = await VideoJson.RequireAsync(s, P, "aaaaaaaaaaa", default);
        Assert.Equal(6, fd.Rev); Assert.Null(fd.Root["rev"]);   // rev lives in the blob metadata (x-ms-meta-rev), not in the body   // every write raised the rev inside its If-Match
        var aggDoc = (await All(s))["aaaaaaaaaaa"]; var agg = aggDoc.Root; Assert.Equal(6, aggDoc.Rev); Assert.Equal("6", s.Blobs[P.Video("aaaaaaaaaaa", "video.json")].Meta["rev"]);
        for (int i = 0; i < 6; i++) Assert.Equal(i, (int)agg["n" + i]!);   // nothing lost
    }

    [Fact]
    public async Task A_reader_that_races_a_commit_gets_a_412_and_its_retry_reads_a_consistent_version()
    {
        var s = new FakeStore(); await Put(s, "aaaaaaaaaaa", "k", "old"); await Put(s, "bbbbbbbbbbb", "k", "old");
        // after the reader's block list, a commit lands: the reader's GET If-Match fails (412), it lists again and reads the new version whole
        s.BeforeRange = _ => Put(s, "aaaaaaaaaaa", "k", "new").GetAwaiter().GetResult();
        var all = await All(s);
        Assert.True(VideoAggregate.Stats.ReadRetries >= 1);
        Assert.Equal("new", (string)all["aaaaaaaaaaa"].Root["k"]!); Assert.Equal("old", (string)all["bbbbbbbbbbb"].Root["k"]!);
        Assert.Equal(0, s.StagedIds(Agg).Count);
    }

    [Fact]
    public async Task Block_ids_are_all_the_same_length_a_short_and_a_long_id_both_round_trip_and_a_too_long_one_is_refused()
    {
        var s = new FakeStore(); var shortId = "ab"; var longId = new string('L', 32);
        await Put(s, shortId, "k", "s"); await Put(s, longId, "k", "l"); await Put(s, "abcdefghij1", "k", "y");
        var bl = (await s.GetBlockListAsync(Agg, default))!;
        Assert.Equal(3, bl.Committed.Count); Assert.Single(bl.Committed.Select(b => b.Id.Length).Distinct()); Assert.Equal(44, bl.Committed[0].Id.Length); Assert.All(bl.Committed, b => Convert.FromBase64String(b.Id));   // 44 base64 characters, valid as they are
        Assert.Equal((shortId, 1L), VideoAggregate.Decode(VideoAggregate.BlockId(shortId, 1))!.Value);
        var all = await All(s); Assert.Equal("s", (string)all[shortId].Root["k"]!); Assert.Equal("l", (string)all[longId].Root["k"]!);
        Assert.Equal("110L+GBl3L428" + new string('A', 21) + "0000000007", VideoAggregate.BlockId("0L-GBl3L428", 7));   // '-' is '+' in the id
        Assert.Equal(("0L-GBl3L_28", 7L), VideoAggregate.Decode(VideoAggregate.BlockId("0L-GBl3L_28", 7))!.Value);
        await Put(s, "a-b_c-d_e-f", "k", "m"); Assert.Equal("m", (string)(await All(s))["a-b_c-d_e-f"].Root["k"]!);
        var tooLong = new string('T', 33);
        Assert.False(VideoAggregate.Supports(tooLong));
        await Put(s, tooLong, "k", "t");   // the per-upload file is written, the aggregate refuses the id
        Assert.DoesNotContain(tooLong, (await All(s)).Keys); Assert.Equal("t", (string)(await VideoJson.RequireAsync(s, P, tooLong, default)).Root["k"]!);
    }

    [Fact]
    public async Task Stretches_move_to_stretches_json_and_the_block_has_no_stretches_and_a_title()
    {
        var s = new FakeStore();
        await VideoJson.UpdateAsync(s, P, "aaaaaaaaaaa", r => { r["youtube"] = new JsonObject { ["title"] = "T1" }; r["grouping"] = new JsonObject { ["coveredShare"] = 0.5, ["stretches"] = new JsonArray(new JsonObject { ["from"] = 1, ["to"] = 2, ["upload"] = "zzzzzzzzzzz", ["offset"] = 0 }) }; }, default);
        var blk = (await All(s))["aaaaaaaaaaa"].Root;
        Assert.Null(blk["grouping"]!["stretches"]); Assert.Equal(0.5, (double)blk["grouping"]!["coveredShare"]!); Assert.Equal("T1", (string)blk["title"]!);
        var st = await VideoJson.ReadStretchesAsync(s, P, "aaaaaaaaaaa", default); Assert.Equal(1, st!.Count);
        Assert.True(s.Exists(P.Video("aaaaaaaaaaa", "stretches.json")));
    }

    [Fact]
    public async Task The_migration_round_trip_matches_the_per_upload_files()
    {
        var s = new FakeStore(); VideoAggregate.Enabled = false;   // the old world: per-upload files only
        var ids = Enumerable.Range(0, 25).Select(i => "mig" + i.ToString("D8")).ToList();
        foreach (var id in ids) await VideoJson.UpdateAsync(s, P, id, r => { r["speech"] = new JsonObject { ["language"] = "ko" }; r["grouping"] = new JsonObject { ["stretches"] = new JsonArray(new JsonObject { ["from"] = 0, ["to"] = 1, ["upload"] = "x", ["offset"] = 0 }), ["coveredShare"] = 0.1 }; }, default);
        VideoAggregate.Enabled = true; Assert.False(s.Exists(Agg));
        var dry = await VideosAggregateCommand.BuildAsync(s, P, true, 4, null, default); Assert.Equal(25, dry.Blocks); Assert.False(s.Exists(Agg));
        var rep = await VideosAggregateCommand.BuildAsync(s, P, false, 4, null, default); Assert.Equal(25, rep.Blocks); Assert.Equal(25, rep.Stretches);
        var all = await All(s); Assert.Equal(25, all.Count);
        foreach (var id in ids)
        {
            var file = (JsonObject)(await VideoJson.RequireAsync(s, P, id, default)).Root.DeepClone(); ((JsonObject)file["grouping"]!).Remove("stretches");
            file["title"] = file["title"]; var want = file.ToJsonString(); var got = all[id].Root; if (got["title"] == null) file.Remove("title");
            Assert.Equal(file.ToJsonString(), got.ToJsonString());
            Assert.True(s.Exists(P.Video(id, "stretches.json")));
        }
        var again = await VideosAggregateCommand.BuildAsync(s, P, false, 4, null, default); Assert.Equal(0, again.Blocks); Assert.Equal(25, again.AlreadyCurrent);   // idempotent
    }

    [Fact]
    public async Task A_join_with_stretches_gets_a_stretches_json_and_a_file_without_rev_metadata_gets_a_metadata_only_call()
    {
        var s = new FakeStore(); var id = "kpBUg3m6IJA";
        var st = new JsonArray(Enumerable.Range(0, 2370).Select(i => (JsonNode?)new JsonObject { ["from"] = i, ["to"] = i + 1, ["upload"] = "x", ["offset"] = 0.5 }).ToArray());
        // an old file: no metadata, rev in no place, written whole
        var body = new JsonObject { ["v"] = 1, ["id"] = id, ["grouping"] = new JsonObject { ["proposal"] = new JsonObject { ["kind"] = "join" }, ["stretches"] = st } };
        await s.PutAsync(P.Video(id, "video.json"), System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(body), null, null, false, default);
        var before = s.Blobs[P.Video(id, "video.json")].Data.ToArray(); var etag = (await s.HeadAsync(P.Video(id, "video.json"), default))!.ETag;
        var rep = await VideosAggregateCommand.BuildAsync(s, P, false, 2, null, default);
        Assert.Equal(1, rep.Stretches); Assert.Equal(1, rep.MetadataSets);
        var sj = await s.GetAsync(P.Video(id, "stretches.json"), default); Assert.NotNull(sj); Assert.Equal(2370, JsonNode.Parse(sj!.Data)!.AsArray().Count);
        Assert.Equal(before, s.Blobs[P.Video(id, "video.json")].Data);   // content untouched
        Assert.Equal("1", s.Blobs[P.Video(id, "video.json")].Meta["rev"]);
        Assert.Equal(1, (await All(s))[id].Rev);
        var again = await VideosAggregateCommand.BuildAsync(s, P, false, 2, null, default); Assert.Equal(0, again.MetadataSets); Assert.Equal(0, again.Blocks);
    }

    [Fact]
    public async Task Stretches_json_reaches_the_inner_store_through_the_wrapped_store_and_an_empty_list_writes_none()
    {
        var f = new FakeStore(); var dir = Path.Combine(Path.GetTempPath(), "stz-" + Guid.NewGuid().ToString("N")[..8]);
        IStore s = StageZipStore.Wrap(new MeteredStore(f, new StoreMeter()), dir);   // as Program.Open builds it (a StageZipStore around the store)
        try
        {
            var st = new JsonArray(new JsonObject { ["from"] = 1, ["to"] = 2, ["upload"] = "x", ["offset"] = 0.5 });
            await VideoJson.UpdateAsync(s, P, "withstretch1", r => r["grouping"] = new JsonObject { ["proposal"] = new JsonObject { ["kind"] = "join" }, ["stretches"] = st }, default);
            await VideoJson.UpdateAsync(s, P, "emptystretch", r => r["grouping"] = new JsonObject { ["stretches"] = new JsonArray() }, default);
            Assert.True(f.Exists(P.Video("withstretch1", "stretches.json")));    // in the real store, not staged locally
            Assert.False(f.Exists(P.Video("emptystretch", "stretches.json")));
            Assert.Empty(await VideoJson.ReadStretchesAsync(s, P, "emptystretch", default) ?? []);
            Assert.Single(await VideoJson.ReadStretchesAsync(s, P, "withstretch1", default) ?? []);
            // the migration through the same wrapped store
            var old = new JsonObject { ["v"] = 1, ["id"] = "migjoin0001", ["grouping"] = new JsonObject { ["stretches"] = st.DeepClone() } };
            await f.PutAsync(P.Video("migjoin0001", "video.json"), System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(old), null, null, false, default);
            var rep = await VideosAggregateCommand.BuildAsync(s, P, false, 2, null, default);
            Assert.True(f.Exists(P.Video("migjoin0001", "stretches.json"))); Assert.Equal(2, rep.Stretches);   // withstretch1 and migjoin0001; the empty list of emptystretch is not counted
            Assert.False(f.Exists(P.Video("emptystretch", "stretches.json")));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public async Task The_ai_check_reads_zero_per_upload_files_for_uploads_the_aggregate_holds()
    {
        var s = new FakeStore(); var ids = Enumerable.Range(0, 30).Select(i => "ai" + i.ToString("D9")).ToList();
        foreach (var id in ids) await VideoJson.UpdateAsync(s, P, id, r => VideoJson.SetAi(r, 0.9, true, "precheck"), default);
        s.Gets.Clear(); VideoJson.ClearCache(s);
        var docs = await VideoJson.ReadManyAsync(s, P, ids, default);
        Assert.All(ids, id => Assert.True(ActorsStage.AiFlag(docs[id]!.Root)));
        Assert.DoesNotContain(s.Gets, g => g.Contains("/video.json"));   // not one per-upload video.json
        Assert.Equal(1, s.Gets.Count(g => g == Agg)); Assert.Equal(1, s.BlockListGets - 0 > 0 ? 1 : 0);
    }

    /// <summary>What an old (0.13.23) node does: replaces the per-upload blob with NO x-ms-meta-rev and leaves the aggregate alone.</summary>
    static async Task OldWrite(FakeStore s, string id, string key, string val)
    {
        var name = VideoJson.Name(P, id); var cur = JsonNode.Parse((await s.GetAsync(name, default))!.Data)!.AsObject(); cur[key] = val;
        await s.PutAsync(name, System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(cur), null, null, false, default);
    }
    static async Task<string> BlockOf(FakeStore s, string id) => (string)(await All(s))[id].Root["speech"]!;

    [Fact]
    public async Task New_write_old_write_new_write_ends_with_the_block_equal_to_the_per_upload_content()
    {
        var s = new FakeStore(); const string id = "mixedaaaaaa";
        await Put(s, id, "speech", "one"); await Put(s, id, "speech", "two"); await Put(s, id, "speech", "three");   // rev 3 committed
        await OldWrite(s, id, "speech", "OLD");   // per-upload file now "OLD", no rev metadata, aggregate still "three"
        await Put(s, id, "title2", "x");          // new node: rev 0 + 1 = 1, below the committed 3
        var file = (await VideoJson.ReadAsync(s, P, id, default, fresh: true))!;
        var agg = (await All(s))[id];
        Assert.Equal("OLD", (string)file.Root["speech"]!); Assert.Equal("OLD", (string)agg.Root["speech"]!); Assert.Equal("x", (string)agg.Root["title2"]!);
        Assert.True(VideoAggregate.SameContent(agg.Root, file.Root)); Assert.True(agg.Rev >= 4); Assert.Equal(agg.Rev, file.Rev);   // the metadata matches the block
        Assert.Empty(s.StagedIds(Agg));
    }

    [Fact]
    public async Task The_migration_after_an_old_write_fixes_the_stale_block_and_a_rerun_finds_nothing()
    {
        var s = new FakeStore(); const string id = "mixedbbbbbb";
        await Put(s, id, "speech", "one"); await Put(s, id, "speech", "two");
        await OldWrite(s, id, "speech", "OLD");
        var rep = await VideosAggregateCommand.BuildAsync(s, P, false, 2, null, default);
        Assert.Equal(1, rep.StaleFixed); var agg = (await All(s))[id]; var file = (await VideoJson.ReadAsync(s, P, id, default, fresh: true))!;
        Assert.Equal("OLD", (string)agg.Root["speech"]!); Assert.Equal(3, agg.Rev); Assert.Equal(3, file.Rev);
        var again = await VideosAggregateCommand.BuildAsync(s, P, false, 2, null, default); Assert.Equal(0, again.StaleFixed); Assert.Equal(1, again.AlreadyCurrent);
    }
}
