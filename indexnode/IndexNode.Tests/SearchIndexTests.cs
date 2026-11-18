using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using IndexNode;
using Xunit;
using Xunit.Abstractions;

namespace IndexNode.Tests;

public class SearchIndexTests(ITestOutputHelper output)
{
    static string Tmp() => Directory.CreateTempSubdirectory("search-").FullName;

    static JsonObject Drama(string id, bool full = true, int cast = 3)
    {
        var d = JsonNode.Parse("""{"cutoffSeconds":10,"title":{"en":"Reborn Sisters","zh":"重生姐妹"},"fullTitle":{"en":"Reborn Sisters: She Marries the Border Guard"},"teaser":{"en":"Two sisters wake up in the past."},"synopsis":{"en":"Reborn with her sister, Lin Wan marries the border guard Shi An and changes their fate across the frontier."},"tags":["rebirth","romance"],"era":"ancient","fantasy":true,"aiGenerated":{"level":"partly","signs":["voice"]},"ending":{"kind":"cliffhanger","signs":["to be continued"],"nextPart":true,"nextPartHint":"Part 2"},"cast":[],"relations":[]}""")!.AsObject();
        var arr = new JsonArray();
        for (int i = 0; i < cast; i++) arr.Add(JsonNode.Parse($$"""{"cluster":{{i}},"character":{"zh":"石安{{i}}","pinyin":"Shi An {{i}}","en":"Shi An {{i}}"},"role":"male lead","roleAtStart":"male lead","description":"A border guard who keeps a long and detailed secret about the past.","label":{"en":"border guard"},"actor":null}""")!);
        d["cast"] = arr;
        if (!full) { d.Remove("ending"); d.Remove("era"); d["fullTitle"] = new JsonObject(); d["title"]!.AsObject().Remove("zh"); }
        var o = Publish.BuildDrama(d, JsonNode.Parse("{\"id\":\"yt" + id + "\",\"channel\":\"Drama Channel\",\"channel_id\":\"UC123\",\"duration\":1800}")!.AsObject(), null, null, null, id, "transcript", false, new Dictionary<int, List<(string, double, bool)>>(), "t");
        return o;
    }

    static void Put(string outDir, string id, JsonObject drama) { Directory.CreateDirectory(Path.Combine(outDir, id)); File.WriteAllText(Path.Combine(outDir, id, "drama.json"), drama.ToJsonString()); }

    [Fact]
    public void Search_json_has_every_field_and_no_urls_or_pictures()
    {
        var outDir = Tmp(); Put(outDir, "p1", Drama("p1"));
        Publish.WriteIndex(outDir);
        var raw = File.ReadAllText(Path.Combine(outDir, "search.json"));
        Assert.DoesNotContain("\n", raw); Assert.DoesNotContain("  ", raw); Assert.DoesNotContain("http", raw); Assert.DoesNotContain("portrait", raw); Assert.DoesNotContain("stills", raw); Assert.DoesNotContain("relations", raw);
        var s = JsonNode.Parse(raw)!; Assert.Equal(1, (int)s["v"]!); Assert.True(DateTime.TryParse((string)s["builtAt"]!, out _));
        var e = s["dramas"]![0]!;
        Assert.Equal("p1", (string)e["packId"]!); Assert.Equal("ytp1", (string)e["videos"]![0]!);
        Assert.Equal("Reborn Sisters", (string)e["title"]!["en"]!); Assert.Equal("重生姐妹", (string)e["title"]!["zh"]!); Assert.Contains("Border Guard", (string)e["fullTitle"]!["en"]!);
        Assert.Equal("Two sisters wake up in the past.", (string)e["teaser"]!["en"]!); Assert.Contains("Shi An", (string)e["synopsis"]!["en"]!);
        Assert.Equal("rebirth", (string)e["tags"]![0]!); Assert.Equal("ancient", (string)e["era"]!); Assert.True((bool)e["fantasy"]!); Assert.Equal("partly", (string)e["ai"]!);
        Assert.Equal("cliffhanger", (string)e["ending"]!["kind"]!); Assert.True((bool)e["ending"]!["nextPart"]!); Assert.Equal("Part 2", (string)e["ending"]!["nextPartHint"]!);
        var c = e["cast"]![0]!; Assert.Equal("Shi An 0", (string)c["en"]!); Assert.Equal("石安0", (string)c["zh"]!); Assert.Equal("Shi An 0", (string)c["pinyin"]!); Assert.Equal("male lead", (string)c["role"]!); Assert.Equal("border guard", (string)c["label"]!);
        Assert.Equal(1800, (double)e["durationSeconds"]!); Assert.Equal("UC123", (string)e["channel"]!["id"]!); Assert.Equal("Drama Channel", (string)e["channel"]!["name"]!);
        Assert.Equal(3, e["cast"]!.AsArray().Count);
    }

