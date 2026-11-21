using System.Text;
using System.Text.Json.Nodes;
using IndexNode;
using Xunit;

namespace IndexNode.Tests;

public class EndingBackfillTests
{
    static readonly Paths P = new("index-test/");
    static string Tmp() => Directory.CreateTempSubdirectory("ending-").FullName;
    static byte[] B(string s) => Encoding.UTF8.GetBytes(s);

    sealed class Chat(Func<string, string> answer) : IChat
    {
        public readonly List<string> Prompts = [];
        public Task<string> ChatAsync(string model, JsonNode content, CancellationToken ct) { var p = content.GetValue<string>(); Prompts.Add(p); return Task.FromResult(answer(p)); }
    }

    static List<string> IdsIn(string prompt) => prompt.Split('\n').Where(l => l.StartsWith("=== drama ")).Select(l => l["=== drama ".Length..].Trim()).ToList();

    static string Answer(IEnumerable<string> ids, string kind = "cliffhanger") =>
        "{\"dramas\":[" + string.Join(",", ids.Select(i => $"{{\"id\":\"{i}\",\"ending\":{{\"kind\":\"{kind}\",\"signs\":[\"next episode teaser\"],\"nextPart\":true,\"nextPartHint\":\"Part 2 of {i}\"}}}}")) + "]}";

    /// <summary>A drama with describe.zip (describe.json + a second member), download.zip (info.json, description.txt) and transcribe.zip (a long VTT).</summary>
    static async Task Seed(FakeStore s, string dir, string id, bool withEnding = false)
    {
        async Task Zip(string stage, params (string, byte[])[] files)
        {
            var z = Path.Combine(dir, id + "-" + stage + ".zip");
            ZipFormat.Write(z, files.Select(f => new ZipFormat.Member(f.Item1, null, f.Item2)));
            await ((IStore)s).PutZipAsync(P.Video(id, stage + ".zip"), z, StageLayout.Metadata(ZipFormat.Sha256Of(z), "inputs-" + stage, "seed", "{}"), null, false, default);
        }
        var describe = new JsonObject { ["title"] = new JsonObject { ["en"] = "T " + id }, ["synopsis"] = new JsonObject { ["en"] = "She wins, then a stranger arrives." }, ["cast"] = new JsonArray(), ["aiGenerated"] = new JsonObject { ["level"] = "no", ["signs"] = new JsonArray() } };
        if (withEnding) describe["ending"] = new JsonObject { ["kind"] = "complete", ["signs"] = new JsonArray(), ["nextPart"] = false };
        await Zip("describe", ("describe.json", B(describe.ToJsonString())), ("describe.extra", B("keep me " + id)));
        await Zip("download", ("info.json", B($"{{\"title\":\"Upload {id}\",\"duration\":600,\"description\":\"desc {id}\"}}")), ("downloaded.json", B("{\"durationSeconds\":600}")));
        var vtt = new StringBuilder("WEBVTT\n\n");
        for (int i = 0; i < 60; i++) { int t = i * 10; vtt.Append($"00:{t / 60:D2}:{t % 60:D2}.000 --> 00:{t / 60:D2}:{t % 60:D2}.900\nline{i} of {id}\n\n"); }
        await Zip("transcribe", ("asr/" + Transcribe.ModelName + ".zh.vtt", B(vtt.ToString())));
    }

    static JsonObject DescribeOf(FakeStore s, string id)
    {
        var tmp = Path.Combine(Tmp(), "r.zip"); File.WriteAllBytes(tmp, s[P.Video(id, "describe.zip")].Data);
        var src = new FileZipSource(tmp);
        var e = ZipFormat.ReadDirectoryAsync(src, default).Result.First(x => x.Name == "describe.json");
        return (JsonObject)JsonNode.Parse(ZipFormat.ReadMemberAsync(src, e, default).Result)!;
    }

    static string Member(FakeStore s, string id, string name)
    {
        var tmp = Path.Combine(Tmp(), "m.zip"); File.WriteAllBytes(tmp, s[P.Video(id, "describe.zip")].Data);
        var src = new FileZipSource(tmp);
        var e = ZipFormat.ReadDirectoryAsync(src, default).Result.First(x => x.Name == name);
        return Encoding.UTF8.GetString(ZipFormat.ReadMemberAsync(src, e, default).Result);
    }

    [Fact]
    public void The_prompt_is_describe_7_and_asks_for_the_ending()
    {
        Assert.Equal("describe-9", Describe.PromptVersion);
        var d = new DescribeInput { JobId = "j", Title = "t", Duration = 100, Clusters = [] };
        var main = Describe.BuildMain(d, "m").Prompt;
        Assert.Contains("- ending:", main); Assert.Contains("\"ending\":{\"kind\":\"complete\",\"signs\":[],\"nextPart\":null,\"nextPartHint\":null}", main);
        Assert.Contains("cliffhanger", main); Assert.Contains("truncated", main); Assert.Contains("nextPart", main); Assert.Contains("nextPartHint", main);
    }

