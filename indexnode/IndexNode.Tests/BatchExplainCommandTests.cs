using Xunit;

namespace IndexNode.Tests;

public class BatchExplainCommandTests
{
    private static readonly Paths P = new("index-test/");

    private static async Task Job(FakeStore s, string id, string added, string[]? needs)
    {
        var job = JobDescriptor.NewStep("frames", id); job.AddedAt = added; job.Needs = needs;
        await s.PutAsync(P.Job("frames", id), job.ToBytes(), Tags.Job("frames", "pending", added, null, 0), null, true, default);
    }

    [Theory]
    [InlineData("batch", "explain")]
    [InlineData("batch", "explain", "--top", "8")]
    [InlineData("batch", "explain", "--top=8")]
    [InlineData("batch", "explain", "--stages", "frames,sscd")]
    public async Task Explain_runs_with_any_of_its_argument_spellings(params string[] argv)
    {
        var s = new FakeStore();
        await Job(s, "blocked", "2026-11-17T09:00:00Z", ["asr/sensevoice.zh.json"]);
        await Job(s, "fine", "2026-11-17T10:00:00Z", null);
        var before = s.Blobs.Keys.OrderBy(k => k).ToList(); var lines = new List<string>();
        var a = Args.Parse(argv); Assert.Equal("batch", a.Positional[0]); a.Positional.RemoveAt(0);   // what Main does before dispatching
        Assert.Equal("explain", a.Positional.FirstOrDefault());
        var rc = await Commands.ExplainAsync(a, new NodeConfig(), s, P, lines.Add);
        Assert.Equal(0, rc);
        Assert.DoesNotContain(lines, l => l.StartsWith("usage"));
        Assert.Contains(lines, l => l.Contains("blocked") && l.Contains("-> no")); Assert.Contains(lines, l => l.Contains("need asr/sensevoice.zh.json missing"));
        Assert.Contains(lines, l => l.Contains("batch picks") && l.Contains("fine"));
        Assert.Equal(before, s.Blobs.Keys.OrderBy(k => k).ToList());   // read-only
    }

    [Fact]
    public async Task Without_the_explain_subcommand_it_prints_usage_and_fails()
    {
        var err = new StringWriter(); var old = Console.Error; Console.SetError(err);
        try { Assert.Equal(2, await Commands.BatchAsync(Args.Parse([]))); Assert.Contains("usage: indexnode batch explain", err.ToString()); }
        finally { Console.SetError(old); }
    }
}
