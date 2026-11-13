using System.Text.Json;
using System.Text.Json.Nodes;

namespace IndexNode.Tests;

public class CommentsTests
{
    private static readonly Paths P = new("index-test/");
    private static readonly DateTimeOffset D = DateTimeOffset.Parse("2026-11-13T19:00:00Z");

    // ------------------------------------------------------------ schedule

    [Fact]
    public void Refresh_rounds_fall_at_1_3_7_30_days_then_every_30_days_for_a_year()
    {
        var now = D;
        var offsets = new List<double>();
        int round = 0;
        while (CommentsSchedule.Next(D, round, now) is { } n) { Assert.Equal(round + 1, n.Round); offsets.Add((n.Due - D).TotalDays); round = n.Round; }
        Assert.Equal([1, 3, 7, 30, 60, 90, 120, 150, 180, 210, 240, 270, 300, 330, 360], offsets);
    }

    [Fact]
    public void Late_node_skips_rounds_that_already_passed()
    {
        var n = CommentsSchedule.Next(D, 0, D.AddDays(10))!.Value;
        Assert.Equal(4, n.Round);
        Assert.Equal(D.AddDays(30), n.Due);
        Assert.Null(CommentsSchedule.Next(D, 15, D.AddDays(361)));
        Assert.Null(CommentsSchedule.Next(D, 3, D.AddDays(400)));
    }

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

    [Theory]
    [InlineData(false)]
    [InlineData(true)] // the SAS lacks permission f: listing with tags must respect idx_due too
    public async Task Work_is_found_only_when_idx_due_has_arrived(bool forbidFind)
    {
        var s = new FakeStore { ForbidFind = forbidFind };
        await s.AddJob(P, "comments", "later", Ago(60), due: Ahead(60));
        await s.AddJob(P, "comments", "ready", Ago(30), due: Ago(5));
        var ran = new List<string>();
        await Engine(s, "comments", ctx => { ran.Add(ctx.Job.JobId); return Task.CompletedTask; }).RunAsync(default);
        Assert.Equal(["ready"], ran);
        Assert.Equal("pending", s.State(P.Job("comments", "later")));
        Assert.Equal("done", s.State(P.Job("comments", "ready")));
    }

    [Fact]
    public void Pending_query_includes_the_due_condition()
    {
        Assert.Equal("\"idx_stage\"='comments' AND \"idx_state\"='pending' AND \"idx_due\" <= '2026-11-13T20:00:00Z'", Tags.PendingWhere("comments", "2026-11-13T20:00:00Z"));
        Assert.DoesNotContain("idx_due", Tags.PendingWhere("comments"));
    }

    [Fact]
    public async Task A_finished_comments_round_goes_back_to_pending_with_the_next_due_and_keeps_its_added_time()
    {
        var s = new FakeStore();
        var added = Ago(90);
        await s.AddJob(P, "comments", "vid", added);
        var next = Ahead(24 * 60);
        await Engine(s, "comments", ctx => { ctx.Job.Round = 1; ctx.NextDue = next; return Task.CompletedTask; }).RunAsync(default);
        var name = P.Job("comments", "vid");
        Assert.Equal("pending", s.State(name));
        Assert.Equal(next, s[name].Tags[Tags.Due]);
        Assert.Equal(added, s[name].Tags[Tags.Added]);
        Assert.Equal("comments", s[name].Tags[Tags.Stage]);
        Assert.Equal(1, s.Job(name).Round);
        Assert.Null(s.LeaseOf(name));
        // not due yet: another run finds nothing
        var ran = 0;
        await Engine(s, "comments", _ => { ran++; return Task.CompletedTask; }).RunAsync(default);
        Assert.Equal(0, ran);
    }

    [Fact]
    public async Task Without_a_next_due_the_comments_job_ends_as_done()
    {
        var s = new FakeStore();
        await s.AddJob(P, "comments", "vid", Ago(10));
        await Engine(s, "comments", _ => Task.CompletedTask).RunAsync(default);
        Assert.Equal("done", s.State(P.Job("comments", "vid")));
    }