    [Fact]
    public void The_cleaner_keeps_known_kinds_and_nulls_the_rest()
    {
        var ok = Describe.CleanEnding(JsonNode.Parse("""{"kind":" Cliffhanger ","signs":["to be continued","  ","villain returns",3],"nextPart":true,"nextPartHint":"  第二部 "}"""))!;
        Assert.Equal("cliffhanger", (string)ok["kind"]!); Assert.True((bool)ok["nextPart"]!); Assert.Equal("第二部", (string)ok["nextPartHint"]!);   // trimmed
        Assert.Equal(new string('x', 80), (string)Describe.CleanEnding(JsonNode.Parse("{\"nextPartHint\":\"" + new string('x', 80) + "\"}"))!["nextPartHint"]!);
        foreach (var h in new[] { "\"" + new string('x', 81) + "\"", "\"  \"", "5", "[\"Part 2\"]", "null" }) Assert.Null(Describe.CleanEnding(JsonNode.Parse("{\"nextPartHint\":" + h + "}"))!["nextPartHint"]);   // too long (not cut), blank, not a string
        Assert.Equal(["to be continued", "villain returns", "3"], ok["signs"]!.AsArray().Select(x => (string)x!).ToArray());   // blank dropped, the word villain removed

        var bad = Describe.CleanEnding(JsonNode.Parse("""{"kind":"the end","signs":"x","nextPart":"yes"}"""))!;
        Assert.Null(bad["kind"]); Assert.Empty(bad["signs"]!.AsArray()); Assert.Null(bad["nextPart"]); Assert.Null(bad["nextPartHint"]);

        Assert.Equal(new[] { "complete", "truncated" }, new[] { "complete", "truncated" }.Select(k => (string?)Describe.CleanEnding(JsonNode.Parse($"{{\"kind\":\"{k}\"}}"))!["kind"]).ToArray());
        Assert.Null(Describe.CleanEnding(null)); Assert.Null(Describe.CleanEnding(JsonNode.Parse("\"complete\"")));
        Assert.Equal(6, Describe.CleanEnding(JsonNode.Parse("""{"kind":"complete","signs":["a","b","c","d","e","f","g","h"]}"""))!["signs"]!.AsArray().Count);
    }

    [Fact]
    public void Normalize_puts_the_cleaned_ending_in_the_result()
    {
        var d = new DescribeInput { JobId = "j", Duration = 100, Clusters = [] };
        var r = Describe.Normalize(JsonNode.Parse("""{"title":{"en":"t"},"ending":{"kind":"weird","signs":["x"],"nextPart":false,"nextPartHint":"Part 2"}}""")!.AsObject(), d);
        Assert.Null(r["ending"]!["kind"]); Assert.False((bool)r["ending"]!["nextPart"]!); Assert.Equal("Part 2", (string)r["ending"]!["nextPartHint"]!);
        Assert.Null(Describe.Normalize(JsonNode.Parse("""{"title":{"en":"t"}}""")!.AsObject(), d)["ending"]);
    }

    [Fact]
    public void Publish_carries_the_ending_into_drama_json_and_packs_json()
    {
        var d = JsonNode.Parse("""{"cutoffSeconds":10,"title":{"en":"t"},"fullTitle":{"en":"t"},"teaser":{"en":"t"},"synopsis":{"en":"s"},"tags":[],"cast":[],"relations":[],"aiGenerated":{"level":"no","signs":[]},"ending":{"kind":"cliffhanger","signs":["to be continued"],"nextPart":true,"nextPartHint":"EP 5"}}""")!.AsObject();
        var o = Publish.BuildDrama(d, null, null, null, null, "j", "transcript", false, new Dictionary<int, List<(string, double, bool)>>(), "t");
        Assert.Equal("cliffhanger", (string)o["ending"]!["kind"]!); Assert.True((bool)o["ending"]!["nextPart"]!); Assert.Equal("EP 5", (string)o["ending"]!["nextPartHint"]!); Assert.Null(o["aiGenerated"]); Assert.Null(o["ai"]);
        var noEnding = JsonNode.Parse(d.ToJsonString())!.AsObject(); noEnding.Remove("ending");
        Assert.Null(Publish.BuildDrama(noEnding, null, null, null, null, "j", "transcript", false, new Dictionary<int, List<(string, double, bool)>>(), "t")["ending"]);

        var outDir = Tmp(); Directory.CreateDirectory(Path.Combine(outDir, "j"));
        File.WriteAllText(Path.Combine(outDir, "j", "drama.json"), o.ToJsonString());
        Publish.WriteIndex(outDir);
        Assert.Equal("cliffhanger", (string)JsonNode.Parse(File.ReadAllText(Path.Combine(outDir, "packs.json")))!["packs"]![0]!["ending"]!["kind"]!);
        Assert.Equal("EP 5", (string)JsonNode.Parse(File.ReadAllText(Path.Combine(outDir, "packs.json")))!["packs"]![0]!["ending"]!["nextPartHint"]!);
    }

