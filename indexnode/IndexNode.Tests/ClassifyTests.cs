using System.Text.Json.Nodes;

namespace IndexNode.Tests;

public class ClassifyTests
{
    private static readonly Paths P = new("index-test/");

    private sealed class Chat(Func<string, string> answer) : IChat
    {
        public readonly List<string> Prompts = [];
        public Task<string> ChatAsync(string model, JsonNode content, CancellationToken ct) { var p = content.GetValue<string>(); Prompts.Add(p); return Task.FromResult(answer(p)); }
    }

    private static JobDescriptor Job(params ClassifyEntry[] e) =>
        JobDescriptor.NewClassify(JobDescriptor.NewWatch("channel", "UCaaaaaaaaaaaaaaaaaaaaaa", "Drama Channel", "1h", ["videos"], false, 0, 480), e.ToList(), "2026-11-14T12:00:00Z");

    private static ClassifyEntry E(string id, double? minutes, string title) => new() { Id = id, Title = title, Duration = minutes * 60 };

    private static async Task Run(FakeStore s, JobDescriptor job, IChat? chat)
    {
        var ctx = new JobContext { Job = job, Stage = "classify", BlobName = P.Job("classify", job.JobId), LeaseId = "l", Worker = 1, Store = s, Paths = P, NodeId = "n", Token = default, ThreadShare = () => 1, Report = (_, _) => { } };
        await new ClassifyStage(s) { Ai = () => chat == null ? null : (chat, "m") }.RunAsync(ctx);
    }

    [Fact]
    public async Task Short_uploads_are_rejected_by_rule_and_the_rest_go_to_the_ai_in_one_call_and_candidates_get_download_jobs()
    {
        var s = new FakeStore();
        var chat = new Chat(_ => """{"videos":[{"id":"aaaaaaaaaaa","candidate":true,"kind":"full","language":"zh","reason":"whole drama"},{"id":"bbbbbbbbbbb","candidate":false,"kind":"recap","reason":"recap"}]}""");
        await Run(s, Job(E("aaaaaaaaaaa", 120, "Full Movie"), E("bbbbbbbbbbb", 25, "Recap of ep 1-80"), E("ccccccccccc", 2, "Trailer")), chat);
        Assert.Single(chat.Prompts);
        Assert.DoesNotContain("ccccccccccc", chat.Prompts[0]);                                   // too short: decided by rule, not asked
        Assert.True(s.Exists(P.Job("download", "aaaaaaaaaaa")));
        Assert.False(s.Exists(P.Job("download", "bbbbbbbbbbb")));
        Assert.False(s.Exists(P.Job("download", "ccccccccccc")));
        Assert.Equal("rule", JsonNode.Parse(s.Blobs[ClassifyStage.Blob(P, "ccccccccccc")].Data)!["by"]!.ToString());
        Assert.Equal("ai", JsonNode.Parse(s.Blobs[ClassifyStage.Blob(P, "aaaaaaaaaaa")].Data)!["by"]!.ToString());
    }

    [Fact]
    public async Task A_stored_decision_is_never_asked_again()
    {
        var s = new FakeStore();
        var chat = new Chat(_ => """{"videos":[{"id":"aaaaaaaaaaa","candidate":true,"kind":"full","reason":"x"}]}""");
        await Run(s, Job(E("aaaaaaaaaaa", 90, "Drama")), chat);
        await s.DeleteAsync(P.Job("download", "aaaaaaaaaaa"), default);
        await Run(s, Job(E("aaaaaaaaaaa", 90, "Drama")), chat);
        Assert.Single(chat.Prompts);                                                             // second run used the stored decision
        Assert.True(s.Exists(P.Job("download", "aaaaaaaaaaa")));
    }

    [Fact]
    public async Task A_node_without_ai_passes_the_job_on_and_an_incomplete_answer_leaves_the_upload_undecided()
    {
        var s = new FakeStore();
        await Assert.ThrowsAsync<JobSkipped>(() => Run(s, Job(E("aaaaaaaaaaa", 90, "Drama")), null));
        Assert.False(s.Exists(ClassifyStage.Blob(P, "aaaaaaaaaaa")));
        var partial = new Chat(_ => """{"videos":[]}""");
        await Run(s, Job(E("aaaaaaaaaaa", 90, "Drama")), partial);   // 0.10.1: an omission never fails the job
        Assert.False(s.Exists(ClassifyStage.Blob(P, "aaaaaaaaaaa")));   // asked again, still no answer: undecided
        // only short uploads: no AI needed at all
        await Run(s, Job(E("ddddddddddd", 1, "Short")), null);
        Assert.True(s.Exists(ClassifyStage.Blob(P, "ddddddddddd")));
    }
}
