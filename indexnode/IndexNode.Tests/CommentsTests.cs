using System.Text.Json;
using System.Text.Json.Nodes;

namespace IndexNode.Tests;

[Collection("path-env")]
public class CommentsTests : IDisposable
{
    public void Dispose() => CommentsTests.RestorePath();
    private static readonly Paths P = new("index-test/");
    private static readonly DateTimeOffset D = DateTimeOffset.Parse("2026-11-13T19:00:00Z");

    // ------------------------------------------------------------ schedule



    // ------------------------------------------------------------ snapshot

    private static JsonObject Info(long? total, params object[][] comments) => new()
    {
        ["comment_count"] = total,
        ["comments"] = new JsonArray(comments.Select(c => (JsonNode)new JsonObject
        {
            ["id"] = (string)c[0], ["parent"] = (string)c[1], ["author"] = "a" + c[0], ["author_id"] = "UC" + c[0], ["text"] = "text " + c[0],
            ["like_count"] = (long)c[2], ["timestamp"] = 1700000000L, ["is_pinned"] = (bool)c[3], ["author_is_uploader"] = (bool)c[4],
        }).ToArray()),
    };

    [Fact]
    public void Snapshot_has_the_protocol_shape_and_merges_the_two_passes_by_id()
    {
        var top = Info(42, ["c1", "root", 5L, true, false], ["c2", "c1", 1L, false, true]);
        var newest = Info(43, ["c2", "c1", 1L, false, true], ["c3", "root", 0L, false, false]);
        var snap = CommentsSnapshot.Build(top, newest);
        Assert.Equal(1, (int)snap["v"]!);
        Assert.Equal(3, (int)snap["count"]!);
        Assert.Equal(42L, (long)snap["commentCount"]!); // YouTube's total, from the first pass
        var list = snap["comments"]!.AsArray();
        Assert.Equal(["c1", "c2", "c3"], list.Select(c => (string)c!["id"]!));
        var c1 = list[0]!.AsObject();
        Assert.Equal(["id", "parent", "author", "authorId", "text", "likes", "timestamp", "pinned", "byUploader"], c1.Select(kv => kv.Key));
        Assert.Equal("root", (string)c1["parent"]!); Assert.Equal(5L, (long)c1["likes"]!); Assert.True((bool)c1["pinned"]!);
        Assert.True((bool)list[1]!["byUploader"]!);
    }

    [Fact]
    public void Snapshot_of_a_video_without_comments_is_empty_not_an_error()
    {
        var snap = CommentsSnapshot.Build(new JsonObject(), null);
        Assert.Equal(0, (int)snap["count"]!);
        Assert.Empty(snap["comments"]!.AsArray());
    }

    [Fact]
    public void Snapshot_blob_timestamp_format() => Assert.Equal("20261113T190501Z", CommentsSnapshot.BlobTimestamp(DateTimeOffset.Parse("2026-11-13T19:05:01Z")));

    [Fact]
    public void Yt_dlp_comment_args_use_the_protocol_extractor_args_and_no_cookies()
    {
        var a = CommentsSnapshot.Args("https://x", "/w/top.%(ext)s", 1000, "top");
        Assert.Contains("--write-comments", a); Assert.Contains("--skip-download", a);
        Assert.Contains("youtube:max_comments=1000,all,all,all;comment_sort=top", a);
        Assert.DoesNotContain("--cookies", a); Assert.DoesNotContain("--cookies-from-browser", a);
    }

    // ------------------------------------------------------------ idx_due and rescheduling in the engine

    private sealed class FuncHandler(Func<JobContext, Task> f) : IStageHandler { public Task RunAsync(JobContext ctx) => f(ctx); }