    [Fact]
    public void The_tail_keeps_only_the_last_three_minutes()
    {
        var cues = Enumerable.Range(0, 60).Select(i => new ShapeCue(i * 10, i * 10 + 5, "c" + i)).ToList();   // 0..595 s
        var tail = EndingBackfill.Tail(cues, 600);
        Assert.Contains("c59", tail); Assert.Contains("c42", tail); Assert.DoesNotContain("c10", tail); Assert.DoesNotContain("c41 ", tail + " ");
        Assert.Equal("", EndingBackfill.Tail([], 600));
    }

    static string WriteTemp(byte[] data) { var p = Path.Combine(Tmp(), "x.zip"); File.WriteAllBytes(p, data); return p; }

    [Fact]
    public async Task Backfill_batches_the_dramas_tolerates_an_omitted_answer_and_rewrites_describe_zip()
    {
        var s = new FakeStore(); var dir = Tmp();
        foreach (var id in new[] { "aaa", "bbb", "ccc", "ddd" }) await Seed(s, dir, id);
        await Seed(s, dir, "eee", withEnding: true);   // already done: skipped, not asked
        var etagBefore = (await ((IStore)s).HeadAsync(P.Video("bbb", "describe.zip"), default))!.ETag;
        var metaBefore = new Dictionary<string, string>(s[P.Video("bbb", "describe.zip")].Meta);
        // first call answers all but "ccc"; the follow-up for ccc (asked alone) answers it
        var chat = new Chat(p => { var ids = IdsIn(p); return Answer(ids.Where(i => i != "ccc" || ids.Count == 1)); });

        var (rep, _) = await EndingBackfill.RunAsync(s, P, chat, "m", "node-x", Path.Combine(dir, "work"), null, false, default);

        Assert.Equal(new EndingBackfill.Report(5, 4, 4, 1, 0, 0, 2), rep);
        Assert.Equal(2, chat.Prompts.Count);   // 4 dramas in ONE call, then only the omitted one again
        Assert.Equal(["aaa", "bbb", "ccc", "ddd"], IdsIn(chat.Prompts[0])); Assert.Equal(["ccc"], IdsIn(chat.Prompts[1]));
        var p0 = chat.Prompts[0];
        Assert.Contains("Upload aaa", p0); Assert.Contains("desc aaa", p0); Assert.Contains("a stranger arrives", p0);   // title, description, synopsis
        Assert.Contains("line59 of aaa", p0); Assert.DoesNotContain("line10 of aaa", p0);   // only the tail of the transcript
        Assert.DoesNotContain("drama eee", p0);

        foreach (var id in new[] { "aaa", "bbb", "ccc", "ddd" })
        {
            var d = DescribeOf(s, id);
            Assert.Equal("cliffhanger", (string)d["ending"]!["kind"]!); Assert.True((bool)d["ending"]!["nextPart"]!); Assert.Equal("Part 2 of " + id, (string)d["ending"]!["nextPartHint"]!);   // the backfill fills the hint too
            Assert.Equal("T " + id, (string)d["title"]!["en"]!);   // the rest of describe.json is untouched
            Assert.Equal("keep me " + id, Member(s, id, "describe.extra"));   // other members survive
        }
        var after = s[P.Video("bbb", "describe.zip")];
        Assert.NotEqual(etagBefore, (await ((IStore)s).HeadAsync(P.Video("bbb", "describe.zip"), default))!.ETag);
        Assert.Equal(metaBefore["idx_inputs"], after.Meta["idx_inputs"]); Assert.Equal("1", after.Meta["idx_v"]);
        Assert.Equal(ZipFormat.Sha256Of(WriteTemp(after.Data)), after.Meta["idx_sha256"]);
        Assert.NotEqual(metaBefore["idx_sha256"], after.Meta["idx_sha256"]);
        Assert.Equal("node-x", after.Meta["idx_node"]);
        Assert.Equal(0, s.ZipConflicts);

        // a second run finds nothing to do and asks nothing
        var chat2 = new Chat(_ => throw new InvalidOperationException("must not be called"));
        var (rep2, _) = await EndingBackfill.RunAsync(s, P, chat2, "m", "n", Path.Combine(dir, "work"), null, false, default);
        Assert.Equal((5, 0, 0, 5), (rep2.Found, rep2.Asked, rep2.Written, rep2.Skipped)); Assert.Empty(chat2.Prompts);
    }

