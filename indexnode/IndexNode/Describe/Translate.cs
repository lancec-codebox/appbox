using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace IndexNode;

/// <summary>What the translate step reads from a drama's folder (all optional except one text source).</summary>
public sealed class TranslateInput
{
    public string JobId { get; set; } = "";
    /// <summary>ocr/subs.zh-Hans.vtt (else ocr/subs.zh.vtt): the burned-in Chinese.</summary>
    public IReadOnlyList<ShapeCue> Ocr { get; set; } = [];
    /// <summary>The transcript's segments with their own times (asr/sensevoice.zh.json).</summary>
    public IReadOnlyList<ShapeCue> Asr { get; set; } = [];
    /// <summary>sync.json offset: transcript time minus OCR time (0 when unknown).</summary>
    public double SyncOffset { get; set; }
    public JsonObject? Describe { get; set; }
}

/// <summary>The chosen Chinese source: which track, how much of the speech it covers, and its cues with their own times.</summary>
public sealed record TranslateSource(string Track, double? Coverage, double SpeechSeconds, IReadOnlyList<ShapeCue> Cues, string Hash);

/// <summary>One glossary line. Kind "address" is a form of address (how it is SAID in English); <c>Refers</c> says whom it refers to, for context only.</summary>
public sealed record GlossaryEntry(string Zh, string En, string Kind, string Refers = "");

/// <summary>The glossary call's answer: the entries and the private plot summary (kept in en.json only, never published).</summary>
public sealed record GlossaryResult(List<GlossaryEntry> Entries, string Plot);

public sealed record TranslateResult(bool Skipped, string? Reason, byte[]? Vtt, byte[]? Json, ShapeStats? Stats, int Calls, int Batches, double Seconds);

/// <summary>
/// The translate step (owner decision 2026-11-14): our own name-aware English subtitles, made from the Chinese (burned-in OCR when it covers
/// most of the speech, else our transcript; never an existing English track). One call builds the drama's glossary, then batches of about 200
/// cues, each with neighbouring cues as context-only lines, the glossary and the synopsis, run up to 4 at a time. The Chinese cue times are
/// kept; <see cref="CueShaper"/> shapes the English (Latin limits, reading time).
/// </summary>
public static partial class Translate
{
    public const string PromptVersion = "translate-2";
    public const int BatchCues = 200, ContextCues = 10, Parallelism = 4, MaxReasks = 2;
    /// <summary>Context on each side of a batch: at least this many cues AND at least this many seconds (whichever reaches further), so an exchange split by a boundary is seen whole.</summary>
    public const double ContextSeconds = 30;
    /// <summary>The OCR is the source when at least this share of the transcript's speech time has an OCR cue on screen.</summary>
    public const double MinCoverage = 0.6;
    const double CoverPad = 0.3;
    const int GlossaryMaxChars = 250_000;
    public const string OutVtt = "subs/ai/en.vtt", OutJson = "subs/ai/en.json";

    [GeneratedRegex(@"[\u3400-\u9fff]")] private static partial Regex Cjk();
    [GeneratedRegex(@"\s*[\(\[（【][^\)\]）】]*[\)\]）】]\s*")] private static partial Regex Bracketed();
    [GeneratedRegex(@"^[\s♪♫♬～~…·—\-]*$|^[\(（\[【][^\)）\]】]*[\)）\]】]$")] private static partial Regex NonSpeech();

    // ---------- source ----------

    static string Flat(string s) => Regex.Replace(s.Replace("\r", ""), @"\s*\n\s*", "").Trim();

    public static bool IsNonSpeech(string zh) => NonSpeech().IsMatch(zh.Trim());

