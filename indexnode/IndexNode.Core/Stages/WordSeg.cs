using System.IO.Compression;
using System.Reflection;

namespace IndexNode;

/// <summary>
/// Chinese word boundaries for <see cref="CueShaper"/>, so a cue is never broken inside a word. A small built-in port of jieba's core:
/// the dictionary is jieba's (MIT, words with frequency of 10 or more, gzipped as the embedded resource zh-words.txt.gz) and the cut is the
/// highest-probability route through the word graph (dynamic programming over word frequencies). No HMM for unknown words: those stay single characters.
/// </summary>
public static class WordSeg
{
    private const int MaxWord = 12;
    private static readonly Lazy<(Dictionary<string, double> Logp, double Unknown)> Dict = new(Load);

    private static (Dictionary<string, double>, double) Load()
    {
        var words = new Dictionary<string, long>(110_000); long total = 0;
        using var s = typeof(WordSeg).Assembly.GetManifestResourceStream("zh-words.txt.gz")!;
        using var gz = new GZipStream(s, CompressionMode.Decompress);
        using var rd = new StreamReader(gz);
        for (string? line; (line = rd.ReadLine()) != null;)
        {
            int sp = line.LastIndexOf(' ');
            if (sp <= 0 || !long.TryParse(line.AsSpan(sp + 1), out var f)) continue;
            words[line[..sp]] = f; total += f;
        }
        double lt = Math.Log(total);
        return (words.ToDictionary(kv => kv.Key, kv => Math.Log(kv.Value) - lt), -lt);
    }

    private static bool IsAsciiWord(char c) => c < 128 && char.IsLetterOrDigit(c);
    private static bool IsText(char c) => !char.IsWhiteSpace(c) && !char.IsPunctuation(c) && !char.IsSymbol(c) && !IsAsciiWord(c);

    /// <summary><c>b[i]</c> is true when a break between character i-1 and i is at a word boundary (b[0] and b[n] are true).</summary>
    public static bool[] Boundaries(string s)
    {
        var b = new bool[s.Length + 1]; b[0] = b[s.Length] = true;
        int i = 0;
        while (i < s.Length)
        {
            int j = i + 1;
            if (IsAsciiWord(s[i])) { while (j < s.Length && IsAsciiWord(s[j])) j++; }
            else if (IsText(s[i])) { while (j < s.Length && IsText(s[j])) j++; Cut(s, i, j, b); }
            b[i] = true; b[j] = true;
            i = j;
        }
        return b;
    }

    private static void Cut(string s, int a, int e, bool[] b)
    {
        var (logp, unknown) = Dict.Value; int n = e - a;
        var best = new double[n + 1]; var next = new int[n + 1];
        for (int i = n - 1; i >= 0; i--)
        {
            best[i] = double.NegativeInfinity;
            for (int len = 1; len <= Math.Min(MaxWord, n - i); len++)
            {
                double lp;
                if (logp.TryGetValue(s.Substring(a + i, len), out var v)) lp = v;
                else if (len == 1) lp = unknown;
                else continue;
                if (lp + best[i + len] > best[i]) { best[i] = lp + best[i + len]; next[i] = i + len; }
            }
        }
        for (int i = 0; i < n; i = next[i]) b[a + i] = true;
    }

    /// <summary>The longest run of characters (in a word, not counting punctuation) between two boundaries, for the over-long-word fallback.</summary>
    public static int LongestWord(bool[] b)
    {
        int best = 0, last = 0;
        for (int i = 1; i < b.Length; i++) if (b[i]) { best = Math.Max(best, i - last); last = i; }
        return best;
    }
}