    private static NodeEngine Engine(FakeStore s, string stage, Func<JobContext, Task> h) => new(s, P, new EngineOptions
    {
        NodeId = "n-1", Stages = [stage], Jobs = new() { [stage] = 1 }, Once = true,
        PollInterval = TimeSpan.FromMilliseconds(30), RenewInterval = TimeSpan.FromMilliseconds(40), ControlInterval = TimeSpan.FromMilliseconds(40),
        RegistryInterval = TimeSpan.FromSeconds(30), TransientBackoff = _ => TimeSpan.FromHours(1),
    }, new Dictionary<string, IStageHandler> { [stage] = new FuncHandler(h) });

    private static string Ago(int minutes) => Protocol.Utc(DateTimeOffset.UtcNow.AddMinutes(-minutes));
    private static string Ahead(int minutes) => Protocol.Utc(DateTimeOffset.UtcNow.AddMinutes(minutes));

    [Fact]
    public void Pending_query_includes_the_due_condition()
    {
        Assert.Equal("\"idx_stage\"='comments' AND \"idx_state\"='pending' AND \"idx_due\" <= '2026-11-13T20:00:00Z'", Tags.PendingWhere("comments", "2026-11-13T20:00:00Z"));
        Assert.DoesNotContain("idx_due", Tags.PendingWhere("comments"));
    }

    // ------------------------------------------------------------ unknown steps (transcribe)

    [Fact]
    public async Task A_job_with_an_unknown_step_is_released_without_counting_an_attempt()
    {
        var s = new FakeStore();
        await s.AddJob(P, "process", "vid", Ago(10));
        var job = s.Job(P.Job("process", "vid"));
        job.Steps = ["frames", "sscd", "faces", "compress"];
        await s.PutAsync(P.Job("process", "vid"), job.ToBytes(), Tags.Job("process", "pending", job.AddedAt), null, false, default);
        var stage = new ProcessStage(new Tools(new HttpClient()), null!, new NodeConfig(), new HttpClient());
        await Engine(s, "process", stage.RunAsync).RunAsync(default);
        var name = P.Job("process", "vid");
        Assert.Equal("pending", s.State(name));
        Assert.Equal(0, s.Job(name).Attempts);
        Assert.Null(s.LeaseOf(name));
    }

    // ------------------------------------------------------------ the stages with a fake yt-dlp