    /// <summary>OCR when it covers at least <see cref="MinCoverage"/> of the transcript's speech time (OCR cue times shifted by the sync offset, ±0.3 s), else the transcript. Without a transcript the OCR is used, without OCR the transcript.</summary>
    public static TranslateSource ChooseSource(IReadOnlyList<ShapeCue> ocr, IReadOnlyList<ShapeCue> asr, double offset)
    {
        var o = ocr.Where(c => Flat(c.Text).Length > 0).Select(c => new ShapeCue(c.Start, c.End, Flat(c.Text))).OrderBy(c => c.Start).ToList();
        var a = asr.Where(c => c.Text.Trim().Length > 0).Select(c => new ShapeCue(c.Start, c.End, Flat(c.Text))).OrderBy(c => c.Start).ToList();
        double speech = a.Sum(c => Math.Max(0, c.End - c.Start));
        double? cov = null;
        if (speech > 0 && o.Count > 0)
        {
            // the union of the OCR intervals on the speech clock, then its overlap with each speech segment
            var u = new List<(double S, double E)>();
            foreach (var c in o)
            {
                double s = c.Start + offset - CoverPad, e = c.End + offset + CoverPad;
                if (u.Count > 0 && s <= u[^1].E) u[^1] = (u[^1].S, Math.Max(u[^1].E, e)); else u.Add((s, e));
            }
            double covered = 0;
            foreach (var c in a) foreach (var (s, e) in u) { double x = Math.Min(c.End, e) - Math.Max(c.Start, s); if (x > 0) covered += x; }
            cov = Math.Round(covered / speech, 4);
        }
        bool useOcr = o.Count > 0 && (a.Count == 0 || (cov ?? 0) >= MinCoverage);
        if (!useOcr && a.Count == 0) return new TranslateSource("none", cov, speech, [], Hash("none", []));
        var cues = useOcr ? o : a; var track = useOcr ? "ocr" : "asr";
        return new TranslateSource(track, cov, Math.Round(speech, 1), cues, Hash(track, cues));
    }

