using System.Reflection;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Xunit;

namespace IndexNode.Tests;

/// <summary>The prompts live in Prompts/*.yaml (embedded); the committed samples under Prompts/samples/ are the full requests for one fixture drama and are checked here.</summary>
public class PromptFileTests
{
    static readonly string[] Files = ["describe", "twist-check", "translate", "ending", "classify", "probe"];

    [Fact]
    public void Every_prompt_file_loads_has_a_version_and_nothing_is_missing()
    {
        foreach (var n in Files) { var f = PromptFile.Load(n); Assert.Matches(@"^[a-z]+-\d+$", f.Version); }
        Assert.Equal("describe-9", PromptFile.Load("describe").Version); Assert.Equal("describe-9", Describe.PromptVersion); Assert.Equal("translate-3", Translate.PromptVersion);
        Assert.Equal(16, PromptFile.Load("describe").List("example-titles").Count); Assert.Contains("aunt-uncle", PromptFile.Load("describe").List("relation-kinds"));
        Assert.Contains("describe-8 (2026-11-19)", File.ReadAllText(Path.Combine(Root(), "IndexNode", "Prompts", "describe.yaml")));   // the history comment moved with the text
    }

    [Fact]
    public void Every_yaml_file_in_the_source_is_embedded_and_every_embedded_prompt_is_in_the_list()
    {
        var onDisk = Directory.GetFiles(Path.Combine(Root(), "IndexNode", "Prompts"), "*.yaml").Select(Path.GetFileNameWithoutExtension).OrderBy(x => x).ToArray();
        Assert.Equal(Files.OrderBy(x => x).ToArray(), onDisk!);
        var emb = typeof(PromptFile).Assembly.GetManifestResourceNames().Where(n => n.StartsWith("prompts/")).Select(n => n[8..^5]).OrderBy(x => x).ToArray();
        Assert.Equal(onDisk, emb);
    }

    [Fact]
    public void A_missing_field_or_list_fails_clearly_naming_the_file_and_the_field()
    {
        var f = PromptFile.Load("probe");
        var e = Assert.Throws<InvalidOperationException>(() => f.Text("nope")); Assert.Contains("probe.yaml", e.Message); Assert.Contains("'nope'", e.Message);
        var e2 = Assert.Throws<InvalidOperationException>(() => f.List("nope")); Assert.Contains("'nope'", e2.Message);
        Assert.Throws<InvalidOperationException>(() => PromptFile.Load("no-such-prompt"));
        Assert.Throws<FormatException>(() => PromptFile.Parse("x", "not a field"));
        Assert.Throws<FormatException>(() => PromptFile.Parse("x", "a: 1\na: 2"));
    }

    [Fact]
    public void The_parser_reads_block_scalars_lists_quotes_and_comments()
    {
        var f = PromptFile.Parse("t", "# c\nversion: t-1\nblock: |\n  line one\n    indented {x}\n\n  line two\nstrip: |-\n  a\nlist:\n  - one\n  # skip\n  - \"two: 2\"\nq: 'it''s'\nj: '{\"a\":1}'\n");
        Assert.Equal("line one\n  indented {x}\n\nline two", f.Text("block")); Assert.Equal("a", f.Text("strip"));
        Assert.Equal(new[] { "one", "two: 2" }, f.List("list")); Assert.Equal("it's", f.Text("q"));
        Assert.Equal("indented 5", f.Fill("block", ("x", "5")).Split('\n')[1].Trim()); Assert.Equal("{\"a\":1}", f.Text("j")); Assert.Empty(f.Placeholders("j"));   // JSON braces are not placeholders
    }

    [Fact]
    public void Fill_fails_on_an_unfilled_placeholder_and_on_a_value_nothing_uses()
    {
        var f = PromptFile.Parse("t", "a: Hello {name}, {title}\n");
        Assert.Equal("Hello Ann, Mr", f.Fill("a", ("name", "Ann"), ("title", "Mr")));
        Assert.Contains("{title}", Assert.Throws<InvalidOperationException>(() => f.Fill("a", ("name", "Ann"))).Message);
        Assert.Contains("{extra}", Assert.Throws<InvalidOperationException>(() => f.Fill("a", ("name", "Ann"), ("title", "x"), ("extra", "y"))).Message);
    }

    [Fact]
    public void No_built_prompt_has_an_unfilled_placeholder_left()
    {
        foreach (var (name, text) in Requests().Select(r => (r.Name, r.Text)))
            Assert.DoesNotMatch(new Regex(@"\{(title|duration|description|textSource|textTrack|credits|clusters|relationKinds|exampleTitles|synopsis|teaser|cast|seed|script|plotLine|shape|glossary|relations|plot|ids|count|id|text|en|minutes|tail|channel|channelPart|length|channelColumn|fullTitle)\}"), text);
    }

    // ---- the committed samples
    static string Root() { var d = new DirectoryInfo(AppContext.BaseDirectory); while (d != null && !Directory.Exists(Path.Combine(d.FullName, "IndexNode", "Prompts"))) d = d.Parent; return d?.FullName ?? throw new DirectoryNotFoundException("indexnode source folder"); }

