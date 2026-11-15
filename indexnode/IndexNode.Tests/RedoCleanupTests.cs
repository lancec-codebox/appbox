using System.Text;
using System.Text.Json.Nodes;
namespace IndexNode.Tests;

public class RedoCleanupTests
{
    static readonly Paths P = new("index-test/");
    const string V = "index-test/videos/vid/";

    sealed class Chat(bool fail = false) : IChat
    {
        public Task<string> ChatAsync(string model, JsonNode content, CancellationToken ct) => throw new NotSupportedException();
        public Task<JsonObject> JsonAsync(string model, JsonNode content, string tool, CancellationToken ct)
        {
            if (fail) throw new InvalidOperationException("model down");
            if (tool == "submit_glossary") return Task.FromResult(JsonNode.Parse("""{"plot":"p","entries":[]}""")!.AsObject());
            var arr = new JsonArray();
            foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(content.GetValue<string>(), @"^TRANSLATE \[(\d+)\] ", System.Text.RegularExpressions.RegexOptions.Multiline))
                arr.Add(new JsonObject { ["i"] = int.Parse(m.Groups[1].Value), ["en"] = "Line " + m.Groups[1].Value });
            return Task.FromResult(new JsonObject { ["cues"] = arr });
        }
    }

    static readonly string[] Source = ["video.mp4", "info.json", "description.txt", "thumb.jpg", "subs/zh.vtt", "subs/zh.json", "comments/c1.json", "downloaded.json"];
    static readonly string[] Others = ["frames/manifest.json", "frames/c0.mjpeg", "keyframes/k0.mp4", "sscd/manifest.json", "clip/manifest.json", "asr/sensevoice.zh.json", "ocr/lines.json", "ocr/subs.zh.vtt", "ocr/subs.zh-Hans.vtt", "ocr/subs.en.vtt", "cast/clusters.json", "cast/poster_0.jpg", "describe.json", "sync.json", "compact.mp4", "processed.json"];

    static async Task<FakeStore> Store()
    {
        var s = new FakeStore();
        async Task Put(string n, string? c = null) => await s.PutAsync(V + n, Encoding.UTF8.GetBytes(c ?? (n.EndsWith(".json") ? """{"segments":[]}""" : "x")), null, null, false, default);
        foreach (var n in Source.Concat(Others)) await Put(n);
        await Put("downloaded.json", """{"durationSeconds":60}""");
        var vtt = "WEBVTT\n\n" + string.Concat(Enumerable.Range(0, 8).Select(i => $"00:00:{i * 2 + 1:00}.000 --> 00:00:{i * 2 + 2:00}.000\n你好{i}\n\n"));
        await Put("ocr/subs.zh-Hans.vtt", vtt); await Put("ocr/subs.zh.vtt", vtt);
        await Put("subs/ai/en.vtt", "OLD"); await Put("subs/ai/en.json", "OLD"); await Put("subs/ai/en.old-run.vtt", "OLD");
        return s;
    }

    static async Task Redo(FakeStore s, string step, IChat chat)
    {
        ProcessStage.Ai = new AiConfig { BaseUrl = "http://x", TextModel = "m" };
        var ctx = new JobContext { Job = new JobDescriptor { Stage = "process", JobId = "vid", Steps = [step] }, Stage = "process", BlobName = "", LeaseId = "", Worker = 0, Store = s, Paths = P, NodeId = "t", Token = default, ThreadShare = () => 1, Report = (_, _) => { } };
        await new ProcessStage(new Tools(new HttpClient()), s, new NodeConfig(), new HttpClient()) { Redo = true, Chat = (_, _) => chat }.RunAsync(ctx);
    }

    [Fact]
    public async Task A_redo_removes_only_that_steps_stale_files_after_the_new_run_wrote_its_own()
    {
        var s = await Store(); var before = s.Blobs.Keys.ToHashSet();
        await Redo(s, "translate", new Chat());
        Assert.NotEqual("OLD", Encoding.UTF8.GetString(s.Blobs[V + "subs/ai/en.vtt"].Data));   // replaced
        Assert.NotEqual("OLD", Encoding.UTF8.GetString(s.Blobs[V + "subs/ai/en.json"].Data));
        Assert.False(s.Exists(V + "subs/ai/en.old-run.vtt"));                                   // stale own file gone
        foreach (var n in Source.Concat(Others)) Assert.True(s.Exists(V + n), n + " must stay");  // sources and other steps untouched (processed.json too)
    }

    [Fact]
    public async Task A_failed_redo_keeps_the_old_outputs()
    {
        var s = await Store();
        await Assert.ThrowsAnyAsync<Exception>(() => Redo(s, "translate", new Chat(fail: true)));
        foreach (var n in new[] { "subs/ai/en.vtt", "subs/ai/en.json", "subs/ai/en.old-run.vtt" }) Assert.Equal("OLD", Encoding.UTF8.GetString(s.Blobs[V + n].Data));
    }

    [Fact]
    public void Stale_is_each_steps_own_derived_files_the_run_did_not_write_and_never_sources()
    {
        var all = Source.Concat(Others).Concat(["frames/old.mjpeg", "frames/shots.json", "keyframes/k1.mp4", "ocr/subs.en.vtt", "cast/poster_3.jpg", "subs/ai/en.vtt"]).Select(n => V + n).Distinct().ToArray();
        string[] Rel(string step, params string[] wrote) => ProcessStage.Stale(step, V, all, wrote.Select(n => V + n).ToArray()).Select(n => n[V.Length..]).OrderBy(x => x).ToArray();
        Assert.Equal(["frames/c0.mjpeg", "frames/old.mjpeg", "frames/shots.json", "keyframes/k0.mp4", "keyframes/k1.mp4"], Rel("frames", "frames/manifest.json", "frames/c9.mjpeg"));
        Assert.Equal(["ocr/subs.en.vtt"], Rel("ocrsubs", "ocr/lines.json", "ocr/subs.zh.vtt", "ocr/subs.zh-Hans.vtt", "ocr/subs.zh-Hans.vtt"));  // no English found this time
        Assert.Equal(["cast/poster_0.jpg", "cast/poster_3.jpg"], Rel("cast", "cast/clusters.json"));
        Assert.Equal(["describe.json"], Rel("describe", "describe.json", "x").Length == 0 ? ["describe.json"] : []);  // wrote its own file: nothing stale
        Assert.Empty(Rel("transcribe"));                            // wrote nothing: nothing is removed
        Assert.Empty(Rel("describe", "describe.json"));
        foreach (var step in ProcessStage.KnownSteps) foreach (var n in Rel(step, ProcessStage.Owned.GetValueOrDefault(step)?.Select(o => o.EndsWith('/') ? o + "new" : o).ToArray() ?? []))
            Assert.DoesNotContain(n, Source);
    }
}
