using System.Text;
using IndexNode.Hosting;

namespace IndexNode.Android.Tests;

/// <summary>0.1.5: the app's own update, without Android: what host.json must say, when it is an update, how the download is verified, what an install result means.</summary>
public class HostUpdateTests
{
    const string Sha = "e77fce9b3eb46eaf323707319a3baea583e9a2250d5b54c6f230faea3c044a28";
    static string Json(string version, string? apk = null, string? sha = Sha, long bytes = 5) =>
        $$"""{"version":"{{version}}","apk":"{{apk ?? $"https://github.com/lancec-codebox/appbox/releases/download/indexnode-android-v{version}/indexnode-android.apk"}}","sha256":{{(sha == null ? "null" : "\"" + sha + "\"")}},"bytes":{{bytes}}}""";

    [Fact]
    public void A_newer_host_json_is_an_offer_and_the_same_or_an_older_one_is_not()
    {
        var o = HostUpdate.Parse(Json("0.1.5"), "0.1.4")!;
        Assert.Equal("0.1.5", o.Version); Assert.Equal(Sha, o.Sha256); Assert.Equal(5, o.Bytes);
        Assert.Equal("https://github.com/lancec-codebox/appbox/releases/download/indexnode-android-v0.1.5/indexnode-android.apk", o.ApkUrl);
        Assert.Null(HostUpdate.Parse(Json("0.1.5"), "0.1.5"));
        Assert.Null(HostUpdate.Parse(Json("0.1.4"), "0.1.5"));
        Assert.NotNull(HostUpdate.Parse(Json("0.1.10"), "0.1.9"));   // numbers, not text
        Assert.Null(HostUpdate.Parse(Json("0.1.5"), "not a version"));   // an app that does not know its own version is offered nothing
        Assert.True(HostUpdate.Newer("0.2.0", "0.1.99")); Assert.False(HostUpdate.Newer(null, "0.1.0")); Assert.False(HostUpdate.Newer("x", "0.1.0"));
    }

    [Fact]
    public void A_host_json_with_an_apk_from_elsewhere_or_without_a_hash_is_refused()
    {
        Assert.Throws<FormatException>(() => HostUpdate.Parse(Json("0.1.5", apk: "https://example.com/indexnode-android.apk"), "0.1.4"));
        Assert.Throws<FormatException>(() => HostUpdate.Parse(Json("0.1.5", apk: "http://github.com/lancec-codebox/appbox/releases/download/x/indexnode-android.apk"), "0.1.4"));
        Assert.Throws<FormatException>(() => HostUpdate.Parse(Json("0.1.5", apk: "https://github.com/lancec-codebox/appbox/releases/download/x/readme.txt"), "0.1.4"));
        Assert.Throws<FormatException>(() => HostUpdate.Parse(Json("0.1.5", sha: null), "0.1.4"));
        Assert.Throws<FormatException>(() => HostUpdate.Parse(Json("0.1.5", sha: "abc"), "0.1.4"));
        Assert.Throws<FormatException>(() => HostUpdate.Parse(Json("newest"), "0.1.4"));
        Assert.Throws<FormatException>(() => HostUpdate.Parse("Not Found", "0.1.4"));
        Assert.Throws<FormatException>(() => HostUpdate.Parse("[]", "0.1.4"));
    }

    [Fact]
    public void A_download_is_installed_only_when_its_size_and_hash_match()
    {
        var dir = Directory.CreateTempSubdirectory("hostupd").FullName; var f = Path.Combine(dir, "a.apk");
        File.WriteAllBytes(f, Encoding.UTF8.GetBytes("HELLO"));
        string sha; using (var s = File.OpenRead(f)) sha = HostUpdate.Sha256Hex(s);
        Assert.Equal(64, sha.Length); Assert.Equal(sha, sha.ToLowerInvariant());
        Assert.True(HostUpdate.Verify(f, new HostOffer("0.1.5", "u", sha, 5, null)));
        Assert.True(HostUpdate.Verify(f, new HostOffer("0.1.5", "u", sha, -1, null)));   // no size given: the hash decides
        Assert.False(HostUpdate.Verify(f, new HostOffer("0.1.5", "u", sha, 6, null)));
        Assert.False(HostUpdate.Verify(f, new HostOffer("0.1.5", "u", Sha, 5, null)));
        // the hash in host.json may be upper case
        Assert.Equal(sha, HostUpdate.Parse(Json("0.1.5", sha: sha.ToUpperInvariant()), "0.1.4")!.Sha256);
    }

    [Fact]
    public void An_install_result_says_what_happened()
    {
        Assert.Equal("confirm", HostUpdate.Outcome(-1, null).Kind);
        Assert.Equal("done", HostUpdate.Outcome(0, null).Kind);
        Assert.Equal("cancelled", HostUpdate.Outcome(3, null).Kind);
        var conflict = HostUpdate.Outcome(5, "INSTALL_FAILED_UPDATE_INCOMPATIBLE");
        Assert.Equal("failed", conflict.Kind); Assert.Contains("signing key", conflict.Message); Assert.Contains("INSTALL_FAILED_UPDATE_INCOMPATIBLE", conflict.Message);
        Assert.Equal("failed", HostUpdate.Outcome(1, " ").Kind); Assert.DoesNotContain("(", HostUpdate.Outcome(1, " ").Message);
    }
}