    [Fact]
    public void Missing_optional_fields_come_out_as_null()
    {
        var outDir = Tmp(); Put(outDir, "p1", Drama("p1", full: false));
        var j = Drama("p2", full: false); j.Remove("channelId"); j["channel"] = null; j["cast"] = new JsonArray(); j["tags"] = null; j.Remove("fantasy"); j.Remove("aiGenerated"); Put(outDir, "p2", j);
        Publish.WriteIndex(outDir);
        var d = JsonNode.Parse(File.ReadAllText(Path.Combine(outDir, "search.json")))!["dramas"]!;
        var e = d[0]!; Assert.Null(e["ending"]); Assert.Null(e["era"]); Assert.Null(e["title"]!["zh"]); Assert.Null(e["fullTitle"]!["en"]); Assert.NotNull(e["title"]!["en"]);
        var e2 = d[1]!; Assert.Null(e2["channel"]); Assert.Null(e2["tags"]); Assert.Null(e2["ai"]); Assert.Empty(e2["cast"]!.AsArray());
    }

    [Fact]
    public void The_order_is_by_pack_id_and_the_index_is_rebuilt_from_scratch_each_publish()
    {
        var outDir = Tmp();
        foreach (var id in new[] { "b2", "a1", "c3" }) Put(outDir, id, Drama(id));
        Publish.WriteIndex(outDir);
        string[] Ids() => JsonNode.Parse(File.ReadAllText(Path.Combine(outDir, "search.json")))!["dramas"]!.AsArray().Select(x => (string)x!["packId"]!).ToArray();
        Assert.Equal(["a1", "b2", "c3"], Ids());
        // stale content is not kept: remove a pack, change another, add one, and add a stray entry to search.json
        File.WriteAllText(Path.Combine(outDir, "search.json"), """{"v":1,"builtAt":"x","dramas":[{"packId":"zzz"}]}""");
        Directory.Delete(Path.Combine(outDir, "b2"), true);
        var changed = Drama("a1"); changed["teaser"]!["en"] = "New teaser."; Put(outDir, "a1", changed); Put(outDir, "0z", Drama("0z"));
        Publish.WriteIndex(outDir);
        Assert.Equal(["0z", "a1", "c3"], Ids());
        Assert.Equal("New teaser.", (string)JsonNode.Parse(File.ReadAllText(Path.Combine(outDir, "search.json")))!["dramas"]![1]!["teaser"]!["en"]!);
        Assert.Equal(3, JsonNode.Parse(File.ReadAllText(Path.Combine(outDir, "packs.json")))!["packs"]!.AsArray().Count);
    }

