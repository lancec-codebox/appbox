using System.Text.Json;
using System.Text.Json.Nodes;

namespace IndexNode.Tests;

/// <summary>0.10.3: add queues a lookup due now; the old watch stage, jobs and control key still work; schedules; manual lookup/refresh; members.json.</summary>
public class RefreshScheduleTests
{
    private static readonly Paths P = new("index-test/");
    private static readonly DateTimeOffset Now = new(2026, 11, 17, 12, 0, 0, TimeSpan.Zero);
    private static string Id(char c) => new string(c, 11);
    private static string PlUrl(string id) => $"https://www.youtube.com/playlist?list={id}";
    private static string Lines(params string[] ids) => string.Join("\n", ids.Select(i => i + "\tNA\tNA\t60\tNA\tTitle " + i[0])) + "\n";

    private sealed class Fake
    {
        public Dictionary<string, string> Listings = new();
        public List<string> Calls = new();
        public Tools Tools() => new(new HttpClient()) { YtDlpOverride = (args, _, _) => { Calls.Add(string.Join(' ', args)); return Task.FromResult(new ProcResult(0, Listings.GetValueOrDefault(args[^1], ""), "")); } };
    }

    private static JobDescriptor Pl(string id, string schedule = "once", int backfill = -1) => JobDescriptor.NewSource("playlist", id, "pl " + id, schedule, ["videos"], false, backfill, 480);

    private static async Task<JobContext> RunJob(FakeStore s, Fake f, string sourceId, DateTimeOffset? at = null)
    {
        var name = P.Job("refresh", sourceId);
        var job = s.Job(name);
        var ctx = new JobContext { Job = job, Stage = "refresh", BlobName = name, LeaseId = "l", Worker = 1, Store = s, Paths = P, NodeId = "n", Token = default, ThreadShare = () => 1, Report = (_, _) => { } };
        await new RefreshStage(f.Tools(), s, () => at ?? Now).RunAsync(ctx);
        s.Blobs[name].Data = job.ToBytes(); // what the engine writes back
        return ctx;
    }

    // ---- add queues a lookup due now, at the high priority

    [Fact]
    public async Task A_new_source_defaults_to_the_schedule_once()
    {
        var j = Pl("PLd"); Assert.Equal("once", j.Schedule); Assert.Null(j.Every);
        Assert.Equal("1d", Pl("PLe", "1d").Every);
        Assert.Equal("once", Schedule.Of(j).Text);
    }


    // ---- lookup is a full listing; refresh is incremental




    // ---- schedules
    [Theory]
    [InlineData("once", "once")] [InlineData("off", "off")] [InlineData("", "once")] [InlineData("6h", "6h")] [InlineData("1d", "1d")] [InlineData("every 2h", "2h")] [InlineData("daily@03:00", "daily@03:00")] [InlineData("daily@3:30", "daily@03:30")]
    public void Schedules_parse(string input, string text) => Assert.Equal(text, Schedule.Parse(input).Text);

    [Theory] [InlineData("sometimes")] [InlineData("daily@25:00")] [InlineData("0h")] [InlineData("daily@")]
    public void Bad_schedules_are_refused(string input) => Assert.Throws<ArgumentException>(() => Schedule.Parse(input));

    [Fact]
    public void Next_time_of_each_schedule()
    {
        Assert.Null(Schedule.Parse("once").Next(Now)); Assert.Null(Schedule.Parse("off").Next(Now));
        Assert.Equal(Now.AddHours(6), Schedule.Parse("6h").Next(Now));
        Assert.Equal(new DateTimeOffset(2026, 11, 18, 3, 0, 0, TimeSpan.Zero), Schedule.Parse("daily@03:00").Next(Now));                       // 12:00 is past 03:00: tomorrow
        Assert.Equal(new DateTimeOffset(2026, 11, 17, 13, 30, 0, TimeSpan.Zero), Schedule.Parse("daily@13:30").Next(Now));                      // later today
    }




    [Fact]
    public async Task Existing_sources_keep_their_every_interval_as_the_schedule()
    {
        var old = new JobDescriptor { Stage = "watch", JobId = "ch-UColdoldoldoldoldoldoldo", Kind = "channel", Id = "UColdoldoldoldoldoldoldo", Name = "Old", Every = "1d", Seen = [Id('a')], AddedAt = "2026-11-01T00:00:00Z" };
        Assert.Equal("every", Schedule.Of(old).Kind); Assert.Equal(TimeSpan.FromDays(1), Schedule.Of(old).Interval);
        Assert.Equal("refresh", RefreshStage.KindOf(old));           // it has run before: a refresh, not a lookup
        Assert.Equal("lookup", RefreshStage.KindOf(new JobDescriptor { Every = "1d" })); // never run: a lookup
    }

    // ---- the old watch name


    [Fact]
    public void The_watch_stage_name_and_control_key_mean_refresh()
    {
        Assert.Equal(["refresh"], Protocol.ExpandStages(["watch"]));
        Assert.Equal(["download", "refresh"], Protocol.ExpandStages(["download", "watch", "refresh"]));
        Assert.Equal("refresh", Protocol.CanonicalStage("WATCH"));
        var c = ControlFile.Parse(System.Text.Encoding.UTF8.GetBytes("""{"mode":"run","stages":{"watch":"stop"}}"""));
        Assert.Equal(Level.Stop, c.Effective("refresh")); Assert.Equal(Level.Run, c.Effective("download"));
        var both = ControlFile.Parse(System.Text.Encoding.UTF8.GetBytes("""{"stages":{"watch":"stop","refresh":"run"}}"""));
        Assert.Equal(Level.Run, both.Effective("refresh")); // the new key wins
    }

    [Fact]
    public async Task Control_written_for_the_old_stage_name_stops_the_refresh_stage()
    {
        var s = new FakeStore();
        var c = await ControlWatcher.WriteAsync(s, P, "stop", "watch", null, "t", default);
        Assert.Equal(Level.Stop, c.Effective("refresh"));
    }

    // ---- manual lookup and refresh




    // ---- members.json

}
