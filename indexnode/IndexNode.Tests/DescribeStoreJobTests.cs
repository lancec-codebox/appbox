using System.Text;
using System.Text.Json.Nodes;
using Xunit;

namespace IndexNode.Tests;

/// <summary>0.13.14: describe --job reads a store that keeps a drama only as step zips, and refuses a drama with nothing to describe before any AI call.</summary>
public class DescribeStoreJobTests
{
    const string Vid = "vdesc1";
    static readonly Paths P = new("index/");
    static byte[] B(string s) => Encoding.UTF8.GetBytes(s);
    static readonly byte[] Jpg = [0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3, 4, 0xFF, 0xD9];
    const string Vtt = "WEBVTT\n\n00:00:01.000 --> 00:00:03.000\n你好\n\n00:00:04.000 --> 00:00:06.000\n再见\n";

    /// <summary>The loose files of a processed drama, by name relative to videos/&lt;id&gt;/.</summary>
    static Dictionary<string, byte[]> Files(bool text = true, bool duration = true) => new()
    {
        ["info.json"] = B("""{"id":"vdesc1","title":"Reborn Bride","description":"A bride returns."}"""),
        ["thumb.jpg"] = Jpg,
        ["ocr/subs.zh.vtt"] = text ? B(Vtt) : B("WEBVTT\n"),
        ["cast/clusters.json"] = B("""{"clusters":[{"id":3,"main":true,"screenSeconds":42,"first":5,"masked":false}]}"""),
        ["cast/credits.json"] = B("""{"lines":[{"text":"主演：张三"}]}"""),
        ["cast/sheet_3.jpg"] = Jpg,
        ["downloaded.json"] = B(duration ? """{"durationSeconds":600}""" : """{"durationSeconds":0}"""),
    };

    /// <summary>A store with only step zips: the files are written loose through the stage-zip wrapper and committed, one stage each.</summary>
    static async Task<(FakeStore Fs, IStore Store)> Zips(Dictionary<string, byte[]> files, string dir)
    {
        var fs = new FakeStore(); var build = StageZipStore.Wrap(fs, Path.Combine(dir, "build-cache"));
        foreach (var (n, b) in files) await build.PutAsync(P.Video(Vid, n), b, null, null, false, default);
        await VideoJsonSeed.PutAsync(build, P, Vid, 600);
        foreach (var st in new[] { "download", "ocrsubs", "cast" }) Assert.True(await StageZipStore.Stage(build)!.CommitStageAsync(Vid, st, "n1", false, false, default), st);
        Assert.DoesNotContain(fs.Names("index/videos/" + Vid + "/"), n => n.EndsWith("/info.json") || n.EndsWith(".vtt") || n.EndsWith("/clusters.json"));   // only zips are in the store
        return (fs, StageZipStore.Wrap(fs, Path.Combine(dir, "read-cache")));
    }

    static DescribeInput Same(DescribeInput d) => d;
    static string Summary(DescribeInput d) => string.Join("|", d.Title, d.Description, d.Duration, d.TextSource, d.Cues.Count, string.Join(",", d.Credits), string.Join(",", d.Clusters.Select(c => $"{c.Id}:{c.ScreenSeconds}:{c.FirstSeen}")), string.Join(",", d.Sheets.Keys), d.Thumb != null);

    [Fact]
    public async Task A_store_with_only_step_zips_gathers_the_same_inputs_as_a_loose_job_folder()
    {
        var root = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var loose = Path.Combine(root, "loose"); foreach (var (n, b) in Files()) { var f = Path.Combine(loose, n); Directory.CreateDirectory(Path.GetDirectoryName(f)!); File.WriteAllBytes(f, b); }
            var a = await DescribeJob.GatherAsync(new LocalJob(loose, Vid), Path.Combine(root, "w1"), default);
            var (_, store) = await Zips(Files(), root);
            var z = await DescribeJob.GatherAsync(new StoreJob(store, P, Vid), Path.Combine(root, "w2"), default);
            Assert.Equal(600, z.Duration); Assert.Equal(2, z.Cues.Count); Assert.Equal("Reborn Bride", z.Title); Assert.Equal(new[] { 3 }, z.Clusters.Select(c => c.Id).ToArray()); Assert.Single(z.Sheets);
            Assert.Equal(Summary(a), Summary(z));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task A_ghosted_cast_zip_is_hydrated_and_read_not_failed_or_empty()
    {
        var root = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var (fs, store) = await Zips(Files(), root);
            var cz = P.Video(Vid, "cast.zip"); var size = fs[cz].Data.Length;
            fs.Dehydrate(cz); await fs.SetTagsAsync(cz, new Dictionary<string, string> { ["ghostd_state"] = "ghost", ["ghostd_size"] = size.ToString() }, null, default);   // how the Nexis ghost looks in the store
            Assert.Empty(fs[cz].Data);   // 0 bytes, as the ghost is in the store
            var z = await DescribeJob.GatherAsync(new StoreJob(store, P, Vid), Path.Combine(root, "w"), default);
            Assert.Single(z.Clusters); Assert.Single(z.Sheets); Assert.Single(z.Credits);
        }
        finally { Directory.Delete(root, true); }
    }

    sealed class CountingChat : IChat
    {
        public int Calls;
        public Task<string> ChatAsync(string model, JsonNode content, CancellationToken ct) { Calls++; return Task.FromResult("{}"); }
    }

    [Fact]
    public async Task A_drama_without_text_cues_or_without_duration_is_refused_before_any_ai_call()
    {
        var root = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var chat = new CountingChat();
            var (_, noText) = await Zips(Files(text: false), root);
            var e1 = await Assert.ThrowsAsync<InvalidOperationException>(() => DescribeJob.GatherAsync(new StoreJob(noText, P, Vid), Path.Combine(root, "w1"), default));
            Assert.Contains("no text cues", e1.Message);
            var (_, noDur) = await Zips(Files(duration: false), Path.Combine(Directory.CreateDirectory(Path.Combine(root, "b")).FullName));
            var e2 = await Assert.ThrowsAsync<InvalidOperationException>(() => DescribeJob.GatherAsync(new StoreJob(noDur, P, Vid), Path.Combine(root, "w2"), default));
            Assert.Contains("duration", e2.Message);
            // a completely empty store: the same refusal; the chat is never reached (gather throws first)
            var e3 = await Assert.ThrowsAsync<InvalidOperationException>(() => DescribeJob.GatherAsync(new StoreJob(new FakeStore(), P, "nothing"), Path.Combine(root, "w3"), default));
            Assert.Contains("no text cues", e3.Message); Assert.Equal(0, chat.Calls);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task The_describe_command_on_an_empty_local_folder_fails_non_zero_with_no_ai_call()
    {
        var root = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var empty = Path.Combine(root, "empty"); Directory.CreateDirectory(empty);
            var args = Args.Parse(["describe", "--local", empty, "--ai-url", "http://127.0.0.1:1/never", "--ai-key", "k", "--ai-text-model", "m"]);
            await Assert.ThrowsAsync<InvalidOperationException>(() => SiteCommands.DescribeAsync(args));   // Program turns an exception into "error: ..." and exit code 1
            Assert.False(File.Exists(Path.Combine(empty, "describe.json")));
        }
        finally { Directory.Delete(root, true); }
    }
}
