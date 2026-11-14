namespace IndexNode.Tests;

public class WordBreakTests
{
    [Theory]
    [InlineData("我清楚他的改变都是为了我丞相道这就是丞相", "丞相")]
    [InlineData("他其实根本就不知道这件事情到底是怎么回事", "事情")]
    public void Boundaries_keep_known_words_whole(string s, string word)
    {
        var b = WordSeg.Boundaries(s); int i = s.IndexOf(word, StringComparison.Ordinal);
        Assert.True(b[i]); Assert.False(b[i + 1]); Assert.True(b[i + word.Length]);
    }

    [Fact]
    public void A_cue_is_not_broken_inside_a_word_like_the_chengxiang_case()
    {
        const string text = "我清楚他的改变都是为了我丞相道这就是丞相";
        var t = Enumerable.Range(0, text.Length).Select(i => 10 + i * 0.3).ToArray();
        var cues = CueShaper.Shape([new ShapeCue(10, 10 + text.Length * 0.3, text, t)]);
        Assert.Equal(0, CueShaper.Stats(cues).OverChars);
        var joined = string.Join("|", cues.SelectMany(c => c.Text.Split('\n')));
        Assert.DoesNotContain("丞|相", joined);
        Assert.DoesNotContain("丞\n相", string.Join("\n", cues.Select(c => c.Text)));
    }

    [Fact]
    public void A_two_line_cue_breaks_near_the_middle_at_a_word_boundary()
    {
        var cues = CueShaper.Shape([new ShapeCue(0, 5, "我清楚他的改变都是为了我丞相道这就是丞相")]);
        Assert.Single(cues);
        var l = cues[0].Text.Split('\n'); Assert.Equal(2, l.Length);
        Assert.True(Math.Abs(l[0].Length - l[1].Length) <= 4, cues[0].Text);
        Assert.False(l[0].EndsWith("丞") && l[1].StartsWith("相"), cues[0].Text);
    }

    [Fact]
    public void A_word_over_a_line_is_the_only_thing_cut_by_characters()
    {
        var text = new string('啊', 25);
        var cues = CueShaper.Shape([new ShapeCue(0, 5, text)]);
        Assert.Equal(0, CueShaper.Stats(cues).OverChars);
        Assert.Equal(text, string.Concat(cues.SelectMany(c => c.Text.Split('\n'))));
    }
}
