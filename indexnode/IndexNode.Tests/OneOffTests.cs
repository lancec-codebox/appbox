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

    private static async Task<FakeStore> Downloaded(bool queue)
    {
        var s = new FakeStore(); const string id = "rjzV__OHNR0";
        await s.PutAsync(P.Video(id, "downloaded.json"), "{}"u8.ToArray(), null, null, false, default);   // already stored: only the follow-up jobs are made
        var job = JobDescriptor.NewDownload($"https://www.youtube.com/watch?v={id}", 480, null); job.JobId = id;
        var ctx = new JobContext { Job = job, Stage = "download", BlobName = P.Job("download", id), LeaseId = "", Worker = 0, Store = s, Paths = P, NodeId = "n", Token = default, ThreadShare = () => 1, Report = (_, _) => { } };
        await new DownloadStage(new Tools(new HttpClient()), s, new DownloadGate()) { QueueProcessing = queue }.RunAsync(ctx);
        return s;
    }

    [Fact]
    public async Task No_process_stores_the_video_without_queueing_any_processing_step()
    {
        var off = await Downloaded(false);
        Assert.DoesNotContain(off.Blobs.Keys, k => Protocol.StepStages.Any(st => k.Contains($"/jobs/{st}/")));
        Assert.True(off.Exists(P.Job("comments", "rjzV__OHNR0")));                             // comments are not processing: still fetched
        var on = await Downloaded(true);
        Assert.True(on.Exists(P.Job("frames", "rjzV__OHNR0")));
    }
}
