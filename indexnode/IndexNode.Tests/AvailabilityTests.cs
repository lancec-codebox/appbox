using System.Text.Json.Nodes;

namespace IndexNode.Tests;

public class AvailabilityTests
{
    private static readonly Paths P = new("index-test/");
    private static readonly DateTimeOffset Now = new(2026, 11, 14, 12, 0, 0, TimeSpan.Zero);
    const string C1 = "UCaaaaaaaaaaaaaaaaaaaaaa", C2 = "UCbbbbbbbbbbbbbbbbbbbbbb";
    static string Id(char c) => new string(c, 11);

    private sealed class Yt
    {
        public Dictionary<string, string[]> Channels = new();     // channel id -> listed video ids
        public HashSet<string> Gone = new();                      // video ids that fail on their own
        public List<string> Calls = new();
        public Tools Tools() => new(new HttpClient()) { YtDlpOverride = (args, _, _) =>
        {
            var url = args[^1]; Calls.Add(url);
            if (url.Contains("watch?v="))
            {
                var id = url.Split('=')[1];
                return Task.FromResult(Gone.Contains(id) ? new ProcResult(1, "", $"ERROR: [youtube] {id}: Video unavailable. This video has been removed by the uploader\n") : new ProcResult(0, "public\n", ""));
            }
            var cid = url.Split("/channel/")[1].Split('/')[0];
            return Task.FromResult(new ProcResult(0, string.Join("\n", Channels.GetValueOrDefault(cid, [])) + "\n", ""));
        } };
    }

    private static async Task Track(FakeStore s, string id, string channel)
    {
        var q = new JobQueue(s, P, "t"); await q.LoadAsync(default);
        await q.AppendLinesAsync([QLine.Add(id, "download", new JobDescriptor { Stage = "download", JobId = id }, "2026-11-14T00:00:00Z", null, null, "done")], default);
        await s.PutAsync(P.Video(id, "info.json"), System.Text.Encoding.UTF8.GetBytes($$"""{"channel_id":"{{channel}}","channel":"Name {{channel[2]}}"}"""), null, null, false, default);
    }

    private static async Task<JobQueue> Loaded(FakeStore s) { var q = new JobQueue(s, P, "t"); await q.LoadAsync(default); return q; }

    private static async Task<JobContext> Sweep(FakeStore s, Yt yt)
    {
        var job = AvailabilityStage.NewSweep();
        var ctx = new JobContext { Job = job, Stage = "availability", BlobName = job.JobId, LeaseId = "l", Worker = 1, Queue = await Loaded(s), Store = s, Paths = P, NodeId = "n", Token = default, ThreadShare = () => 1, Report = (_, _) => { } };
        await new AvailabilityStage(yt.Tools(), s, () => Now).RunAsync(ctx);
        return ctx;
    }

    static JsonObject J(FakeStore s, string name) => (JsonNode.Parse(s.Blobs[name].Data) as JsonObject)!;

    [Fact]
    public async Task One_listing_per_channel_and_only_the_missing_video_is_checked_alone_then_restored_when_listed_again()
    {
        var s = new FakeStore(); var yt = new Yt();
        await Track(s, Id('a'), C1); await Track(s, Id('b'), C1); await Track(s, Id('c'), C1);
        yt.Channels[C1] = [Id('a'), Id('b'), Id('x')]; yt.Gone.Add(Id('c'));
        var ctx = await Sweep(s, yt);
        Assert.Equal(2, yt.Calls.Count);                                                        // the channel listing + one check of the missing video
        var st = J(s, AvailabilityStage.StateBlob(P, Id('c')));
        Assert.False((bool)st["available"]!); Assert.Contains("removed by the uploader", st["reason"]!.ToString());
        Assert.False(s.Exists(AvailabilityStage.StateBlob(P, Id('a'))));                        // listed: nothing written
        var sum = J(s, AvailabilityStage.SummaryBlob(P));
        Assert.Equal(3, (int)sum["tracked"]!); Assert.Single(sum["unavailable"]!.AsArray());
        Assert.Equal("2026-11-15T12:00:00Z", ctx.NextDue);                                      // daily

        yt.Channels[C1] = [Id('a'), Id('b'), Id('c')]; yt.Calls.Clear();
        await Sweep(s, yt);
        Assert.Single(yt.Calls);                                                                 // the channel is listed; nothing checked alone
        Assert.True((bool)J(s, AvailabilityStage.StateBlob(P, Id('c')))["available"]!);
        Assert.Empty(J(s, AvailabilityStage.SummaryBlob(P))["unavailable"]!.AsArray());
    }

    [Fact]
    public async Task Channels_are_check_only_unless_a_watch_job_ingests_them_and_the_sweep_never_starts_one()
    {
        var s = new FakeStore(); var yt = new Yt();
        await Track(s, Id('a'), C1); await Track(s, Id('b'), C2);
        yt.Channels[C1] = [Id('a')]; yt.Channels[C2] = [Id('b')];
        var w = JobDescriptor.NewSource("channel", C2, "Two", "1h", ["videos"], false, 0, 480);
        await (await Loaded(s)).AppendLinesAsync([QLine.Add(w.JobId, "refresh", w, "2026-11-14T00:00:00Z")], default);
        await Sweep(s, yt);
        var modes = J(s, AvailabilityStage.SummaryBlob(P))["channelList"]!.AsArray().ToDictionary(c => c!["id"]!.ToString(), c => c!["mode"]!.ToString());
        Assert.Equal("check", modes[C1]); Assert.Equal("ingest", modes[C2]);
        Assert.False(s.InQueue(P.Job("refresh", "ch-" + C1)));
        Assert.Equal(C1, J(s, AvailabilityStage.MembersBlob(P))[Id('a')]!.ToString());           // learned once, remembered
    }

    [Fact]
    public async Task A_sweep_of_many_uploads_reads_no_state_blob_per_upload()
    {
        var s = new FakeStore(); var yt = new Yt();
        var ids = Enumerable.Range(0, 60).Select(i => "v" + i.ToString("D10")).ToArray();
        foreach (var id in ids) await Track(s, id, C1);
        yt.Channels[C1] = ids;
        int gets = s.Gets.Count; await Sweep(s, yt);
        Assert.DoesNotContain(s.Gets.Skip(gets), g => g.EndsWith("/availability.json") && ids.Any(id => g.Contains("/" + id + "/")));   // no per-upload state read
    }
}
