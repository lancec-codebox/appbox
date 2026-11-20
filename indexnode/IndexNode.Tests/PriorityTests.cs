using IndexNode;
using Xunit;

namespace IndexNode.Tests;

public class PriorityTests
{
    private static readonly Paths P = new("index-test/");
    private static string T(int minute) => $"2026-11-13T19:{minute:D2}:00Z";

    private static async Task Add(FakeStore s, string stage, string id, string added, int priority = 0, string? rawTag = null)
    {
        var url = "https://www.youtube.com/watch?v=" + id.PadRight(11, 'x');
        var job = stage == "download" ? JobDescriptor.NewDownload(url, 480, null) : JobDescriptor.NewStep(stage, id);
        job.JobId = id; job.AddedAt = added; job.Priority = priority;
        var tags = Tags.Job(stage, "pending", added, null, priority);
        if (rawTag != null) tags[Tags.Priority] = rawTag;
        await s.PutAsync(P.Job(stage, id), job.ToBytes(), tags, null, true, default);
    }

    private static EngineOptions Opts() => new()
    {
        NodeId = "test-abc123", Name = "test", Stages = ["download"], Workers = 1, Threads = 8, Once = true,
        PollInterval = TimeSpan.FromMilliseconds(30), RenewInterval = TimeSpan.FromMilliseconds(40), RegistryInterval = TimeSpan.FromMilliseconds(100),
        BotBackoff = TimeSpan.FromHours(1), TransientBackoff = _ => TimeSpan.Zero, ControlInterval = TimeSpan.FromMilliseconds(40),
    };

    private static EngineOptions OptsFor(string[] stages) => new()
    {
        NodeId = "test-abc123", Name = "test", Stages = stages, Workers = 1, Threads = 8, Once = true,
        PollInterval = TimeSpan.FromMilliseconds(30), RenewInterval = TimeSpan.FromMilliseconds(40), RegistryInterval = TimeSpan.FromMilliseconds(100),
        BotBackoff = TimeSpan.FromHours(1), TransientBackoff = _ => TimeSpan.Zero, ControlInterval = TimeSpan.FromMilliseconds(40),
    };




    private sealed class H(Action<JobContext> f) : IStageHandler { public Task RunAsync(JobContext c) { f(c); return Task.CompletedTask; } }

    [Fact]
    public void Priority_tag_is_only_written_when_not_0_and_must_be_in_range()
    {
        Assert.False(Tags.Job("download", "pending", T(0)).ContainsKey(Tags.Priority));
        Assert.Equal("-3", Tags.Job("download", "pending", T(0), null, -3)[Tags.Priority]);
        Assert.Throws<ArgumentOutOfRangeException>(() => Tags.Job("download", "pending", T(0), null, 1001));
        Assert.Throws<ArgumentOutOfRangeException>(() => Tags.CheckPriority(-1001));
        Assert.Equal(1000, Tags.CheckPriority(1000));
        Assert.Throws<ArgumentException>(() => PriorityTool.ParseOption("x"));
        Assert.Equal(0, PriorityTool.ParseOption(null));
    }

    // ---------------------------------------------------------------- inheritance

    private static JobContext Ctx(FakeStore s, JobDescriptor job, string stage) => new()
    {
        Job = job, Stage = stage, BlobName = P.Job(stage, job.JobId), LeaseId = "L", Worker = 1, Store = s, Paths = P, NodeId = "n-1", Token = default,
        ThreadShare = () => 1, Report = (_, _) => { },
    };



    // ---------------------------------------------------------------- priority set

    private static async Task<FakeStore> Mixed()
    {
        var s = new FakeStore();
        await Add(s, "download", "pend1", T(0)); await Add(s, "download", "pend2", T(1)); await Add(s, "download", "leased1", T(2)); await Add(s, "download", "done1", T(3)); await Add(s, "download", "fail1", T(4));
        await s.AcquireLeaseAsync(P.Job("download", "leased1"), 60, default);
        s[P.Job("download", "done1")].Tags[Tags.State] = "done"; s[P.Job("download", "fail1")].Tags[Tags.State] = "failed";
        return s;
    }






    [Fact]
    public async Task Enqueue_priority_writes_the_tag()
    {
        var s = new FakeStore();
        var job = JobDescriptor.NewDownload("https://www.youtube.com/watch?v=abcdefghij1", 480, null); job.Priority = PriorityTool.ParseOption("15");
        await s.PutAsync(P.Job("download", job.JobId), job.ToBytes(), Tags.Job("download", "pending", job.AddedAt, null, job.Priority), null, true, default);
        Assert.Equal("15", s[P.Job("download", job.JobId)].Tags[Tags.Priority]);
    }

    // ---------------------------------------------------------------- source priority

    private static async Task<JobDescriptor> Watch(FakeStore s, string id, int priority = 0)
    {
        var w = JobDescriptor.NewSource("playlist", id, "pl", "1h", ["videos"], false, 0, 360); w.Priority = priority;
        await s.PutAsync(P.Job("refresh", w.JobId), w.ToBytes(), Tags.Job("refresh", "pending", w.AddedAt, null, priority), null, true, default);
        return w;
    }

    private static async Task Video(FakeStore s, string id, params string[] sources)
    {
        await Add(s, "download", id, T(0));
        await Add(s, "comments", id, T(0)); await Add(s, "sscd", id, T(0)); await Add(s, "thumbs", id, T(0));
        if (sources.Length > 0)
        {
            var j = s.Job(P.Job("download", id)); j.Source = sources[0]; j.Sources = sources.ToList();
            await s.PutAsync(P.Job("download", id), j.ToBytes(), s[P.Job("download", id)].Tags, null, false, default);
        }
    }

    static string Tag(FakeStore s, string stage, string id) => s[P.Job(stage, id)].Tags.GetValueOrDefault(Tags.Priority) ?? "";





}
