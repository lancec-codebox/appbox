using System.Text;
using System.Text.Json.Nodes;

namespace IndexNode.Tests;

/// <summary>0.13.2: video.json, the per-video descriptor: the lease holder's conditional merge, the backfill, the catalog built from it, and its loud failures.</summary>
[Collection("console")]
public class VideoJsonTests
{
    static readonly Paths P = new("index/");
    const string Id = "abcdefghij1";

    static async Task<JsonObject> Read(FakeStore s, string id = Id) => (await VideoJson.RequireAsync(s, P, id, default)).Root;

    [Fact]
    public async Task The_first_write_creates_video_json_and_a_412_reads_again_and_merges_with_what_the_other_writer_added()
    {
        var s = new FakeStore();
        await VideoJson.UpdateAsync(s, P, Id, r => r["youtube"] = new JsonObject { ["title"] = "T", ["durationSeconds"] = 600 }, default);   // create (If-None-Match)
        Assert.Equal(1, (int)(await Read(s))["v"]!); Assert.Equal(Id, (string)(await Read(s))["id"]!);
        // a write sneaks in between our read and our conditional write: the 412 makes us read again and merge on top of it
        int merges = 0;
        s.BeforePutIfMatch = name => VideoJson.UpdateAsync(s, P, Id, r => r["speech"] = new JsonObject { ["language"] = "zh", ["source"] = "transcribe" }, default).GetAwaiter().GetResult();
        await VideoJson.UpdateAsync(s, P, Id, r => { merges++; r["ai"] = new JsonObject { ["score"] = 0.2, ["flag"] = false, ["source"] = "centers" }; }, default);
        Assert.Equal(2, merges);   // merged twice: once on the stale read, once after the 412
        var root = await Read(s);
        Assert.Equal("T", (string)root["youtube"]!["title"]!); Assert.Equal("zh", (string)root["speech"]!["language"]!); Assert.Equal(0.2, (double)root["ai"]!["score"]!);   // nothing the other writer added was lost
    }

    [Fact]
    public async Task A_create_loses_to_an_existing_file_and_merges_into_it_instead_of_replacing_it()
    {
        var s = new FakeStore();
        s.BeforePutIfMatch = _ => VideoJsonSeed.PutAsync(s, P, Id, 900, "en", true).GetAwaiter().GetResult();   // someone created it first
        await VideoJson.UpdateAsync(s, P, Id, r => r["precheck"] = new JsonObject { ["decision"] = "new" }, default);
        var root = await Read(s); Assert.Equal("new", (string)root["precheck"]!["decision"]!); Assert.Equal("en", (string)root["speech"]!["language"]!); Assert.Equal(900, VideoJson.Duration(root));
    }

    [Fact]
    public async Task A_missing_video_json_fails_loudly_and_an_unreadable_one_too()
    {
        var s = new FakeStore();
        await Assert.ThrowsAsync<VideoJsonMissingException>(() => VideoJson.RequireAsync(s, P, Id, default));
        Assert.Null(await VideoJson.ReadAsync(s, P, Id, default));
        await s.PutAsync(P.Video(Id, "video.json"), Encoding.UTF8.GetBytes("{broken"), null, null, false, default);
        await Assert.ThrowsAsync<InvalidDataException>(() => VideoJson.ReadAsync(s, P, Id, default));
        await s.PutAsync(P.Video(Id, "video.json"), Encoding.UTF8.GetBytes("[1]"), null, null, false, default);
        await Assert.ThrowsAsync<InvalidDataException>(() => VideoJson.ReadAsync(s, P, Id, default));
    }