    [Fact]
    public async Task Publish_reads_a_drama_from_a_store_that_has_only_stage_zips()
    {
        // 0.10.0 layout: describe.zip + download.zip (info.json, summary in its metadata); no loose files, no processed.json, no downloaded.json
        var fs = new FakeStore(); var tmp = Tmp(); var P = new Paths("index/");
        var describe = JsonNode.Parse("""{"v":1,"promptVersion":"describe-7","cutoffSeconds":36,"title":{"en":"Reborn","zh":"重生"},"fullTitle":{"en":"Reborn Full"},"teaser":{"en":"A premise."},"synopsis":{"en":"Shi An guards the border."},"tags":["rebirth"],"era":"ancient","fantasy":true,"aiGenerated":{"level":"no","signs":[]},"ending":{"kind":"complete","signs":[],"nextPart":false,"nextPartHint":null},"cast":[],"relations":[]}""")!.ToJsonString();
        async Task Zip(string stage, string member, string json, string summary)
        {
            var z = Path.Combine(tmp, stage + ".zip"); ZipFormat.Write(z, [new(member, null, Encoding.UTF8.GetBytes(json))]);
            var meta = StageLayout.Metadata("sha-" + stage, "-", "n1", summary); meta["idx_at"] = "2026-11-17T10:00:00Z";
            await fs.PutZipAsync(P.Video("rjzV__OHNR0", stage + ".zip"), z, meta, null, false, default);
        }
        await Zip("describe", "describe.json", describe, "{}");
        await Zip("download", "info.json", """{"id":"rjzV__OHNR0","title":"原标题","channel":"Chan","channel_id":"UCx","duration":300}""", """{"durationSeconds":300.0,"embeddable":true}""");
        var store = StageZipStore.Wrap(fs, Path.Combine(tmp, "cache"));
        Assert.DoesNotContain(fs.Names("index/videos/rjzV__OHNR0/"), n => n.EndsWith(".json")); // stage zips only
        var found = (await store.ListAsync("index/videos/", false, false, default)).Select(b => b.Name).ToList();
        Assert.Contains("index/videos/rjzV__OHNR0/describe.zip", found);
        Assert.Equal("2026-11-17T10:00:00Z", (await store.HeadAsync(P.Video("rjzV__OHNR0", "describe.zip"), default))!.Metadata["idx_at"]);
        var outDir = Tmp(); var tools = new Tools(new HttpClient(), new NodeConfig());
        var r = await Publish.PublishOneAsync(tools, new StoreJob(store, P, "rjzV__OHNR0"), outDir, false, default);
        Assert.True(r.Written, r.Skipped);
        var drama = JsonNode.Parse(File.ReadAllText(Path.Combine(outDir, "rjzV__OHNR0", "drama.json")))!;
        Assert.Equal("Reborn", (string)drama["title"]!["en"]!); Assert.Equal(300, (double)drama["durationSeconds"]!); Assert.True((bool)drama["embeddable"]!); Assert.Equal("Chan", (string)drama["channel"]!);
        Publish.WriteIndex(outDir);
        var e = JsonNode.Parse(File.ReadAllText(Path.Combine(outDir, "search.json")))!["dramas"]![0]!;
        Assert.Equal("rjzV__OHNR0", (string)e["packId"]!); Assert.Contains("Shi An", (string)e["synopsis"]!["en"]!); Assert.Equal("UCx", (string)e["channel"]!["id"]!); Assert.Equal("complete", (string)e["ending"]!["kind"]!);
        Assert.Equal("up to date", (await Publish.PublishOneAsync(tools, new StoreJob(store, P, "rjzV__OHNR0"), outDir, false, default)).Skipped);
    }

    [Fact]
    public void Size_of_forty_dramas()
    {
        var outDir = Tmp();
        var rnd = new Random(7); var vocab = Enumerable.Range(0, 900).Select(_ => string.Concat(Enumerable.Range(0, rnd.Next(3, 9)).Select(_ => (char)('a' + rnd.Next(26))))).ToArray(); // varied text, so gzip is not flattered by repetition
        string Words(int n) => string.Join(" ", Enumerable.Range(0, n).Select(_ => vocab[rnd.Next(vocab.Length)]));
        for (int i = 0; i < 40; i++)
        {
            var j = Drama("p" + i.ToString("D2"), cast: 6); j["synopsis"]!["en"] = Words(140) + "."; j["teaser"]!["en"] = Words(25) + "."; j["fullTitle"]!["en"] = Words(7);
            foreach (var c in j["cast"]!.AsArray()) { c!["character"]!["en"] = Words(2); c["character"]!["pinyin"] = Words(2); c["label"] = new JsonObject { ["en"] = Words(3) }; }
            Put(outDir, "p" + i.ToString("D2"), j);
        }
        Publish.WriteIndex(outDir);
        var bytes = File.ReadAllBytes(Path.Combine(outDir, "search.json"));
        var ms = new MemoryStream(); using (var gz = new GZipStream(ms, CompressionLevel.Optimal, leaveOpen: true)) gz.Write(bytes, 0, bytes.Length);
        output.WriteLine($"search.json 40 dramas: {bytes.Length} bytes, gzip {ms.Length} bytes; per drama {bytes.Length / 40} bytes, gzip {ms.Length / 40} bytes");
        Assert.True(bytes.Length / 40 < 6000);
    }
}
