namespace IndexNode.Tests;

public class OneOffTests
{
    private static readonly Paths P = new("index-test/");

    [Theory]
    [InlineData("https://m.youtube.com/watch?v=rjzV__OHNR0&pp=iggCQAE%3D", "rjzV__OHNR0")]
    [InlineData("https://youtu.be/rjzV__OHNR0?si=x", "rjzV__OHNR0")]
    [InlineData("https://www.youtube.com/shorts/GI7M1ss1pOw", "GI7M1ss1pOw")]
    [InlineData("https://www.youtube.com/playlist?list=PLKWDudJP7NKE", null)]
    public void A_video_id_comes_from_any_single_video_url(string url, string? id) => Assert.Equal(id, YtDlp.VideoId(url));

    private static async Task<(FakeStore Store, JobContext Ctx)> Downloaded(bool queue)
    {
        var s = new FakeStore(); const string id = "rjzV__OHNR0";
        await s.PutAsync(P.Video(id, "downloaded.json"), "{}"u8.ToArray(), null, null, false, default);   // already stored: only the follow-up jobs are made
        var job = JobDescriptor.NewDownload($"https://www.youtube.com/watch?v={id}", 480, null); job.JobId = id;
        var ctx = new JobContext { Job = job, Stage = "download", BlobName = P.Job("download", id), LeaseId = "", Worker = 0, Store = s, Paths = P, NodeId = "n", Token = default, ThreadShare = () => 1, Report = (_, _) => { } };
        var offline = new Tools(new HttpClient()) { YtDlpOverride = (_, _, _) => Task.FromResult(new ProcResult(1, "", "ERROR: offline")) };   // the comments fetch fails here; the download must not
        await new DownloadStage(offline, s, new DownloadGate()) { QueueProcessing = queue }.RunAsync(ctx);
        return (s, ctx);
    }

    [Fact]
    public async Task No_process_stores_the_video_without_queueing_any_processing_step()
    {
        var (off, offCtx) = await Downloaded(false);
        Assert.DoesNotContain(offCtx.FollowUps, l => l.Stage == "process");
        Assert.DoesNotContain(off.Blobs.Keys, k => k.Contains("/jobs/"));                 // no job blobs at all since 0.13.0
        var (_, onCtx) = await Downloaded(true);
        Assert.Contains(onCtx.FollowUps, l => l.T == "add" && l.Stage == "process" && l.S == "rjzV__OHNR0");
        Assert.DoesNotContain(onCtx.FollowUps, l => l.Stage == "comments");               // comments are part of the download, not a job
    }
}