    [Fact]
    public async Task The_pre_check_record_goes_into_video_json_with_its_speech_and_ai_facts_and_no_precheck_json_is_written()
    {
        var s = new FakeStore(); await VideoJsonSeed.PutAsync(s, P, Id, 600);
        var rec = JsonNode.Parse("{\"decision\":\"new\",\"speech\":{\"speechLanguage\":\"zh\",\"dubbed\":false},\"ai\":{\"score\":0.31,\"flag\":false}}")!.AsObject();
        await PreCheckStage.RecordAsync(s, P, Id, rec, default);
        var root = await Read(s);
        Assert.Equal("new", (string)root["precheck"]!["decision"]!); Assert.Equal("zh", (string)root["speech"]!["language"]!); Assert.Equal("precheck", (string)root["speech"]!["source"]!); Assert.Equal(0.31, (double)root["ai"]!["score"]!);
        Assert.Equal(600, VideoJson.Duration(root)); Assert.False(s.Exists(P.Video(Id, "precheck.json")));
    }

    // ------------------------------------------------------------------ backfill
    static async Task Legacy(FakeStore s, string id, double? duration, bool precheck = false, bool info = true)
    {
        await s.PutAsync(P.Video(id, "video.mp4"), [1, 2], null, null, false, default);
        await s.PutAsync(P.Video(id, "download.zip"), [1], null, null, false, default);
        if (duration != null) await s.SetMetadataAsync(P.Video(id, "download.zip"), new Dictionary<string, string> { ["idx_summary"] = "{\"durationSeconds\":" + duration.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) + ",\"bytes\":5}" }, null, default);
        if (info) await s.PutAsync(P.Video(id, "info.json"), Encoding.UTF8.GetBytes("{\"title\":\"Title " + id + "\",\"channel\":\"Chan\",\"channel_id\":\"UC1\",\"upload_date\":\"20261101\",\"language\":\"en\"}"), null, null, false, default);
        if (precheck) await s.PutAsync(P.Video(id, "precheck.json"), Encoding.UTF8.GetBytes("{\"decision\":\"new\",\"at\":\"2026-11-02T00:00:00Z\",\"video\":{\"duration\":777},\"speech\":{\"speechLanguage\":\"zh\",\"dubbed\":false},\"ai\":{\"score\":0.3,\"flag\":false}}"), null, null, false, default);
    }

    [Fact]
    public async Task Backfill_dry_run_counts_writes_nothing_then_creates_from_idx_summary_info_and_precheck_and_never_overwrites()
    {
        var s = new FakeStore();
        await Legacy(s, "aaaaaaaaaa1", 1800); await Legacy(s, "bbbbbbbbbb2", 1500, precheck: true); await Legacy(s, "cccccccccc3", null, precheck: true, info: false);   // c3: no idx_summary; precheck.json knows 777 s
        await VideoJsonSeed.PutAsync(s, P, "dddddddddd4", 42); await s.PutAsync(P.Video("dddddddddd4", "video.mp4"), [1], null, null, false, default);   // already has one
        await s.PutAsync(P.Video("commentsonly", "comments.zip"), [1], null, null, false, default);   // not an upload
        var before = s.Blobs.Count;
        var dry = await VideosBackfill.BackfillAsync(s, P, true, 0.479, 2, null, default);
        Assert.Equal((4, 1, 3, 0), (dry.Videos, dry.Have, dry.Created, dry.Claimed)); Assert.Equal(before, s.Blobs.Count); Assert.False(s.Exists(P.Video("aaaaaaaaaa1", "video.json")));
        var rep = await VideosBackfill.BackfillAsync(s, P, false, 0.479, 2, null, default);
        Assert.Equal((4, 1, 3, 0, 0), (rep.Videos, rep.Have, rep.Created, rep.Claimed, rep.Raced)); Assert.Empty(rep.Failed);
        var a = await Read(s, "aaaaaaaaaa1");
        Assert.Equal(1800, VideoJson.Duration(a)); Assert.Equal("Title aaaaaaaaaa1", (string)a["youtube"]!["title"]!); Assert.Equal("en", (string)a["youtube"]!["language"]!); Assert.Equal(5, (int)a["download"]!["bytes"]!); Assert.Null(a["precheck"]);
        var b = await Read(s, "bbbbbbbbbb2");
        Assert.Equal(1500, VideoJson.Duration(b)); Assert.Equal("new", (string)b["precheck"]!["decision"]!); Assert.Equal("zh", (string)b["speech"]!["language"]!); Assert.Equal("precheck", (string)b["speech"]!["source"]!); Assert.Equal(0.3, (double)b["ai"]!["score"]!);
        Assert.Equal(777, VideoJson.Duration(await Read(s, "cccccccccc3")));
        Assert.Equal(42, VideoJson.Duration(await Read(s, "dddddddddd4")));   // create-only: the existing one is untouched
        var again = await VideosBackfill.BackfillAsync(s, P, false, 0.479, 2, null, default); Assert.Equal((4, 4, 0), (again.Videos, again.Have, again.Created));   // idempotent
    }

    [Fact]
    public async Task Backfill_is_create_only_even_when_a_claim_writes_the_descriptor_between_the_listing_and_the_write()
    {
        var s = new FakeStore(); await Legacy(s, "aaaaaaaaaa1", 1800);
        s.BeforePutIfMatch = _ => VideoJsonSeed.PutAsync(s, P, "aaaaaaaaaa1", 5).GetAwaiter().GetResult();   // the claim's write lands first
        var rep = await VideosBackfill.BackfillAsync(s, P, false, 0.479, 1, null, default);
        Assert.Equal((0, 1), (rep.Created, rep.Raced)); Assert.Equal(5, VideoJson.Duration(await Read(s, "aaaaaaaaaa1")));
    }

    [Fact]
    public async Task Backfill_skips_and_reports_a_video_under_an_active_claim_and_reports_one_with_no_duration_anywhere_as_failed()
    {
        var s = new FakeStore(); await Legacy(s, "aaaaaaaaaa1", 1800); await Legacy(s, "bbbbbbbbbb2", 1500); await Legacy(s, "nodurationn1", null, info: false);
        await s.PutAsync(P.Active("bbbbbbbbbb2"), Encoding.UTF8.GetBytes("{}"), null, null, false, default);
        Assert.NotNull(await s.AcquireLeaseAsync(P.Active("bbbbbbbbbb2"), 60, default));   // a live lease: a node is working on it
        var rep = await VideosBackfill.BackfillAsync(s, P, false, 0.479, 1, null, default);
        Assert.Equal((3, 2, 1), (rep.Videos, rep.Created, rep.Claimed)); Assert.Equal(["bbbbbbbbbb2"], rep.ClaimedIds);
        Assert.False(s.Exists(P.Video("bbbbbbbbbb2", "video.json"))); Assert.True(s.Exists(P.Video("aaaaaaaaaa1", "video.json")));   // its claim writes it
        Assert.Empty(rep.Failed); var nd = await Read(s, "nodurationn1"); Assert.Null(VideoJson.Duration(nd)); Assert.Equal("unknown", (string?)nd["youtube"]!["durationSource"]);   // 0.13.3: recorded as unknown, not failed
    }

    // ------------------------------------------------------------------ the catalog from video.json
    static SearchUpload Up(string id, string? lang = null, bool? dub = null) => new(id, "s", "e", 1, 0, 1, 1, 1, 0, lang, dub);

    [Fact]
    public async Task The_catalog_is_built_from_video_json_alone_reuses_complete_entries_and_reads_nothing_else()
    {
        var s = new FakeStore();
        await VideoJsonSeed.PutAsync(s, P, "proc0000001", 900, "en", true, title: "Processed", channel: "Chan", aiScore: 0.2, aiFlag: false);
        var dup = new JsonObject { ["decision"] = "duplicate", ["matchUpload"] = "proc0000001", ["top"] = new JsonArray(new JsonObject { ["upload"] = "proc0000001", ["share"] = 0.9, ["bestCosine"] = 0.8, ["bestTime"] = 3.5 }) };
        await VideoJsonSeed.PutAsync(s, P, "dupe0000001", 880, "zh", false, dup, title: "Dup", aiScore: 0.1, aiFlag: false);
        await VideoJsonSeed.PutAsync(s, P, "held0000001", 300, "zh", false, new JsonObject { ["decision"] = "new" }, aiScore: 0.7, aiFlag: true);
        await VideoJsonSeed.PutAsync(s, P, "dlonly00001", 100);   // downloaded, not pre-checked, not indexed: not in the catalog
        s.Calls.Clear(); int heads = s.Ops.GetValueOrDefault("head");
        var cat = await CatalogBuild.BuildAsync(s, P, 1, [Up("proc0000001")], null, 0.479, null, default);
        Assert.Equal(["dupe0000001", "held0000001", "proc0000001"], cat.Uploads.Select(u => u.Id).OrderBy(x => x, StringComparer.Ordinal));
        var p = cat.Find("proc0000001")!; Assert.Equal((900.0, "en", true, "Processed", "Chan", "full"), (p.DurationSeconds, p.SpeechLanguage, p.Dubbed, p.Title, p.Channel, p.Data)); Assert.Equal(0.2, p.Ai!.Score);
        var d = cat.Find("dupe0000001")!; Assert.Equal(("duplicate", "proc0000001", 0.9, 3.5, 0.8, "precheck"), (d.Decision, d.MatchUpload, d.MatchShare, d.MatchOffset, d.MatchCosine, d.CheckedBy)); Assert.Null(d.Held);
        Assert.Equal("ai", cat.Find("held0000001")!.Held);
        Assert.Equal(heads, s.Ops.GetValueOrDefault("head"));   // no HEAD of a zip
        // a complete entry of the previous version is reused: its video.json is not read again (it may be gone)
        s.Blobs.Remove(P.Video("proc0000001", "video.json"));
        var next = await CatalogBuild.BuildAsync(s, P, 2, [Up("proc0000001", "en", true)], cat, 0.479, null, default);
        Assert.Equal(900, next.Find("proc0000001")!.DurationSeconds);
    }

    [Fact]
    public async Task A_missing_or_unreadable_video_json_marks_the_entry_missing_and_the_catalog_build_goes_on()
    {
        var s = new FakeStore(); await VideoJsonSeed.PutAsync(s, P, "good0000001", 900, "en", true, aiScore: 0.2, aiFlag: false);
        await s.PutAsync(P.Video("bad00000001", "video.json"), Encoding.UTF8.GetBytes("{nope"), null, null, false, default);
        var cat = await CatalogBuild.BuildAsync(s, P, 1, [Up("good0000001", "en", true), Up("gone0000001", "zh", false), Up("bad00000001")], null, 0.479, null, default);
        Assert.Equal(["bad00000001", "gone0000001"], cat.Missing);
        Assert.Equal("missing", cat.Find("gone0000001")!.Data); Assert.Equal("missing", cat.Find("bad00000001")!.Data); Assert.Null(cat.Find("gone0000001")!.DurationSeconds);
        Assert.Equal("full", cat.Find("good0000001")!.Data);
        // the entry is complete again at the next build, once video.json exists (a missing entry is never reused)
        await VideoJsonSeed.PutAsync(s, P, "gone0000001", 500, "zh", false, aiScore: 0.1, aiFlag: false); s.Blobs.Remove(P.Video("bad00000001", "video.json"));
        var next = await CatalogBuild.BuildAsync(s, P, 2, [Up("good0000001", "en", true), Up("gone0000001", "zh", false)], cat, 0.479, null, default);
        Assert.Empty(next.Missing); Assert.Equal((500.0, "full"), (next.Find("gone0000001")!.DurationSeconds, next.Find("gone0000001")!.Data));
    }
}
