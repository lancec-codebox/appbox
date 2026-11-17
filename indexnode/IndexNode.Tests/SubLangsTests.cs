namespace IndexNode.Tests;

public class SubLangsTests
{
    static string Info(string[] subs, string[] autos) =>
        "{\"subtitles\":{" + string.Join(",", subs.Select(k => $"\"{k}\":[{{}}]")) + "},\"automatic_captions\":{" + string.Join(",", autos.Select(k => $"\"{k}\":[{{}}]")) + "}}";

    [Fact]
    public void Uploaded_en_and_zh_are_requested_by_exact_key_and_the_original_auto_track_only()
    {
        var p = SubLangs.Plan(Info(["en", "en-US", "zh-Hans", "fr", "ja", "zh"], ["zh-orig", "zh-Hans", "en", "en-ar", "en-ja"]));
        Assert.Equal(["zh", "zh-Hans", "en", "en-US"], p.Manual);
        Assert.Equal(["zh-orig"], p.Auto);
        Assert.Equal("zh,zh-Hans,en,en-US", p.ManualArg);
    }

    [Fact]
    public void Only_automatic_zh_orig_with_a_hundred_translations_asks_for_that_one_track()
    {
        var autos = Enumerable.Range(0, 100).Select(i => "tr" + i).Concat(["zh-orig", "zh-Hans", "zh-TW", "en", "en-ar"]).ToArray();
        var p = SubLangs.Plan(Info([], autos));
        Assert.Empty(p.Manual);
        Assert.Equal(["zh-orig"], p.Auto);
    }

    [Fact]
    public void An_english_original_and_real_en_uploads_and_no_regex()
    {
        var p = SubLangs.Plan(Info(["en-GB", "en-US", "en", "zh-CN"], ["en-orig", "en", "en-zh", "zh-Hans"]));
        Assert.Equal(["zh-CN", "en", "en-GB", "en-US"], p.Manual);
        Assert.Equal(["en-orig"], p.Auto);
        Assert.All(p.Manual.Concat(p.Auto), l => Assert.DoesNotContain('*', l));
    }

    [Fact]
    public void No_subtitles_no_info_or_a_broken_info_ask_for_nothing()
    {
        var none = SubLangs.Plan(Info([], []));
        Assert.Empty(none.Manual); Assert.Empty(none.Auto);
        foreach (var bad in new string?[] { null, "", "not json", "[]", "{\"id\":\"x\"}" }) { var p = SubLangs.Plan(bad); Assert.Empty(p.Manual); Assert.Empty(p.Auto); }
    }
}
