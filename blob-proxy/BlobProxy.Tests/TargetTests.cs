using BlobProxy;
using Xunit;

public class TargetTests
{
    [Theory]
    [InlineData("/acct-media/index/videos/x/video.json", "acct", "media", "/index/videos/x/video.json")]
    [InlineData("/acct-media", "acct", "media", "")]
    [InlineData("/acct-media/", "acct", "media", "/")]
    [InlineData("/st0rage99-drama-index-v2/a/b", "st0rage99", "drama-index-v2", "/a/b")]   // the container keeps its own hyphens
    [InlineData("/acct-$root/x", "acct", "$root", "/x")]
    public void The_first_hyphen_ends_the_account(string path, string account, string container, string blob)
    {
        var t = Target.Parse(path);
        Assert.NotNull(t);
        Assert.Equal((account, container, blob), (t!.Account, t.Container, t.BlobPath));
        Assert.Equal($"https://{account}.blob.core.windows.net/{container}", t.Prefix("blob.core.windows.net"));
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/healthz")]
    [InlineData("/v1/stats")]
    [InlineData("/-media/x")]          // no account
    [InlineData("/acct-/x")]           // no container
    [InlineData("/Acct-media/x")]      // account names are lowercase
    [InlineData("/ab-media/x")]        // too short
    [InlineData("/acct-me--dia/x")]    // two hyphens in a row
    [InlineData("/acct-m/x")]          // container too short
    [InlineData("/acct.evil.com-media/x")]
    [InlineData("")]
    public void Anything_else_is_not_a_storage_path(string path) => Assert.Null(Target.Parse(path));

    [Fact]
    public void The_allowlist_takes_commas_or_spaces_and_lowercases() =>
        Assert.Equal(new HashSet<string> { "acct", "other" }, Target.Allowlist(" Acct, other ;"));

    [Theory]
    [InlineData("GET", "list", false, "list")]
    [InlineData("GET", null, false, "get")]
    [InlineData("GET", null, true, "range")]
    [InlineData("HEAD", null, false, "head")]
    [InlineData("PUT", "appendblock", false, "append")]
    [InlineData("PUT", "block", false, "block")]
    [InlineData("PUT", "blocklist", false, "block")]
    [InlineData("PUT", null, false, "put")]
    [InlineData("PUT", "lease", false, "lease")]
    [InlineData("GET", "tags", false, "tags")]
    [InlineData("DELETE", null, false, "delete")]
    [InlineData("PUT", "metadata", false, "put:metadata")]
    public void Operations_are_classed(string method, string? comp, bool range, string op) => Assert.Equal(op, Stats.Op(method, comp, range));

    [Fact]
    public void Stats_add_up_by_client_and_window()
    {
        var s = new Stats(TimeProvider.System);
        s.Record(new("proc-18", "range", "acct-media", "index/videos"), 0, 1000, 10, 206);
        s.Record(new("proc-18", "put", "acct-media", "index/jobs"), 500, 0, 30, 201);
        s.Record(new("ip:1.2.3.4", "get", "acct-media", "index/search"), 0, 7, 5, 404);
        var snap = s.Snapshot();
        var all = snap["last5m"]!["all"]![0]!;
        Assert.Equal(3L, (long)all["requests"]!); Assert.Equal(1007L, (long)all["bytesOut"]!); Assert.Equal(500L, (long)all["bytesIn"]!); Assert.Equal(1L, (long)all["4xx"]!);
        var proc = snap["sinceStart"]!["byClient"]!.AsArray().First(r => (string?)r!["key"] == "proc-18")!;
        Assert.Equal(2L, (long)proc["requests"]!);
        Assert.Equal("index/videos", Stats.Folder("/index/videos/x/frames.zip")); Assert.Equal("(container)", Stats.Folder(""));
    }
}