    [Fact]
    public async Task A_transient_failure_keeps_the_due_tag()
    {
        var s = new FakeStore();
        var due = Ago(3);
        await s.AddJob(P, "comments", "vid", Ago(30), due: due);
        await Engine(s, "comments", _ => throw new IOException("net")).RunAsync(default);
        var name = P.Job("comments", "vid");
        Assert.Equal(due, s[name].Tags[Tags.Due]);
        Assert.Equal(1, s.Job(name).Attempts);
    }

    // ------------------------------------------------------------ unknown steps (transcribe)

    [Fact]
    public async Task A_job_with_an_unknown_step_is_released_without_counting_an_attempt()
    {
        var s = new FakeStore();
        await s.AddJob(P, "process", "vid", Ago(10));
        var job = s.Job(P.Job("process", "vid"));
        job.Steps = ["frames", "sscd", "transcribe", "compress"];
        await s.PutAsync(P.Job("process", "vid"), job.ToBytes(), Tags.Job("process", "pending", job.AddedAt), null, false, default);
        var stage = new ProcessStage(new Tools(new HttpClient()), null!, new NodeConfig(), new HttpClient());
        await Engine(s, "process", stage.RunAsync).RunAsync(default);
        var name = P.Job("process", "vid");
        Assert.Equal("pending", s.State(name));
        Assert.Equal(0, s.Job(name).Attempts);
        Assert.Null(s.LeaseOf(name));
    }

    [Fact]
    public async Task Skipping_a_job_does_not_stop_the_node_taking_other_work()
    {
        var s = new FakeStore();
        await s.AddJob(P, "process", "skipme", Ago(20));
        await s.AddJob(P, "process", "other", Ago(10));
        var ran = new List<string>();
        var e = new NodeEngine(s, P, new EngineOptions { NodeId = "n-1", Stages = ["process"], Jobs = new() { ["process"] = 1 }, Once = false, MaxJobs = 2,
            PollInterval = TimeSpan.FromMilliseconds(30), ControlInterval = TimeSpan.FromMilliseconds(40), RenewInterval = TimeSpan.FromMilliseconds(40), RegistryInterval = TimeSpan.FromSeconds(30) },
            new Dictionary<string, IStageHandler> { ["process"] = new FuncHandler(ctx => { ran.Add(ctx.Job.JobId); return ctx.Job.JobId == "skipme" ? throw new JobSkipped("unknown step") : Task.CompletedTask; }) });
        await e.RunAsync(default).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(["skipme", "other"], ran);
        Assert.Equal("pending", s.State(P.Job("process", "skipme")));
        Assert.Equal("done", s.State(P.Job("process", "other")));
    }

    // ------------------------------------------------------------ the stages with a fake yt-dlp