    /// <summary>Hash of the cue text's letters and digits only (as AvSync.Clean), 16 hex characters.</summary>
    public static string CleanHash(string zh) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(AvSync.Clean(zh)))).ToLowerInvariant()[..16];

    static string Hash(string track, IReadOnlyList<ShapeCue> cues)
    {
        var sb = new StringBuilder(track).Append('\n');
        foreach (var c in cues) sb.Append(c.Start.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)).Append('|').Append(c.End.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)).Append('|').Append(c.Text).Append('\n');
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()))).ToLowerInvariant();
    }

    /// <summary>Reads the step's inputs from a job's files.</summary>
    public static async Task<TranslateInput> GatherAsync(IJobSource src, CancellationToken ct)
    {
        var t = new TranslateInput { JobId = src.JobId };
        var hans = await src.ReadAsync("ocr/subs.zh-Hans.vtt", ct) ?? await src.ReadAsync("ocr/subs.zh.vtt", ct);
        if (hans != null) t.Ocr = CueShaper.ParseVtt(Encoding.UTF8.GetString(hans));
        if (await src.ReadAsync("asr/" + Transcribe.ModelName + ".zh.json", ct) is { } ab)
        {
            using var doc = JsonDocument.Parse(ab); var segs = new List<ShapeCue>();
            if (doc.RootElement.TryGetProperty("segments", out var arr))
                foreach (var s in arr.EnumerateArray())
                    segs.Add(new ShapeCue(s.GetProperty("start").GetDouble(), s.GetProperty("end").GetDouble(), s.TryGetProperty("text", out var tx) ? tx.GetString() ?? "" : ""));
            t.Asr = segs;
        }
        if (await src.ReadAsync("sync.json", ct) is { } sb && JsonNode.Parse(sb)?["offset"] is JsonValue ov && ov.TryGetValue<double>(out var off)) t.SyncOffset = off;
        if (await src.ReadAsync("describe.json", ct) is { } db) t.Describe = JsonNode.Parse(db) as JsonObject;
        return t;
    }

    // ---------- glossary ----------

    /// <summary>describe's English names and roles, as lines for the glossary prompt, plus the (zh, en) pairs that are fixed.</summary>
    public static (string Text, List<GlossaryEntry> Fixed) Seed(JsonObject? describe)
    {
        var sb = new StringBuilder(); var fixedNames = new List<GlossaryEntry>();
        if (describe == null) return ("", fixedNames);
        if (describe["title"]?["en"]?.GetValue<string>() is { Length: > 0 } title) sb.Append("Drama title: ").Append(title).Append('\n');
        foreach (var c in (describe["cast"] as JsonArray) ?? [])
        {
            if (c is not JsonObject co) continue;
            var ch = co["character"] as JsonObject;
            string zh = ch?["zh"]?.GetValue<string>()?.Trim() ?? "", py = ch?["pinyin"]?.GetValue<string>()?.Trim() ?? "", en = ch?["en"]?.GetValue<string>()?.Trim() ?? "";
            string label = (co["label"] as JsonObject)?["en"]?.GetValue<string>()?.Trim() ?? co["label"]?.GetValue<string>()?.Trim() ?? "";
            string role = co["roleAtStart"]?.GetValue<string>() ?? co["role"]?.GetValue<string>() ?? "";
            string name = en.Length > 0 ? en : py;
            if (name.Length == 0 && label.Length == 0) continue;
            sb.Append("- ").Append(zh.Length > 0 ? zh : "(no Chinese name)").Append(" => ").Append(name.Length > 0 ? name : "(unnamed)")
              .Append(label.Length > 0 ? $" [{label}]" : "").Append(role.Length > 0 ? $" ({role})" : "").Append('\n');
            if (zh.Length > 0 && name.Length > 0 && !Cjk().IsMatch(name)) fixedNames.Add(new GlossaryEntry(zh, name, "character"));
        }
        return (sb.ToString(), fixedNames);
    }

    /// <summary>describe's relations as lines: "Huo Zhan (the marquis) -[spouse]-> Su Wan (his bride): husband and wife [REVEAL]"; from and to are cast clusters, named by character label (else English name). Reveals (spoiler true) are kept: the translators need them.</summary>
    public static string Relations(JsonObject? d)
    {
        if (d == null) return "";
        var names = new Dictionary<int, string>();
        foreach (var c in (d["cast"] as JsonArray) ?? [])
        {
            if (c is not JsonObject co || co["cluster"] is not JsonValue cv || !cv.TryGetValue<int>(out var id)) continue;
            string label = (co["label"] as JsonObject)?["en"]?.GetValue<string>()?.Trim() ?? co["label"]?.ToString().Trim() ?? "", en = (co["character"] as JsonObject)?["en"]?.GetValue<string>()?.Trim() ?? "";
            names[id] = label.Length > 0 ? label : en.Length > 0 ? en : "cluster " + id;
        }
        string N(JsonNode? n) => n is JsonValue v && v.TryGetValue<int>(out var i) ? names.GetValueOrDefault(i, "cluster " + i) : n?.ToString() ?? "?";
        var sb = new StringBuilder();
        foreach (var r in (d["relations"] as JsonArray) ?? [])
        {
            if (r is not JsonObject ro) continue;
            string label = (ro["label"] as JsonObject)?["en"]?.GetValue<string>()?.Trim() ?? ro["label"]?.ToString().Trim() ?? "";
            bool reveal = ro["spoiler"] is JsonValue sv && sv.TryGetValue<bool>(out var b) && b;
            sb.Append("- ").Append(N(ro["from"])).Append(" -[").Append(ro["type"]?.ToString()).Append("]-> ").Append(N(ro["to"])).Append(label.Length > 0 ? ": " + label : "").Append(reveal ? " [REVEAL]" : "").Append('\n');
        }
        return sb.ToString();
    }

    static string SynopsisOf(JsonObject? d) => d?["synopsis"]?["en"]?.GetValue<string>()?.Trim() ?? "";

    public static string SeedHash(JsonObject? describe) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(PromptVersion + "\n" + Seed(describe).Text + "\n" + SynopsisOf(describe) + "\n" + Relations(describe)))).ToLowerInvariant()[..16];

    public static string GlossaryHash(IReadOnlyList<GlossaryEntry> g) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', g.OrderBy(e => e.Zh, StringComparer.Ordinal).Select(e => $"{e.Kind}\t{e.Zh}\t{e.En}\t{e.Refers}")))))
            .ToLowerInvariant()[..16];

    /// <summary>The whole Chinese script, one cue per line, evenly thinned when it would not fit (a 2 h drama is about 50k characters).</summary>
    public static string Script(IReadOnlyList<ShapeCue> cues, int maxChars = GlossaryMaxChars)
    {
        long total = cues.Sum(c => c.Text.Length + 1L);
        if (total <= maxChars) return string.Join('\n', cues.Select(c => c.Text));
        double keep = maxChars / (double)total, acc = 0; var res = new List<string>();
        foreach (var c in cues) { acc += keep; if (acc >= 1) { acc -= 1; res.Add(c.Text); } }
        return string.Join('\n', res);
    }

    static string GlossaryPrompt(string seed, string synopsis, string script, bool wantPlot)
    {
        var plotLine = wantPlot ? "Also write \"plot\": a short PRIVATE plot summary of the whole drama, end to end, about 100 to 150 words in English, spoilers allowed: who the main characters are (use the glossary spellings), how they relate (spouse, sibling, master and servant, rank) and any hidden identity or reveal that changes how people address each other. It is only a briefing for the translators." : "";
        var shape = wantPlot ? "{\"plot\":\"\",\"entries\":[{\"zh\":\"\",\"en\":\"\",\"kind\":\"character\",\"refers\":\"\"}]}" : "{\"entries\":[{\"zh\":\"\",\"en\":\"\",\"kind\":\"character\",\"refers\":\"\"}]}";
        return $$"""
        You are preparing the name glossary for the English subtitles of a Chinese drama. Read the whole Chinese script below and list every proper name and recurring form of address that must be rendered the same way in every line.

        Include:
        - kind "character": a character's NAME only (full name, or the name as the Chinese writes it), as ONE English spelling: pinyin without tone marks, given name then surname unless the known list below says otherwise (use the known spelling exactly when the character is listed there). Never put a form of address, a pet name or a nickname here.
        - kind "place": places, palaces, mansions, cities, schools, sects.
        - kind "title": official titles, ranks, noble titles and honorifics used with or instead of a name (e.g. 世子, 侯爷, 王爷, 将军).
        - kind "address": every recurring FORM OF ADDRESS, kin term, pet name or short form of a name used to call someone (e.g. 嫂嫂, 母亲, 夫君, 师父, 昭姐姐, 阿战, 小可爱, 为善少爷). "en" is how it is SAID in English, as an address, not the person's full name: 昭姐姐 => "Sister Zhao", 阿战 => "A-Zhan" or "Zhan", 嫂嫂 => "Sister-in-law", 小可爱 => "sweetie" or "little one", 母亲 => "Mother", 世子 => "Your Highness" when said to him. "refers" is the English name of the person it usually refers to (context only; it is never substituted into the line). If one form is used differently by different speakers, give the commonest rendering.
        - kind "other": anything else that must stay consistent (organisations, named objects, techniques).

        Rules: keep names and address forms apart: a name entry maps a name to its spelling, an address entry maps an address form to how it is said. "en" is plain English letters (no Chinese, no notes, no brackets); one entry per Chinese form; give only entries that occur in the script; at most 250 entries, most frequent first.

        Known drama facts (English spellings chosen for this drama; keep them):
        Synopsis: {{(synopsis.Length > 0 ? synopsis : "(none)")}}
        Characters:
        {{(seed.Length > 0 ? seed : "(none)")}}

        {{plotLine}}

        Answer with ONE JSON object: {{shape}}

        CHINESE SCRIPT (one subtitle cue per line):
        {{script}}
        """;
    }

    public static async Task<GlossaryResult> BuildGlossaryAsync(IChat chat, string model, TranslateSource src, JsonObject? describe, CancellationToken ct)
    {
        var (seed, fixedNames) = Seed(describe);
        var o = await chat.JsonAsync(model, JsonValue.Create(GlossaryPrompt(seed, SynopsisOf(describe), Script(src.Cues), describe == null))!, "submit_glossary", ct);
        var map = new Dictionary<string, GlossaryEntry>(StringComparer.Ordinal);
        var arr = o["entries"] as JsonArray ?? o.Select(kv => kv.Value).OfType<JsonArray>().FirstOrDefault() ?? [];
        foreach (var e in arr.OfType<JsonObject>())
        {
            string zh = e["zh"]?.ToString().Trim() ?? "", en = e["en"]?.ToString().Trim() ?? "", kind = (e["kind"]?.ToString() ?? "other").Trim().ToLowerInvariant();
            if (zh.Length == 0 || en.Length == 0 || Cjk().IsMatch(en)) continue;
            if (kind is not ("character" or "place" or "title" or "address")) kind = "other";
            map.TryAdd(zh, new GlossaryEntry(zh, en, kind, kind == "address" ? (e["refers"]?.ToString() ?? "").Trim() : ""));
        }
        // describe's spellings win for its characters, whatever the model wrote
        foreach (var f in fixedNames) map[f.Zh] = f;
        var plot = describe == null ? Regex.Replace(o["plot"]?.ToString() ?? "", @"\s+", " ").Trim() : "";
        return new GlossaryResult(map.Values.OrderBy(e => e.Kind, StringComparer.Ordinal).ThenBy(e => e.Zh, StringComparer.Ordinal).ToList(), plot);
    }

    // ---------- translation ----------

    sealed class Counter { public int Calls; }

    static string GlossaryText(IReadOnlyList<GlossaryEntry> g) =>
        g.Count == 0 ? "(none)" : string.Join('\n', g.Select(e => e.Kind == "address"
            ? $"{e.Zh} => {e.En} [address form{(e.Refers.Length > 0 ? $"; refers to {e.Refers}, context only" : "")}]"
            : $"{e.Zh} => {e.En} [{e.Kind}]"));

    static string BatchPrompt(IReadOnlyList<ShapeCue> cues, IReadOnlyList<(int Id, bool Ask)> window, IReadOnlyDictionary<int, string> known, IReadOnlyList<GlossaryEntry> glossary, string synopsis, string relations, string plot, string title)
    {
        var sb = new StringBuilder();
        sb.Append("""
            You translate the Chinese subtitles of a drama into English subtitles. The cues are in time order; read them as one continuous script.

            Rules:
            - Natural, spoken English that an audience reads at a glance. Keep each speaker's register (formal court speech, family talk, slang, anger, tenderness).
            - Use the glossary EXACTLY for names, places and titles: the English in the glossary is the only spelling to use, every time.
            - A form of address stays a form of address: translate it as the address, in the speaker's register, using the glossary's English for that form (昭姐姐 => "Sister Zhao", 阿战 from his mother => "Zhan" or "A-Zhan", 小可爱 => "sweetie"). NEVER swap in the person's full name for an address. Use a full name only where the Chinese itself uses the name. "refers to" is context only, so you know who is meant.
            - Garbled or stray OCR: a lone character or fragment that is not a word in this context (an OCR error) is not translated. Answer it with "en": "" and "skip": true. Do not guess or invent text for it. Real short words and names are translated normally.
            - No notes, no explanations, no brackets, no speaker labels, no quotation of the Chinese. Do not add words that are not said.
            - One sentence may run across several cues: translate each cue's own part so that the cues read right in sequence (do not repeat the whole sentence in each cue).
            - A cue that is only a sound effect, music or non-speech gets an empty string ("skip": true).
            - Lines marked CONTEXT are only there to help you; do NOT translate them. Lines marked TRANSLATE are the ones to answer.

            """);
        if (title.Length > 0) sb.Append("Drama: ").Append(title).Append('\n');
        if (synopsis.Length > 0) sb.Append("The whole story, with the ending (spoilers; a briefing only, never add words from it): ").Append(synopsis).Append("\n\n");
        if (relations.Length > 0) sb.Append("Relations between the characters ([REVEAL] = hidden until later in the story; use it to know who is who and how they address each other, and keep the speaker's current knowledge):\n").Append(relations).Append('\n');
        if (plot.Length > 0) sb.Append("Plot of the whole drama (a short briefing, spoilers; never add words from it): ").Append(plot).Append("\n\n");
        sb.Append("GLOSSARY:\n").Append(GlossaryText(glossary)).Append("\n\nSCRIPT:\n");
        foreach (var (id, ask) in window)
        {
            sb.Append(ask ? "TRANSLATE " : "CONTEXT ").Append('[').Append(id).Append("] ").Append(cues[id].Text);
            if (!ask && known.TryGetValue(id, out var en)) sb.Append("   (English so far: ").Append(en).Append(')');
            sb.Append('\n');
        }
        var ids = window.Where(w => w.Ask).Select(w => w.Id).ToList();
        sb.Append("\nAnswer with ONE JSON object: {\"cues\":[{\"i\":<id>,\"en\":\"<English>\"}]} (for a garbled or non-speech cue: {\"i\":<id>,\"en\":\"\",\"skip\":true}) with exactly one entry for each of the ").Append(ids.Count)
          .Append(" TRANSLATE ids: ").Append(string.Join(',', ids)).Append(". Nothing else.");
        return sb.ToString();
    }

    /// <summary>Cleans one answer: single line, no bracketed notes. Null when it cannot be used (why in <paramref name="why"/>).</summary>
    public static string? Accept(string zh, string? en, bool skip, out string? why)
    {
        why = null;
        if (skip) return ""; // the model marked it as garbled OCR or non-speech: dropped, never invented
        if (en == null) { why = "no text"; return null; }
        var t = Regex.Replace(Bracketed().Replace(en, " "), @"\s+", " ").Trim();
        if (t.Length == 0) { if (IsNonSpeech(zh)) return ""; why = "empty (answer skip:true for garbled OCR)"; return null; }
        if (Cjk().IsMatch(t)) { why = "still has Chinese"; return null; }
        int n = zh.Count(ch => !char.IsWhiteSpace(ch));
        if (t.Length > 12 * n + 40) { why = "far too long"; return null; }
        if (n >= 4 && t.Length < n / 4) { why = "far too short"; return null; }
        return t;
    }

    /// <summary>The answers of one reply: id → English for the usable ones, with the reasons for the rest (missing, duplicated and different, unusable).</summary>
    public static (Dictionary<int, string> Good, Dictionary<int, string> Bad) Validate(JsonObject reply, IReadOnlyCollection<int> asked, IReadOnlyList<ShapeCue> cues)
    {
        var good = new Dictionary<int, string>(); var bad = new Dictionary<int, string>(); var seen = new Dictionary<int, string?>(); var dup = new HashSet<int>(); var skips = new HashSet<int>();
        var arr = reply["cues"] as JsonArray ?? reply["translations"] as JsonArray ?? reply.Select(kv => kv.Value).OfType<JsonArray>().FirstOrDefault() ?? [];
        var askedSet = asked.ToHashSet();
        foreach (var item in arr.OfType<JsonObject>())
        {
            int id;
            if (item["i"] is JsonValue iv && (iv.TryGetValue<int>(out id) || (iv.TryGetValue<string>(out var s) && int.TryParse(s, out id)))) { }
            else continue;
            if (!askedSet.Contains(id)) continue;
            string? en = item["en"] is JsonValue ev && ev.TryGetValue<string>(out var es) ? es : item["en"]?.ToString();
            if (seen.TryGetValue(id, out var prev)) { if (prev != en) dup.Add(id); continue; }
            seen[id] = en; if (item["skip"] is JsonValue sk && sk.TryGetValue<bool>(out var skb) && skb) skips.Add(id);
        }
        foreach (var id in asked)
        {
            if (!seen.TryGetValue(id, out var en)) { bad[id] = "missing"; continue; }
            if (dup.Contains(id)) { bad[id] = "answered twice with different text"; continue; }
            var ok = Accept(cues[id].Text, en, skips.Contains(id), out var why);
            if (ok == null) bad[id] = why!; else good[id] = ok;
        }
        return (good, bad);
    }

    /// <summary>The asked ids with context-only cues on both sides: at least <paramref name="minCues"/> cues and at least <paramref name="minSeconds"/> seconds from the nearest asked cue, whichever reaches further.</summary>
    static List<(int Id, bool Ask)> Window(IReadOnlyList<ShapeCue> cues, IReadOnlyCollection<int> ask, int minCues, double minSeconds)
    {
        var set = ask.ToHashSet(); var keep = new SortedSet<int>(ask);
        foreach (var i in ask)
        {
            if (set.Contains(i - 1) && set.Contains(i + 1)) continue; // inside a run: only run ends need context
            if (!set.Contains(i - 1)) for (int k = i - 1, n = 0; k >= 0 && (n < minCues || cues[i].Start - cues[k + 1].Start < minSeconds); k--, n++) keep.Add(k);
            if (!set.Contains(i + 1)) for (int k = i + 1, n = 0; k < cues.Count && (n < minCues || cues[k - 1].End - cues[i].End < minSeconds); k++, n++) keep.Add(k);
        }
        return keep.Select(i => (i, set.Contains(i))).ToList();
    }

    static async Task TranslateBatchAsync(IChat chat, string model, IReadOnlyList<ShapeCue> cues, int from, int to, IReadOnlyList<GlossaryEntry> glossary, string synopsis, string relations, string plot, string title,
        string?[] result, Counter counter, int batchNo, int batches, Action<string> progress, CancellationToken ct)
    {
        var pending = Enumerable.Range(from, to - from).ToList(); var known = new Dictionary<int, string>(); string lastWhy = "";
        for (int attempt = 0; attempt <= MaxReasks; attempt++)
        {
            // first ask: the batch with ~10 cues of context on each side; a re-ask: only the bad ids, each with a few neighbours and the English already accepted
            var window = attempt == 0 ? Window(cues, pending, ContextCues, ContextSeconds) : Window(cues, pending, 4, 10);
            var known2 = attempt == 0 ? new Dictionary<int, string>() : known;
            var prompt = BatchPrompt(cues, window, known2, glossary, synopsis, relations, plot, title);
            Interlocked.Increment(ref counter.Calls);
            try
            {
                var reply = await chat.JsonAsync(model, JsonValue.Create(prompt)!, "submit_translation", ct);
                var (good, bad) = Validate(reply, pending, cues);
                foreach (var (id, en) in good) { known[id] = en; result[id] = en; }
                pending = pending.Where(id => !good.ContainsKey(id)).ToList();
                lastWhy = string.Join("; ", bad.Take(5).Select(kv => $"{kv.Key}: {kv.Value}"));
            }
            catch (Exception e) when (e is FormatException or InvalidOperationException or HttpRequestException)
            {
                lastWhy = AiJson.Short(Redact.Sas(e.Message));
                Log.Info($"translate batch {batchNo}/{batches}: call {attempt + 1} failed ({lastWhy})");
                if (attempt < MaxReasks) await Task.Delay(TimeSpan.FromSeconds(2 * (attempt + 1)), ct);
            }
            if (pending.Count == 0) { progress($"batch {batchNo}/{batches}: {to - from} cues translated in {attempt + 1} call(s)"); return; }
            progress($"batch {batchNo}/{batches}: {pending.Count} cue(s) missing or unusable after call {attempt + 1}{(attempt < MaxReasks ? ", asking again" : "")}");
        }
        throw new InvalidOperationException($"translate: batch {batchNo}/{batches} still has {pending.Count} cue(s) missing or unusable after {MaxReasks + 1} calls (ids {string.Join(',', pending.Take(10))}; Chinese: {string.Join(" | ", pending.Take(5).Select(id => cues[id].Text))}): {lastWhy}");
    }

    // ---------- the step ----------

    static readonly JsonSerializerOptions Opt = new() { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>True when an earlier en.json was made from this source text and these describe names with this prompt: nothing to redo.</summary>
    public static bool UpToDate(byte[]? existingJson, string sourceHash, string seedHash)
    {
        if (existingJson == null) return false;
        try
        {
            var o = JsonNode.Parse(existingJson);
            return o?["sourceHash"]?.GetValue<string>() == sourceHash && o?["glossarySeedHash"]?.GetValue<string>() == seedHash && o?["promptVersion"]?.GetValue<string>() == PromptVersion;
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException) { return false; }
    }

    /// <param name="existingJson">the stored subs/ai/en.json, when there is one: the step is skipped when it matches (unless <paramref name="force"/>).</param>
    public static async Task<TranslateResult> RunAsync(IChat chat, AiConfig ai, TranslateInput input, byte[]? existingJson, bool force, Action<string> progress, CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var model = ai.TextModel ?? throw new InvalidOperationException("translate needs a text model (ai.text-model)");
        var src = ChooseSource(input.Ocr, input.Asr, input.SyncOffset);
        if (src.Cues.Count == 0) return new TranslateResult(true, "no Chinese text (no OCR cues and no transcript)", null, null, null, 0, 0, 0);
        var seedHash = SeedHash(input.Describe);
        if (!force && UpToDate(existingJson, src.Hash, seedHash)) return new TranslateResult(true, "source text and glossary seed unchanged", null, null, null, 0, 0, 0);
        progress($"source: {src.Track} ({src.Cues.Count} cues, coverage {(src.Coverage is { } c ? $"{c:P0}" : "n/a")}, speech {src.SpeechSeconds:F0}s){(input.Describe == null ? "; no describe.json" : "")}");
        var counter = new Counter();

        Interlocked.Increment(ref counter.Calls);
        var gr = await BuildGlossaryAsync(chat, model, src, input.Describe, ct); var glossary = gr.Entries; var plot = gr.Plot;
        var gHash = GlossaryHash(glossary);
        progress($"glossary: {glossary.Count} entries (version {gHash}); plot summary {plot.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length} words");

        var synopsis = SynopsisOf(input.Describe); var relations = Relations(input.Describe); var title = input.Describe?["title"]?["en"]?.GetValue<string>() ?? "";
        var cues = src.Cues; int batches = (cues.Count + BatchCues - 1) / BatchCues;
        var en = new string?[cues.Count]; var gate = new SemaphoreSlim(Parallelism); var lockObj = new object(); int done = 0;
        void P(string m) { lock (lockObj) progress(m); }
        var tasks = Enumerable.Range(0, batches).Select(async b =>
        {
            await gate.WaitAsync(ct);
            try { await TranslateBatchAsync(chat, model, cues, b * BatchCues, Math.Min(cues.Count, (b + 1) * BatchCues), glossary, synopsis, relations, plot, title, en, counter, b + 1, batches, P, ct); Interlocked.Increment(ref done); }
            finally { gate.Release(); }
        }).ToList();
        await Task.WhenAll(tasks);

        // keep the Chinese cue times, then the one shaping pass with the Latin limits; names are never cut
        var names = glossary.Where(g => g.Kind is "character" or "place" or "title").Select(g => g.En).Where(n => n.Length > 1).Distinct().ToList();
        var shaped = CueShaper.Shape(Enumerable.Range(0, cues.Count).Where(i => !string.IsNullOrWhiteSpace(en[i])).Select(i => new ShapeCue(cues[i].Start, cues[i].End, en[i]!)), names);
        var stats = CueShaper.Stats(shaped);
        if (stats.Over6s != 0 || stats.OverChars != 0 || stats.Overlaps != 0) throw new InvalidOperationException($"translate: shaping left {stats.Over6s} cue(s) over 6 s, {stats.OverChars} over the character limit, {stats.Overlaps} overlapping (a bug)");
        var vtt = Encoding.UTF8.GetBytes(CueShaper.ToVtt(shaped));
        // shortRead, by how far the cue is short of its reading time (the shaper is unchanged)
        int sr05 = 0, srMid = 0, sr1 = 0;
        foreach (var sc in shaped) { double gap = CueShaper.ReadingSeconds(sc.Text) - (sc.End - sc.Start); if (gap <= 0.0005) continue; if (gap <= 0.5) sr05++; else if (gap <= 1.0) srMid++; else sr1++; }
        var dropped = Enumerable.Range(0, cues.Count).Where(i => string.IsNullOrWhiteSpace(en[i])).ToList();
        var ai2 = chat as AiClient;
        var json = JsonSerializer.SerializeToUtf8Bytes(new
        {
            v = 1, promptVersion = PromptVersion, model, api = ai2?.ApiFor(model) ?? ai.EffectiveApi,
            briefing = input.Describe != null ? "describe" : "glossary-call", plot = plot.Length > 0 ? plot : null, glossaryHash = gHash, glossarySeedHash = seedHash, glossary = glossary.Select(g => new { zh = g.Zh, en = g.En, kind = g.Kind, refers = g.Refers.Length > 0 ? g.Refers : null }),
            sourceTrack = src.Track, sourceHash = src.Hash, coverage = src.Coverage, speechSeconds = src.SpeechSeconds, sourceCues = cues.Count, syncOffset = input.SyncOffset,
            describeUsed = input.Describe != null,
            // the pairs before shaping, for reuse by time alignment: start, end, Chinese, hash of its letters and digits, our English ("" for non-speech)
            cues = Enumerable.Range(0, cues.Count).Select(i => new { start = Math.Round(cues[i].Start, 3), end = Math.Round(cues[i].End, 3), zh = cues[i].Text, hash = CleanHash(cues[i].Text), en = en[i] ?? "" }), batches, aiCalls = counter.Calls,
            tokens = ai2 == null ? null : new { input = ai2.InputTokens, output = ai2.OutputTokens },
            shape = stats.ToJson(), shortReadBy = new { upTo0_5s = sr05, from0_5To1s = srMid, over1s = sr1 },
            dropped = dropped.Count, droppedCues = dropped.Select(i => new { i, start = Math.Round(cues[i].Start, 3), zh = cues[i].Text }), seconds = Math.Round(sw.Elapsed.TotalSeconds, 1), at = Protocol.NowUtc(),
        }, Opt);
        progress($"shaped: {stats.Cues} cues, over6s {stats.Over6s}, overChars {stats.OverChars}, overlaps {stats.Overlaps}, shortRead {stats.ShortRead} (up to 0.5 s short {sr05}, 0.5-1 s {srMid}, over 1 s {sr1}); dropped {dropped.Count}");
        return new TranslateResult(false, null, vtt, json, stats, counter.Calls, batches, sw.Elapsed.TotalSeconds);
    }
}
