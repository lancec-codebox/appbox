using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace IndexNode;

/// <summary>An AI prompt kept as a readable YAML file (Prompts/*.yaml, embedded). The C# builds a request only from these files plus the drama's data: no prompt text lives in a C# literal.
/// The file format is a small YAML subset: "key: value" (plain or quoted), "key: |" and "key: |-" block scalars for prose, "key:" followed by "- item" lines for lists, and "#" comment lines.
/// Placeholders are {name} (a letter, then letters or digits); a brace followed by anything else, such as a JSON object, is plain text.</summary>
public sealed class PromptFile
{
    static readonly Regex Placeholder = new(@"\{([A-Za-z][A-Za-z0-9]*)\}", RegexOptions.Compiled);
    static readonly Dictionary<string, PromptFile> Cache = new();

    public string Name { get; }
    readonly Dictionary<string, string> text = new();
    readonly Dictionary<string, List<string>> lists = new();

    PromptFile(string name, string source) { Name = name; Parse(source); }

    /// <summary>The embedded prompt file "name" (Prompts/name.yaml), parsed once. A missing file or a malformed one fails with its name.</summary>
    public static PromptFile Load(string name)
    {
        lock (Cache)
        {
            if (Cache.TryGetValue(name, out var f)) return f;
            var asm = typeof(PromptFile).Assembly; var res = "prompts/" + name + ".yaml";
            using var s = asm.GetManifestResourceStream(res) ?? throw new InvalidOperationException($"prompt file {name}.yaml is not embedded in the build (resource {res})");
            using var r = new StreamReader(s, Encoding.UTF8);
            return Cache[name] = Parse(name, r.ReadToEnd());
        }
    }

    /// <summary>Parses prompt YAML text (for tests and for Load).</summary>
    public static PromptFile Parse(string name, string source) => new(name, source);

    public string Version => Text("version");
    public bool Has(string key) => text.ContainsKey(key) || lists.ContainsKey(key);

    /// <summary>A text field as written, a block scalar without its final newline.</summary>
    public string Text(string key) => text.TryGetValue(key, out var v) ? v : throw new InvalidOperationException($"prompt {Name}.yaml: missing field '{key}'");

    public IReadOnlyList<string> List(string key) => lists.TryGetValue(key, out var v) ? v : throw new InvalidOperationException($"prompt {Name}.yaml: missing list '{key}'");

    /// <summary>The placeholder names a text field uses.</summary>
    public IEnumerable<string> Placeholders(string key) => Placeholder.Matches(Text(key)).Select(m => m.Groups[1].Value).Distinct();

    /// <summary>A text field with every {placeholder} filled from <paramref name="values"/>. A placeholder without a value fails, naming it; so does a value nothing uses.</summary>
    public string Fill(string key, IReadOnlyDictionary<string, string> values)
    {
        var t = Text(key); var used = new HashSet<string>();
        var o = Placeholder.Replace(t, m =>
        {
            var k = m.Groups[1].Value;
            if (!values.TryGetValue(k, out var v)) throw new InvalidOperationException($"prompt {Name}.yaml field '{key}': placeholder {{{k}}} has no value");
            used.Add(k); return v;
        });
        var extra = values.Keys.Where(k => !used.Contains(k)).ToList();
        if (extra.Count > 0) throw new InvalidOperationException($"prompt {Name}.yaml field '{key}': no placeholder for {string.Join(", ", extra.Select(e => "{" + e + "}"))}");
        return o;
    }

    public string Fill(string key, params (string Name, string Value)[] values) => Fill(key, values.ToDictionary(v => v.Name, v => v.Value));

    void Parse(string source)
    {
        var lines = source.Replace("\r\n", "\n").Split('\n'); int i = 0;
        while (i < lines.Length)
        {
            var line = lines[i];
            if (line.Trim().Length == 0 || line.TrimStart().StartsWith('#')) { i++; continue; }
            if (char.IsWhiteSpace(line[0])) throw new FormatException($"prompt {Name}.yaml line {i + 1}: unexpected indent: {line.Trim()}");
            int c = line.IndexOf(':');
            if (c <= 0 || !Regex.IsMatch(line[..c], @"^[A-Za-z][A-Za-z0-9_.-]*$")) throw new FormatException($"prompt {Name}.yaml line {i + 1}: expected 'key: value', got: {line}");
            var key = line[..c]; var rest = line[(c + 1)..].Trim(); i++;
            if (text.ContainsKey(key) || lists.ContainsKey(key)) throw new FormatException($"prompt {Name}.yaml line {i}: field '{key}' appears twice");
            if (rest is "|" or "|-")
            {
                var body = new List<string>();
                while (i < lines.Length && (lines[i].Trim().Length == 0 || char.IsWhiteSpace(lines[i][0]))) body.Add(lines[i++]);
                while (body.Count > 0 && body[^1].Trim().Length == 0) body.RemoveAt(body.Count - 1);
                int indent = body.Where(b => b.Trim().Length > 0).Select(b => b.Length - b.TrimStart().Length).DefaultIfEmpty(0).Min();
                text[key] = string.Join('\n', body.Select(b => b.Length >= indent ? b[indent..] : "")) + (rest == "|" ? "\n" : "");
                if (rest == "|") text[key] = text[key].TrimEnd('\n');   // every use adds its own line ends
            }
            else if (rest.Length == 0)
            {
                var items = new List<string>();
                while (i < lines.Length && (lines[i].Trim().Length == 0 || lines[i].TrimStart().StartsWith('#') || char.IsWhiteSpace(lines[i][0])))
                {
                    var t = lines[i++].Trim(); if (t.Length == 0 || t.StartsWith('#')) continue;
                    if (!t.StartsWith("- ")) throw new FormatException($"prompt {Name}.yaml line {i}: expected '- item' in list '{key}', got: {t}");
                    items.Add(Scalar(t[2..].Trim(), i));
                }
                lists[key] = items;
            }
            else text[key] = Scalar(rest, i);
        }
    }

    string Scalar(string s, int line)
    {
        if (s.StartsWith('"')) { try { return JsonSerializer.Deserialize<string>(s)!; } catch (JsonException) { throw new FormatException($"prompt {Name}.yaml line {line}: bad quoted string: {s}"); } }
        if (s.StartsWith('\'') && s.EndsWith('\'') && s.Length >= 2) return s[1..^1].Replace("''", "'");
        return s;
    }

    /// <summary>A request as readable text, for the committed samples and for people: the messages as plain text, images as their file names.</summary>
    public static string Render(string name, string model, string prompt, IEnumerable<(string Label, string File)> images)
    {
        var sb = new StringBuilder();
        sb.Append("=== ").Append(name).Append("  (model: ").Append(model).Append(")\n--- user message: text ---\n").Append(prompt.TrimEnd('\n')).Append('\n');
        foreach (var (label, file) in images) sb.Append("--- user message: text ---\n").Append(label).Append(":\n--- user message: image ---\n[image file: ").Append(file).Append("]\n");
        return sb.ToString();
    }
}