    private static string Setup(out NodeConfig cfg)
    {
        var dir = Path.Combine(Path.GetTempPath(), "idx-fake-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "yt-dlp.sh"), """
            #!/bin/bash
            echo "$@" >> "$(dirname "$0")/calls.log"
            args=("$@"); out=""; mode=main; ea=""; url="${args[${#args[@]}-1]}"
            for ((i=0;i<${#args[@]};i++)); do
              case "${args[$i]}" in
                -o) out="${args[$((i+1))]}";;
                --write-subs) mode=subs;;
                --write-auto-subs) mode=auto;;
                --write-comments) mode=comments;;
                --extractor-args) ea="${args[$((i+1))]}";;
              esac
            done
            base=$(echo "$out" | sed 's/%(ext)s$//')
            case "$mode" in
              main)
                printf VIDEO > "${base}mp4"; printf JPG > "${base}jpg"
                echo '{"id":"x","description":"line one\nline two","height":480,"duration":12.5,"comments":[{"id":"zz"}]}' > "${base}info.json";;
              subs|auto)
                if [[ "$url" == *nosubs* ]]; then echo "ERROR: There are no subtitles" >&2; exit 1; fi
                printf "WEBVTT $mode en" > "${base}en.vtt"; printf "WEBVTT $mode zh" > "${base}zh-Hans.vtt";;
              comments)
                if [[ "$ea" == *comment_sort=new* ]]; then
                  echo '{"comment_count":42,"comments":[{"id":"c2","parent":"c1","author":"B","author_id":"UCb","text":"reply","like_count":1,"timestamp":1700000100,"is_pinned":false,"author_is_uploader":true},{"id":"c3","parent":"root","author":"C","author_id":"UCc","text":"new","like_count":0,"timestamp":1700000200,"is_pinned":false,"author_is_uploader":false}]}' > "${base}info.json"
                else
                  echo '{"comment_count":42,"comments":[{"id":"c1","parent":"root","author":"A","author_id":"UCa","text":"top","like_count":5,"timestamp":1700000000,"is_pinned":true,"author_is_uploader":false},{"id":"c2","parent":"c1","author":"B","author_id":"UCb","text":"reply","like_count":1,"timestamp":1700000100,"is_pinned":false,"author_is_uploader":true}]}' > "${base}info.json"
                fi;;
            esac
            """.Replace("\r", ""));
        foreach (var n in new[] { "ffmpeg", "ffprobe" }) { File.WriteAllText(Path.Combine(dir, n), "#!/bin/bash\nexit 0\n"); File.SetUnixFileMode(Path.Combine(dir, n), (UnixFileMode)0b111_101_101); }
        Environment.SetEnvironmentVariable("PATH", dir + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH"));
        cfg = new NodeConfig { Ytdlp = new YtDlpConfig { Command = ["bash", Path.Combine(dir, "yt-dlp.sh")] } };
        return dir;
    }

    private static JobContext Ctx(FakeStore s, JobDescriptor job, string stage) => new()
    {
        Job = job, Stage = stage, BlobName = P.Job(stage, job.JobId), LeaseId = "L", Worker = 1, Store = s, Paths = P, NodeId = "n-1", Token = default,
        ThreadShare = () => 1, Report = (_, _) => { },
    };

    private static readonly DownloadGate NoWait = new((_, _) => Task.CompletedTask);

    [Fact]
    public async Task Download_writes_description_subtitles_marker_and_creates_the_follow_up_jobs()
    {
        if (OperatingSystem.IsWindows()) return;
        var dir = Setup(out var cfg);
        var s = new FakeStore();
        var job = JobDescriptor.NewDownload("https://www.youtube.com/watch?v=abcdefghij1", 480, null);
        await new DownloadStage(new Tools(new HttpClient(), cfg), s, NoWait).RunAsync(Ctx(s, job, "download"));

        var id = "abcdefghij1";
        foreach (var f in new[] { "video.mp4", "info.json", "description.txt", "thumb.jpg", "downloaded.json", "subs/en.vtt", "subs/zh-Hans.vtt", "subs/en.auto.vtt", "subs/zh-Hans.auto.vtt" })
            Assert.True(s.Exists(P.Video(id, f)), f);
        Assert.Equal("line one\nline two", System.Text.Encoding.UTF8.GetString(s[P.Video(id, "description.txt")].Data));
        Assert.DoesNotContain("comments", System.Text.Encoding.UTF8.GetString(s[P.Video(id, "info.json")].Data)); // comments live in their own snapshots
        Assert.Contains("subs en", System.Text.Encoding.UTF8.GetString(s[P.Video(id, "subs/en.vtt")].Data));
        Assert.Contains("auto en", System.Text.Encoding.UTF8.GetString(s[P.Video(id, "subs/en.auto.vtt")].Data));

        var marker = JsonDocument.Parse(s[P.Video(id, "downloaded.json")].Data).RootElement;
        Assert.Equal(new[] { "en", "zh-Hans", "en.auto", "zh-Hans.auto" }.OrderBy(x => x), marker.GetProperty("subs").EnumerateArray().Select(x => x.GetString()!).OrderBy(x => x));
        Assert.Equal(480, marker.GetProperty("height").GetInt32());
        Assert.Equal(12.5, marker.GetProperty("durationSeconds").GetDouble());

        var cn = P.Job("comments", id);
        Assert.Equal("pending", s.State(cn));
        Assert.Equal(s[cn].Tags[Tags.Added], s[cn].Tags[Tags.Due]); // due at once
        Assert.Equal(0, s.Job(cn).Round);
        Assert.True(s.Exists(P.Job("process", id)));

        // two subtitle passes with the language list; no cookies anywhere
        var calls = File.ReadAllLines(Path.Combine(dir, "calls.log"));
        Assert.Contains(calls, c => c.Contains("--write-subs") && !c.Contains("--write-auto-subs") && c.Contains(DownloadStage.SubLangs));
        Assert.Contains(calls, c => c.Contains("--write-auto-subs") && !c.Contains("--write-subs"));
        Assert.DoesNotContain(calls, c => c.Contains("cookies"));
    }

    [Fact]
    public async Task Missing_subtitles_are_not_an_error_and_a_repeat_run_keeps_the_existing_jobs()
    {
        if (OperatingSystem.IsWindows()) return;
        var dir = Setup(out var cfg);
        var s = new FakeStore();
        var job = JobDescriptor.NewDownload("https://www.youtube.com/watch?v=nosubsvid01", 480, null);
        var stage = new DownloadStage(new Tools(new HttpClient(), cfg), s, NoWait);
        await stage.RunAsync(Ctx(s, job, "download"));
        var id = "nosubsvid01";
        Assert.True(s.Exists(P.Video(id, "downloaded.json")));
        Assert.Empty(JsonDocument.Parse(s[P.Video(id, "downloaded.json")].Data).RootElement.GetProperty("subs").EnumerateArray());
        Assert.DoesNotContain(s.Blobs.Keys, k => k.Contains("/subs/"));

        var callsBefore = File.ReadAllLines(Path.Combine(dir, "calls.log")).Length;
        await stage.RunAsync(Ctx(s, job, "download")); // marker exists: nothing downloaded again
        Assert.Equal(callsBefore, File.ReadAllLines(Path.Combine(dir, "calls.log")).Length);
        Assert.Equal("pending", s.State(P.Job("comments", id)));
        Assert.Equal(0, s.Job(P.Job("comments", id)).Round);
    }

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

    [Fact]
    public async Task Comments_round_saves_a_dated_snapshot_and_schedules_the_next_refresh()
    {
        if (OperatingSystem.IsWindows()) return;
        var dir = Setup(out var cfg);
        var s = new FakeStore();
        var id = "abcdefghij1";
        var at = DateTimeOffset.UtcNow.AddHours(-1);
        await s.PutAsync(P.Video(id, "downloaded.json"), JsonSerializer.SerializeToUtf8Bytes(new { v = 1, at = Protocol.Utc(at) }), null, null, false, default);
        var job = JobDescriptor.NewComments(id, "https://www.youtube.com/watch?v=" + id);
        var ctx = Ctx(s, job, "comments");
        await new CommentsStage(new Tools(new HttpClient(), cfg), s, NoWait).RunAsync(ctx);

        var snaps = s.Blobs.Keys.Where(k => k.StartsWith(P.Video(id, "comments/"))).ToList();
        var snapName = Assert.Single(snaps);
        Assert.Matches(@"comments/\d{8}T\d{6}Z\.json$", snapName);
        var snap = JsonDocument.Parse(s[snapName].Data).RootElement;
        Assert.Equal(3, snap.GetProperty("count").GetInt32()); // c1, c2 (top) and c3 (new); c2 once
        Assert.Equal(42, snap.GetProperty("commentCount").GetInt32());
        Assert.Equal(1, job.Round);
        Assert.Equal(Protocol.Utc(at.AddDays(1)), ctx.NextDue); // first refresh: 1 day after the download

        var calls = File.ReadAllLines(Path.Combine(dir, "calls.log"));
        Assert.Contains(calls, c => c.Contains("comment_sort=top") && c.Contains("--write-comments") && c.Contains("--skip-download"));
        Assert.Contains(calls, c => c.Contains("comment_sort=new"));
        Assert.DoesNotContain(calls, c => c.Contains("cookies"));
    }
}