    /// <summary>PATH before the first fake ffmpeg/yt-dlp dir was put in front of it; the classes that call <see cref="Setup"/> restore it when each test ends (tests that look for a real ffmpeg must not see the fake).</summary>
    internal static string? OriginalPath;
    internal static void RestorePath() { if (OriginalPath != null) Environment.SetEnvironmentVariable("PATH", OriginalPath); }
    internal static string Setup(out NodeConfig cfg)
    {
        var dir = Path.Combine(Path.GetTempPath(), "idx-fake-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "yt-dlp.sh"), """
            #!/bin/bash
            echo "$@" >> "$(dirname "$0")/calls.log"
            args=("$@"); out=""; mode=main; ea=""; sl=""; url="${args[${#args[@]}-1]}"
            for ((i=0;i<${#args[@]};i++)); do
              case "${args[$i]}" in
                -o) out="${args[$((i+1))]}";;
                --write-subs) mode=subs;;
                --write-auto-subs) mode=auto;;
                --write-comments) mode=comments;;
                --extractor-args) ea="${args[$((i+1))]}";;
                --sub-langs) sl="${args[$((i+1))]}";;
              esac
            done
            base=$(echo "$out" | sed 's/%(ext)s$//')
            case "$mode" in
              main)
                printf VIDEO > "${base}mp4"; printf JPG > "${base}jpg"
                echo '{"id":"x","description":"line one\nline two","height":480,"duration":12.5,"playable_in_embed":true,"format_id":"398+251-0","vcodec":"av01.0.04M.08","acodec":"opus","requested_formats":[{"vcodec":"av01.0.04M.08","acodec":"none"},{"vcodec":"none","acodec":"opus","language":"zh-Hans","format_note":"Chinese, original (default), low"}],"formats":[{"vcodec":"none","acodec":"opus","language":"zh-Hans","format_note":"Chinese, original (default), low"},{"vcodec":"none","acodec":"opus","language":"en-US","format_note":"English (US), dubbed-auto, low"}],"subtitles":{"en":[{}],"zh-Hans":[{}]},"automatic_captions":{"en":[{}],"en-ar":[{}],"en-ja":[{}],"zh-Hans":[{}],"zh-orig":[{}]},"comments":[{"id":"zz"}]}' > "${base}info.json";;
              subs|auto)
                if [[ "$url" == *nosubs* ]]; then echo "ERROR: There are no subtitles" >&2; exit 1; fi
                for l in ${sl//,/ }; do printf "WEBVTT\n\n00:00:01.000 --> 00:00:02.000\n$mode $l\n" > "${base}$l.vtt"; done;;
              comments)
                if [[ "$ea" == *comment_sort=new* ]]; then
                  echo '{"comment_count":42,"comments":[{"id":"c2","parent":"c1","author":"B","author_id":"UCb","text":"reply","like_count":1,"timestamp":1700000100,"is_pinned":false,"author_is_uploader":true},{"id":"c3","parent":"root","author":"C","author_id":"UCc","text":"new","like_count":0,"timestamp":1700000200,"is_pinned":false,"author_is_uploader":false}]}' > "${base}info.json"
                else
                  echo '{"comment_count":42,"comments":[{"id":"c1","parent":"root","author":"A","author_id":"UCa","text":"top","like_count":5,"timestamp":1700000000,"is_pinned":true,"author_is_uploader":false},{"id":"c2","parent":"c1","author":"B","author_id":"UCb","text":"reply","like_count":1,"timestamp":1700000100,"is_pinned":false,"author_is_uploader":true}]}' > "${base}info.json"
                fi;;
            esac
            """.Replace("\r", ""));
        foreach (var n in new[] { "ffmpeg", "ffprobe" }) { File.WriteAllText(Path.Combine(dir, n), "#!/bin/bash\nexit 0\n"); File.SetUnixFileMode(Path.Combine(dir, n), (UnixFileMode)0b111_101_101); }
        OriginalPath ??= Environment.GetEnvironmentVariable("PATH");
        Environment.SetEnvironmentVariable("PATH", dir + Path.PathSeparator + OriginalPath);
        cfg = new NodeConfig { Ytdlp = new YtDlpConfig { Command = ["bash", Path.Combine(dir, "yt-dlp.sh")] } };
        return dir;
    }

    internal static JobContext Ctx(IStore s, JobDescriptor job, string stage) => new()
    {
        Job = job, Stage = stage, BlobName = P.Job(stage, job.JobId), LeaseId = "L", Worker = 1, Store = s, Paths = P, NodeId = "n-1", Token = default,
        ThreadShare = () => 1, Report = (_, _) => { },
    };

    internal static readonly DownloadGate NoWait = new((_, _) => Task.CompletedTask);



    [Fact]
    public void Subtitle_files_are_named_lang_vtt_and_lang_auto_vtt()
    {
        var root = Path.Combine(Path.GetTempPath(), "subs-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Path.Combine(root, "m")); Directory.CreateDirectory(Path.Combine(root, "a"));
        foreach (var f in new[] { "m/video.zh-Hans.vtt", "m/video.en-US.vtt", "a/video.en.vtt", "a/video.zh-TW.vtt" }) File.WriteAllText(Path.Combine(root, f), "x");
        var names = DownloadStage.CollectSubs(Path.Combine(root, "m"), Path.Combine(root, "a")).Select(x => x.Name).ToList();
        Assert.Equal(["zh-Hans", "zh-TW.auto", "en-US", "en.auto"], names);
        Assert.Empty(DownloadStage.CollectSubs(null, null));
        Assert.Empty(DownloadStage.CollectSubs(Path.Combine(root, "none"), null));
    }

}
