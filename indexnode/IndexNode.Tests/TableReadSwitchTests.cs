using System.Text.Json.Nodes;
using Xunit;

namespace IndexNode.Tests;

/// <summary>
/// 0.13.37 (root review): the media table's read switch is off by default and kept per store; a partial row never answers for a document; deletes are refused outside the selftest's scratch partitions;
/// whatif says when this build cannot make the required version.
/// </summary>
public class TableReadSwitchTests
{
    static readonly Paths P = new("index/");

    static async Task<(FakeStore S, FakeMediaTable T)> StoreWithDoc(string id)
    {
        var s = new FakeStore(); var t = new FakeMediaTable(); MediaTables.Attach(s, t);
        await VideoJson.UpdateAsync(s, P, id, r => { r["youtube"] = new JsonObject { ["title"] = "T-" + id }; r["ai"] = new JsonObject { ["flag"] = true, ["score"] = 0.9 }; }, default);
        return (s, t);
    }

    [Fact]
    public async Task Reads_are_off_by_default_the_writer_still_mirrors_the_whole_document_into_the_table()
    {
        var (s, t) = await StoreWithDoc("u1");
        Assert.False(MediaTables.ReadsOn(s)); Assert.Null(MediaTables.ForRead(s)); Assert.NotNull(MediaTables.For(s));
        var row = await t.GetAsync(MediaTables.Upload, "u1", null, default);
        Assert.NotNull(row); Assert.True(MediaRecords.IsFull(row!));   // the dual write: every section and docRev
        VideoJson.ClearCache(s); int gets = t.Gets, queries = t.Queries;
        var doc = await VideoJson.ReadAsync(s, P, "u1", default);
        Assert.Equal("T-u1", (string?)doc!.Root["youtube"]!["title"]);
        await VideoJson.ReadManyAsync(s, P, ["u1"], default);
        Assert.Equal(gets, t.Gets); Assert.Equal(queries, t.Queries);   // with reads off no reader touches the table
    }

    [Fact]
    public async Task The_switch_is_per_store_turning_one_store_on_leaves_another_off()
    {
        var (a, ta) = await StoreWithDoc("u1"); var (b, tb) = await StoreWithDoc("u1");
        MediaTables.SetReads(a, true);
        Assert.True(MediaTables.ReadsOn(a)); Assert.False(MediaTables.ReadsOn(b));
        Assert.True(MediaTables.ReadsOn(new MeteredStore(a, new StoreMeter())));   // found through a wrapper
        VideoJson.ClearCache(a); VideoJson.ClearCache(b); int ga = ta.Gets, gb = tb.Gets;
        Assert.Equal("T-u1", (string?)(await VideoJson.ReadAsync(a, P, "u1", default))!.Root["youtube"]!["title"]);
        await VideoJson.ReadAsync(b, P, "u1", default);
        Assert.True(ta.Gets > ga); Assert.Equal(gb, tb.Gets);
        MediaTables.SetReads(a, false); Assert.False(MediaTables.ReadsOn(a));
        MediaTables.SetReads(new FakeStore(), true);   // a store with no table: nothing to set, nothing thrown
    }

    [Fact]
    public async Task With_reads_on_a_partial_row_does_not_answer_for_the_document()
    {
        var s = new FakeStore(); var t = new FakeMediaTable();
        await VideoJson.UpdateAsync(s, P, "u2", r => { r["youtube"] = new JsonObject { ["title"] = "from the blob" }; r["ai"] = new JsonObject { ["flag"] = true }; }, default);   // written by a node with no table
        MediaTables.Attach(s, t); MediaTables.SetReads(s, true);
        await StageMarks.DoneAsync(t, "cast", "u2", 2, "repair", "n1", 3, null, default);   // a mark creates the upload row with only its own property
        var row = await t.GetAsync(MediaTables.Upload, "u2", null, default);
        Assert.NotNull(row); Assert.False(MediaRecords.IsFull(row!));
        VideoJson.ClearCache(s);
        var doc = await VideoJson.ReadAsync(s, P, "u2", default);
        Assert.Equal("from the blob", (string?)doc!.Root["youtube"]!["title"]);
        Assert.True((bool?)doc.Root["ai"]!["flag"]);   // the AI flag is not lost behind the partial row
        var many = await VideoJson.ReadManyAsync(s, P, ["u2"], default);
        Assert.Equal("from the blob", (string?)many["u2"]!.Root["youtube"]!["title"]);
    }

    [Fact]
    public async Task A_stage_mark_and_a_section_of_the_same_name_do_not_share_a_property()
    {
        var s = new FakeStore(); var t = new FakeMediaTable(); MediaTables.Attach(s, t); MediaTables.SetReads(s, true);
        await VideoJson.UpdateAsync(s, P, "u3", r => r["download"] = new JsonObject { ["bytes"] = 123, ["format"] = "mp4" }, default);
        await StageMarks.DoneAsync(t, "download", "u3", 1, "full", "n1", 2, null, default);   // the download stage's mark: property `download` on the upload row
        var row = (await t.GetAsync(MediaTables.Upload, "u3", null, default))!;
        Assert.Equal(123, (int)row.Section("s_download")!["bytes"]!);   // the section
        Assert.Equal(1, (int)row.Section("download")!["v"]!);           // the mark
        Assert.True(MediaRecords.IsFull(row));
        var doc = MediaRecords.DocOf(row);
        Assert.Equal(123, (int)doc["download"]!["bytes"]!); Assert.Null(doc["download"]!["v"]);   // the document's download section is the section, never the mark
        Assert.False(doc.ContainsKey("s_download"));
        VideoJson.ClearCache(s);
        Assert.Equal("mp4", (string?)(await VideoJson.ReadAsync(s, P, "u3", default))!.Root["download"]!["format"]);
    }

    [Fact]
    public async Task Table_deletes_are_refused_outside_a_selftest_scratch_partition()
    {
        var fake = new FakeMediaTable();
        await Assert.ThrowsAsync<InvalidOperationException>(() => fake.DeleteBatchAsync("upload", ["x"], default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => fake.DeleteBatchAsync("stage-cast", ["x"], default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => fake.DeleteBatchAsync("selftest-", ["x"], default));
        await fake.DeleteBatchAsync("selftest-abc", ["x"], default);
        using var http = new HttpClient(new NoHttp());   // the REST client refuses before any request
        var rest = new RestMediaTable(http, "https://acct.table.core.windows.net/media?sv=1&sig=abc", new StoreMeter());
        await Assert.ThrowsAsync<InvalidOperationException>(() => rest.DeleteBatchAsync("upload", ["x"], default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => rest.DeleteBatchAsync("actor", ["x"], default));
    }
    sealed class NoHttp : HttpMessageHandler { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct) => throw new Xunit.Sdk.XunitException("no request may be sent: " + r.Method + " " + r.RequestUri!.AbsolutePath); }

    [Fact]
    public void Whatif_says_when_this_build_cannot_make_the_required_version()
    {
        int code = StepVersions.Code("cast");
        var over = WhatIf.Run(new QueueState(), "cast", WhatIf.Apply(new ControlFile(), [$"versions.cast={code + 1}"]), "2026-11-23T00:00:00Z");
        Assert.Contains(over.Notes, n => n.Contains($"this build makes cast v{code}") && n.Contains($"v{code + 1} is required"));
        var at = WhatIf.Run(new QueueState(), "cast", WhatIf.Apply(new ControlFile(), [$"versions.cast={code}"]), "2026-11-23T00:00:00Z");
        Assert.DoesNotContain(at.Notes, n => n.Contains("this build makes"));
    }
}