    static DescribeInput Fixture() => new()
    {
        JobId = "fixture1", Title = "重生之我在古代当王妃 EP1-80 full #shortdrama", Description = "A reborn bride returns to her wedding day.", Duration = 600, TextSource = "subtitles",
        Cues = [new ShapeCue(1, 3, "你好，夫君。"), new ShapeCue(4, 6, "我回来了。"), new ShapeCue(7, 9, "这一次，我不会再输。")],
        Credits = ["主演：张三 李四"], Clusters = [new(0, 120, 4, false), new(1, 90, 6, false)],
        Thumb = "thumb.jpg", Sheets = new Dictionary<int, string> { [0] = "cast/sheet_0.jpg", [1] = "cast/sheet_1.jpg" }, PosterSheet = "cast/poster.jpg", PosterCount = 8,
    };

    static JsonObject FixtureResult() => JsonNode.Parse("""
    {"title":{"en":"Reborn Bride Marries the Sickly Heir","zh":"重生之我在古代当王妃"},"titleSource":"translated","fullTitle":{"en":"Reborn Bride Marries the Sickly Heir (Full Version)"},
     "teaser":{"en":"Reborn on her wedding day, she vows to marry the heir's brother this time."},"synopsis":{"en":"She returns to her wedding day, exposes the scheme, and marries the right brother."},
     "cast":[{"cluster":0,"character":{"zh":"苏醒眠","pinyin":"Su Xingmian","en":"Su Xingmian"},"role":"female lead","roleAtStart":"A scholar's daughter.","description":"Clever and kind.","label":{"en":"Su Xingmian"}},
             {"cluster":1,"character":null,"role":"supporting","roleAtStart":"The bride's grandmother.","description":"A stern elder.","label":{"en":"Su Xingmian's grandmother"}}],"relations":[]}
    """)!.AsObject();

    /// <summary>Every request the fixture drama makes, as the readable text of the committed samples.</summary>
    static List<(string Name, string File, string Text)> Requests()
    {
        var d = Fixture(); var list = new List<(string, string, string)>();
        var main = Describe.BuildMain(d, "vision-model"); list.Add(("describe", "describe.request.txt", PromptFile.Render("describe", main.Model, main.Prompt, main.Images)));
        var tw = Describe.BuildTwistCheck(FixtureResult(), "text-model"); list.Add(("twist-check", "twist-check.request.txt", PromptFile.Render("twist-check", tw.Model, tw.Prompt, tw.Images)));
        var cues = new List<ShapeCue> { new(1, 3, "你好，夫君。"), new(4, 6, "我回来了。"), new(7, 9, "这一次，我不会再输。") };
        var glossary = Translate.GlossaryPrompt("苏醒眠 => Su Xingmian", "She returns to her wedding day.", "你好，夫君。\n我回来了。\n这一次，我不会再输。", true);
        var batch = Translate.BatchPrompt(cues, [(0, false), (1, true), (2, true)], new Dictionary<int, string> { [0] = "Hello, husband." }, [new GlossaryEntry("夫君", "husband", "address", "Xiao Linyuan")], "She returns to her wedding day.", "Su Xingmian -> Xiao Linyuan: spouse", "A short plot.", "Reborn Bride");
        list.Add(("translate", "translate.request.txt", PromptFile.Render("translate glossary call", "text-model", glossary, []) + "\n" + PromptFile.Render("translate batch call", "text-model", batch, [])));
        list.Add(("ending", "ending.request.txt", PromptFile.Render("ending backfill call", "text-model", EndingBackfill.Prompt([new EndingBackfill.Item("fixture1", "Reborn Bride", "A reborn bride.", "[9:50] 完", "She marries the right brother.")]), [])));
        list.Add(("classify", "classify.request.txt", PromptFile.Render("classify call", "text-model", ClassifyStage.Prompt(null, [new ClassifyEntry { Id = "v1", Title = "Reborn Bride Full", Duration = 3600, ChannelName = "Drama Channel" }]), [])));
        return list;
    }

    static List<(string Name, string Text)> RequestsNamed => Requests().Select(r => (r.Name, r.Text)).ToList();

    [Fact]
    public void The_committed_request_samples_match_what_the_code_builds_for_the_fixture_drama()
    {
        var dir = Path.Combine(Root(), "IndexNode", "Prompts", "samples"); bool update = Environment.GetEnvironmentVariable("UPDATE_PROMPT_SAMPLES") == "1";
        Directory.CreateDirectory(dir); var diffs = new List<string>();
        foreach (var (name, file, text) in Requests())
        {
            var path = Path.Combine(dir, file); var want = text.Replace("\r\n", "\n");
            if (update) { File.WriteAllText(path, want, new UTF8Encoding(false)); continue; }
            if (!File.Exists(path)) { diffs.Add($"{file}: missing"); continue; }
            if (File.ReadAllText(path).Replace("\r\n", "\n") != want) diffs.Add($"{file}: differs from what the code builds");
        }
        Assert.True(diffs.Count == 0, string.Join("; ", diffs) + ". Run the tests with UPDATE_PROMPT_SAMPLES=1 to rewrite the samples, and review the diff.");
    }
}
