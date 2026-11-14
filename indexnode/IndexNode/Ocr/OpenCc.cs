using System.Reflection;

namespace IndexNode;

/// <summary>
/// Traditional to simplified Chinese (OpenCC's <c>t2s</c>), from OpenCC's own dictionary files (Apache-2.0): TSPhrases and TSCharacters,
/// longest match first. No native OpenCC. When a key has several values, the first one wins, as in OpenCC.
/// </summary>
public sealed class OpenCc
{
    private readonly Dictionary<string, string> map;
    private readonly int maxKey;

    public OpenCc(IEnumerable<string> dictionaryTexts)
    {
        map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var text in dictionaryTexts)
            foreach (var raw in text.Split('\n'))
            {
                var line = raw.TrimEnd('\r');
                if (line.Length == 0 || line[0] == '#') continue;
                var tab = line.IndexOf('\t');
                if (tab <= 0) continue;
                var key = line[..tab];
                var value = line[(tab + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
                if (value == null) continue;
                map.TryAdd(key, value);
            }
        maxKey = map.Count == 0 ? 0 : map.Keys.Max(k => k.Length);
    }

    private static OpenCc? t2s;

    /// <summary>The shipped t2s dictionaries (embedded resources).</summary>
    public static OpenCc T2S => t2s ??= new OpenCc(new[] { "TSPhrases", "TSCharacters" }.Select(n =>
    {
        using var s = typeof(OpenCc).Assembly.GetManifestResourceStream($"opencc.{n}.txt") ?? throw new InvalidOperationException($"missing resource opencc.{n}.txt");
        using var r = new StreamReader(s);
        return r.ReadToEnd();
    }));

    public string Convert(string text)
    {
        if (text.Length == 0 || map.Count == 0) return text;
        var sb = new System.Text.StringBuilder(text.Length);
        int i = 0;
        while (i < text.Length)
        {
            int max = Math.Min(maxKey, text.Length - i);
            bool hit = false;
            for (int len = max; len >= 1; len--)
            {
                // do not split a surrogate pair
                if (len < text.Length - i && char.IsLowSurrogate(text[i + len])) continue;
                if (map.TryGetValue(text.Substring(i, len), out var v)) { sb.Append(v); i += len; hit = true; break; }
            }
            if (!hit) { sb.Append(text[i]); i++; }
        }
        return sb.ToString();
    }
}
