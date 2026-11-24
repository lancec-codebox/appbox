using System.Text.Json.Nodes;
using Xunit;

namespace IndexNode.Tests;

/// <summary>0.13.37: a shadow run writes nothing to the real table (fake HTTP handler: 0 write requests), and reads see the overlay's own writes.</summary>
[Collection("SeqBlobs")]
public class ShadowTableTests
{
    static readonly Paths P = new("index/");
    static string Dir() => Path.Combine(Path.GetTempPath(), "shadowtbl-" + Guid.NewGuid().ToString("N")[..8]);
    static int Writes(FakeTableHttp h) => h.Log.Count(l => !l.StartsWith("GET "));

    static (ShadowStore shadow, ShadowMediaTable overlay, FakeMediaTable real, FakeTableHttp http, FakeStore fs, string dir) Setup()
    {
        var fs = new FakeStore(); var real = new FakeMediaTable(); var m = new StoreMeter();
        var restTable = FakeTableHttp.Rest(real, m, out var http); var dir = Dir();
        var shadow = new ShadowStore(fs, dir); var overlay = new ShadowMediaTable(restTable, dir); MediaTables.Attach(shadow, overlay);
        return (shadow, overlay, real, http, fs, dir);
    }

    [Fact]
    public async Task For_stops_at_the_shadow_store_and_returns_its_overlay_never_the_real_table()
    {
        var (shadow, overlay, _, _, fs, dir) = Setup();
        try
        {
            MediaTables.Attach(fs, new FakeMediaTable());   // the real store has a table of its own
            Assert.Same(overlay, MediaTables.For(shadow));
            Assert.Same(overlay, MediaTables.For(StageZipStore.Wrap(shadow, null)));
            Assert.Null(MediaTables.For(new ShadowStore(fs, Dir())));   // a shadow with no overlay has no table: it never walks to the real one
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public async Task A_group_step_with_seq_a_marks_run_and_a_VideoJson_write_send_no_write_request_to_the_real_table()
    {
        var (shadow, overlay, real, http, fs, dir) = Setup();
        try
        {
            await real.MergeAsync("upload", "vidA", new JsonObject { ["title"] = "real" }, default);
            long writes0 = Writes(http);
            // group step with seq (the blob side goes to the shadow's local overlay)
            var (mine, _) = await SeqBlobs.AssignAsync(shadow, P, MediaTables.For(shadow)!, "vidA", false, default);
            // the marks of an actors job
            await StageMarks.RunAsync(MediaTables.For(shadow), "actors", "vidA", "running", 1, "full", "n", 1, DateTimeOffset.UtcNow, default);
            await StageMarks.DoneAsync(MediaTables.For(shadow), "actors", "vidA", 1, "full", "n", 1, DateTimeOffset.UtcNow, default);
            // a VideoJson section write
            await VideoJson.UpdateAsync(shadow, P, "vidA", r => r["grouping"] = new JsonObject { ["v"] = 1 }, default);
            Assert.Equal(writes0, Writes(http));                      // 0 write requests (every request the fake handler saw was a GET)
            Assert.DoesNotContain(http.Log, l => l.StartsWith("PATCH") || l.StartsWith("POST") || l.StartsWith("PUT") || l.StartsWith("DELETE"));
            Assert.Equal(1, real.Merges);                             // only the test's own seed write is in the real table
            Assert.Null(real.GetAsync("stage-actors", "vidA", null, default).Result);
            Assert.True(Directory.Exists(Path.Combine(dir, "table", "stage-actors")));   // the writes are local files
            Assert.True(File.Exists(Path.Combine(dir, "table", "upload", "vidA.json")));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public async Task Reads_see_the_overlays_own_writes_merged_over_the_real_row_and_queries_too()
    {
        var (shadow, overlay, real, http, fs, dir) = Setup();
        try
        {
            await real.MergeAsync("upload", "v1", new JsonObject { ["title"] = "real", ["data"] = "full" }, default);
            await real.MergeAsync("upload", "v2", new JsonObject { ["title"] = "other", ["data"] = "full" }, default);
            await overlay.MergeAsync("upload", "v1", new JsonObject { ["title"] = "shadowed", ["extra"] = 7 }, default);   // merged as Table merges: title replaced, data kept
            await overlay.InsertOrMergeAsync("upload", "v3", new JsonObject { ["title"] = "new", ["data"] = "full" }, default);   // a row only the overlay knows
            var row = (await overlay.GetAsync("upload", "v1", null, default))!;
            Assert.Equal("shadowed", row.Str("title")); Assert.Equal("full", row.Str("data")); Assert.Equal(7, (int)row.Props["extra"]!);
            Assert.Equal("real", (await real.GetAsync("upload", "v1", null, default))!.Str("title"));   // the real one is untouched
            Assert.NotNull(await overlay.GetAsync("upload", "v3", null, default)); Assert.Null(await real.GetAsync("upload", "v3", null, default));
            var q = await overlay.QueryAsync("PartitionKey eq 'upload' and data eq 'full'", null, default);
            Assert.Equal(["v1", "v2", "v3"], q.Select(x => x.RowKey).OrderBy(x => x).ToArray());
            Assert.Equal("shadowed", q.First(x => x.RowKey == "v1").Str("title"));
            Assert.Equal(0, Writes(http));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public async Task A_local_only_command_table_refuses_writes_and_reads_through()
    {
        var real = new FakeMediaTable(); await real.MergeAsync("upload", "x", new JsonObject { ["a"] = 1 }, default);
        var ro = new ReadOnlyMediaTable(real);
        Assert.NotNull(await ro.GetAsync("upload", "x", null, default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => ro.MergeAsync("upload", "x", new JsonObject(), default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => ro.InsertOrMergeAsync("upload", "x", new JsonObject(), default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => ro.BatchInsertOrMergeAsync("upload", [("x", new JsonObject())], default));
        Assert.Equal(1, real.Merges);
    }
}