    [Fact]
    public async Task A_drama_the_model_never_answers_is_left_untouched_for_the_next_run()
    {
        var s = new FakeStore(); var dir = Tmp();
        foreach (var id in new[] { "aaa", "bbb" }) await Seed(s, dir, id);
        var before = s[P.Video("bbb", "describe.zip")].Data.ToArray();
        var chat = new Chat(p => Answer(IdsIn(p).Where(i => i != "bbb")));
        var (rep, _) = await EndingBackfill.RunAsync(s, P, chat, "m", "n", Path.Combine(dir, "w"), null, false, default);
        Assert.Equal(2, rep.Asked); Assert.Equal(1, rep.Written); Assert.Equal(1, rep.Unanswered);
        Assert.Equal(before, s[P.Video("bbb", "describe.zip")].Data);   // not written
        Assert.NotNull(DescribeOf(s, "aaa")["ending"]);
        Assert.Null(DescribeOf(s, "bbb")["ending"]);
        Assert.Equal(["bbb"], IdsIn(chat.Prompts[^1]));   // asked again alone
    }

    [Fact]
    public async Task A_garbled_reply_for_one_drama_does_not_fail_the_run()
    {
        var s = new FakeStore(); var dir = Tmp(); await Seed(s, dir, "aaa");
        var chat = new Chat(p => "{\"dramas\":[{\"id\":\"aaa\",\"ending\":{\"kind\":\"???\",\"signs\":7,\"nextPart\":\"maybe\",\"nextPartHint\":12}},{\"id\":\"zzz\",\"ending\":{\"kind\":\"complete\"}}]}");
        var (rep, _) = await EndingBackfill.RunAsync(s, P, chat, "m", "n", Path.Combine(dir, "w"), null, false, default);
        Assert.Equal(1, rep.Written);
        var e = DescribeOf(s, "aaa")["ending"]!; Assert.Null(e["kind"]); Assert.Null(e["nextPart"]); Assert.Null(e["nextPartHint"]); Assert.Empty(e["signs"]!.AsArray());   // unknown kinds become null
        Assert.False(s.Exists(P.Video("zzz", "describe.zip")));
    }

    [Fact]
    public async Task A_zip_that_changed_while_the_model_answered_is_not_overwritten()
    {
        var s = new FakeStore(); var dir = Tmp(); await Seed(s, dir, "aaa");
        s.BeforeIfMatch = n => { s.Blobs[n].Version++; };   // another node rewrote it: the ETag moves (runs inside the store's lock)
        var chat = new Chat(p => Answer(IdsIn(p)));
        var (rep, _) = await EndingBackfill.RunAsync(s, P, chat, "m", "n", Path.Combine(dir, "w"), null, false, default);
        Assert.Equal((0, 1), (rep.Written, rep.Raced)); Assert.Equal(1, s.ZipConflicts);
        Assert.Null(DescribeOf(s, "aaa")["ending"]);
    }

    [Fact]
    public async Task Dry_run_n_looks_at_n_dramas_prints_a_prompt_and_neither_asks_nor_writes()
    {
        var s = new FakeStore(); var dir = Tmp();
        foreach (var id in new[] { "aaa", "bbb", "ccc" }) await Seed(s, dir, id);
        var puts = s.ZipPuts;
        var (rep, prompt) = await EndingBackfill.RunAsync(s, P, new Chat(_ => throw new InvalidOperationException("no AI in a dry run")), "m", "n", Path.Combine(dir, "w"), 2, true, default);
        Assert.Equal(2, rep.Asked); Assert.Equal(0, rep.Written); Assert.Equal(0, rep.Calls); Assert.Equal(puts, s.ZipPuts);
        Assert.Equal(["aaa", "bbb"], IdsIn(prompt!));
        Assert.All(new[] { "aaa", "bbb", "ccc" }, id => Assert.Null(DescribeOf(s, id)["ending"]));
    }

    [Fact]
    public async Task The_store_default_for_a_conditional_zip_put_checks_the_etag()
    {
        var s = new FakeStore(); var dir = Tmp(); await Seed(s, dir, "aaa");
        var name = P.Video("aaa", "describe.zip"); var z = Path.Combine(dir, "n.zip"); ZipFormat.Write(z, [new("describe.json", null, B("{}"))]);
        var etag = (await ((IStore)s).HeadAsync(name, default))!.ETag!;
        var none = new Dictionary<string, string>();
        Assert.False(await s.PutZipIfMatchAsync(name, z, none, null, "\"stale\"", default));
        Assert.True(await s.PutZipIfMatchAsync(name, z, none, null, etag, default));
        Assert.False(await s.PutZipIfMatchAsync(name, z, none, null, etag, default));   // the old ETag no longer matches
        Assert.False(await s.PutZipIfMatchAsync(P.Video("nope", "describe.zip"), z, none, null, etag, default));
    }
}
