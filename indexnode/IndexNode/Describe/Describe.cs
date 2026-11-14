using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace IndexNode;

public interface IChat { Task<string> ChatAsync(string model, JsonNode content, CancellationToken ct); }

/// <summary>One batched AI call: its prompt text and the image files it carries.</summary>
public sealed record AiCall(string Name, string Model, string Prompt, IReadOnlyList<(string Label, string File)> Images);

public sealed record MainCluster(int Id, double ScreenSeconds, double FirstSeen, bool Masked);

public sealed class DescribeInput
{
    public string JobId { get; set; } = "";
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public double Duration { get; set; }
    public string TextSource { get; set; } = "transcript";
    public IReadOnlyList<ShapeCue> Cues { get; set; } = [];
    public IReadOnlyList<string> Credits { get; set; } = [];
    public IReadOnlyList<MainCluster> Clusters { get; set; } = [];
    public string? Thumb { get; set; }
    /// <summary>Contact sheet file per main cluster (12 candidate crops each).</summary>
    public IReadOnlyDictionary<int, string> Sheets { get; set; } = new Dictionary<int, string>();
    /// <summary>One sheet of the poster candidates (the "together" frames), and how many there are.</summary>
    public string? PosterSheet { get; set; }
    public int PosterCount { get; set; }
}

/// <summary>The describe step: titles, tags, era, teaser, synopsis, cast and relations in one batched vision call, then a spoiler check with rewrite.</summary>
public static class Describe
{
    public const string PromptVersion = "describe-2";
    public static readonly string[] Eras = ["ancient", "republic", "1950s-60s", "1970s", "1980s-90s", "modern", "futuristic"];
    public const double CutoffFraction = 0.12;

    public static double Cutoff(double duration) => Math.Round(duration * CutoffFraction, 1);

    /// <summary>The model's expression scores as { key: [0..10 or null] }: clamped, anything else null. Keys are cluster ids and "poster".</summary>
    public static JsonObject CleanExpression(JsonNode? n)
    {
        var o = new JsonObject();
        if (n is not JsonObject src) return o;
        foreach (var (k, v) in src)
            if (v is JsonArray a) o[k] = new JsonArray(a.Select(x => x is JsonValue jv && jv.TryGetValue<double>(out var d) ? (JsonNode?)JsonValue.Create((int)Math.Clamp(Math.Round(d), 0, 10)) : null).ToArray());
        return o;
    }

    public static AiCall BuildMain(DescribeInput d, string visionModel)
    {
        var cut = Cutoff(d.Duration);
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("You index a short-drama upload for a catalogue site. Work only from the evidence below. Answer with ONE JSON object and nothing else.");
        sb.AppendLine($"Upload title (as is): {d.Title}");
        sb.AppendLine($"Duration: {d.Duration:F0} s. Spoiler cutoff: {cut:F0} s (first {CutoffFraction:P0} of the story).");
        sb.AppendLine($"Upload description:\n{Trunc(d.Description, 3000)}");
        sb.AppendLine($"Text track ({d.TextSource}; burned-in subtitles where they exist, else our speech recognition), [m:ss] text:\n{TextTrack.Compress(d.Cues)}");
        if (d.Credits.Count > 0) sb.AppendLine("Credits lines read from the video (主演/饰/领衔):\n" + string.Join('\n', d.Credits));
        sb.AppendLine("Main face clusters (cluster id, screen seconds, first seen s, masked): " + string.Join("; ", d.Clusters.Select(c => $"{c.Id}: {c.ScreenSeconds:F0}s, {c.FirstSeen:F0}, {(c.Masked ? "masked" : "bare")}")));
        sb.AppendLine("Images, in order: the upload thumbnail, then one contact sheet per cluster (12 numbered candidate head shots, row by row from 0), then the poster candidate sheet (numbered frames).");
        sb.AppendLine(@"Rules:
- ENGLISH ONLY for teaser, synopsis, tags, descriptions and labels. Chinese appears only as content: title.zh, character.zh, character.pinyin.
- title.en: the drama's own name if the video shows one (title card, thumbnail, description), translated; otherwise a faithful translation of the upload title with hashtags, emoji and filler like 'Full Version' removed. Never invent a title. title.zh is the Chinese name or the cleaned original title. fullTitle.en: faithful translation of the original title.
- teaser.en: premise-style, 2-4 sentences, NO spoilers beyond the cutoff. synopsis.en: the full story with the ending.
- tags: lowercase English words.
- era: one of ancient (dynastic costume drama), republic (1912-1949), 1950s-60s, 1970s, 1980s-90s, modern (present day), futuristic. A time-travel or rebirth story takes the era where most of it happens. fantasy: true for xianxia, xuanhuan or supernatural stories.
- cast: one entry per cluster you can identify; exactly one 'female lead' and one 'male lead' when the story has them, others 'main' or 'supporting'. NEVER use the words villain or antagonist anywhere. Name a person only when subtitles, dialogue or credits support it; otherwise label them relative to a named character (""Gu Heng's grandmother""). actor only from the credits lines, else null. role is the final role, roleAtStart what the viewer knows in the opening; descriptions and labels describe the start, never later reveals.
- relations: every cast member linked to at least one other. since/until in seconds; spoiler is true when since is after the cutoff or the relation is itself a reveal; implied true when not stated outright.
- picks: for each cluster, the best portrait (most flattering, in-character, well lit, expressive, no other face dominating) and 1-3 stills as candidate indexes of its sheet; poster: the index of the single most poster-worthy 'together' frame (charged, iconic, cinematic; NOT a twist, death, blood, identity reveal or late-story costume), or null.
JSON shape: {""title"":{""en"":"""",""zh"":""""},""fullTitle"":{""en"":""""},""teaser"":{""en"":""""},""synopsis"":{""en"":""""},""tags"":[],""era"":"""",""fantasy"":false,""cast"":[{""cluster"":0,""character"":{""zh"":"""",""pinyin"":"""",""en"":""""}|null,""role"":"""",""roleAtStart"":"""",""description"":"""",""actor"":null,""label"":{""en"":""""}}],""relations"":[{""from"":0,""to"":1,""type"":"""",""label"":{""en"":""""},""since"":0,""until"":null,""spoiler"":false,""implied"":false}],""picks"":{""0"":{""portrait"":0,""stills"":[0]}},""poster"":null,""expression"":{""0"":[0],""poster"":[0]},""samePerson"":[[0,1]]}
- expression: score EVERY numbered head shot of EVERY cluster sheet, and every frame of the poster candidate sheet, for how flattering the face is, an integer 0-10. 8-10: eyes open and roughly toward the camera, mouth closed or a slight smile, relaxed or composed. 5-7: acceptable but flat or slightly off. 0-2: mid-speech (open mouth), shocked or startled, crying, grimacing, blinking, eyes closed, looking far away. For a poster frame score the weaker of the two faces. Arrays are in sheet order; ""poster"" is the poster sheet.
from/to and picks keys are cluster ids.
- samePerson: lists of cluster ids that show the SAME person (the face matcher sometimes splits one character across clusters, e.g. in another costume or mask). Compare the contact sheets carefully; only merge when you are sure; [] when none. Do not list such a cluster separately in cast: give the group once, the first id is kept.");
        var imgs = new List<(string, string)>();
        if (d.Thumb != null) imgs.Add(("thumbnail", d.Thumb));
        foreach (var c in d.Clusters) if (d.Sheets.TryGetValue(c.Id, out var f)) imgs.Add(($"cluster {c.Id}", f));
        if (d.PosterSheet != null) imgs.Add(($"poster candidates 0..{d.PosterCount - 1}", d.PosterSheet));
        return new AiCall("describe", visionModel, sb.ToString(), imgs);
    }

    public static JsonNode ToContent(AiCall c)
    {
        var arr = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = c.Prompt });
        foreach (var (label, file) in c.Images)
        {
            arr.Add(new JsonObject { ["type"] = "text", ["text"] = label + ":" });
            arr.Add(new JsonObject { ["type"] = "image_url", ["image_url"] = new JsonObject { ["url"] = "data:image/jpeg;base64," + Convert.ToBase64String(File.ReadAllBytes(file)) } });
        }
        return arr;
    }

    /// <summary>The dry request: what would be sent, with the images listed by file and size instead of embedded.</summary>
    public static string DryJson(IEnumerable<AiCall> calls) => JsonSerializer.Serialize(new
    {
        v = 1, promptVersion = PromptVersion,
        calls = calls.Select(c => new { name = c.Name, model = c.Model, prompt = c.Prompt, images = c.Images.Select(i => new { label = i.Label, file = i.File, bytes = File.Exists(i.File) ? new FileInfo(i.File).Length : 0 }) }),
    }, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });

    static string Trunc(string s, int n) => s.Length <= n ? s : s[..n] + "…";

    /// <summary>Pulls the JSON object out of a model reply (code fences and chatter tolerated).</summary>
    public static JsonObject ParseObject(string reply)
    {
        var s = reply.Trim(); int a = s.IndexOf('{'), b = s.LastIndexOf('}');
        if (a < 0 || b <= a) throw new FormatException("the AI reply holds no JSON object");
        return JsonNode.Parse(s[a..(b + 1)], null, new JsonDocumentOptions { AllowTrailingCommas = true })!.AsObject();
    }

    static readonly Regex Bad = new(@"villain|antagonist", RegexOptions.IgnoreCase);
    static readonly Regex Cjk = new(@"[\u3400-\u9fff]");

    /// <summary>samePerson groups to a map from each merged-in cluster to the group's kept cluster (the one with the most screen time). Unknown ids, singletons and overlaps are ignored.</summary>
    public static Dictionary<int, int> SamePerson(JsonNode? node, HashSet<int> ids, DescribeInput d)
    {
        var map = new Dictionary<int, int>(); var seen = new HashSet<int>(); var secs = d.Clusters.ToDictionary(c => c.Id, c => c.ScreenSeconds);
        foreach (var g in (node as JsonArray) ?? [])
        {
            var grp = ((g as JsonArray) ?? []).Select(x => x is JsonValue v && v.TryGetValue<int>(out var i) ? i : -1).Where(i => ids.Contains(i) && !seen.Contains(i)).Distinct().ToList();
            if (grp.Count < 2) continue;
            int keep = grp.OrderByDescending(i => secs[i]).First(); foreach (var i in grp) { seen.Add(i); if (i != keep) map[i] = keep; }
        }
        return map;
    }

    /// <summary>Validates and cleans a model reply into the describe result: English-only text, valid era, no villain wording, roles from the allowed set, one lead each.</summary>
    public static JsonObject Normalize(JsonObject r, DescribeInput d)
    {
        string En(JsonNode? n) => n is JsonObject o ? o["en"]?.GetValue<string>()?.Trim() ?? "" : n?.GetValue<string>()?.Trim() ?? "";
        string Clean(string s) => Bad.Replace(s, "").Replace("  ", " ").Trim();
        var o = new JsonObject
        {
            ["title"] = new JsonObject { ["en"] = Clean(En(r["title"])), ["zh"] = (r["title"] as JsonObject)?["zh"]?.GetValue<string>() ?? "" },
            ["fullTitle"] = new JsonObject { ["en"] = Clean(En(r["fullTitle"])) },
            ["teaser"] = new JsonObject { ["en"] = Clean(En(r["teaser"])) },
            ["synopsis"] = new JsonObject { ["en"] = Clean(En(r["synopsis"])) },
        };
        var tags = new JsonArray();
        foreach (var t in (r["tags"] as JsonArray) ?? []) { var s = t?.GetValue<string>()?.Trim().ToLowerInvariant() ?? ""; if (s.Length > 0 && !Cjk.IsMatch(s) && !Bad.IsMatch(s) && !tags.Any(x => x!.GetValue<string>() == s)) tags.Add(s); }
        o["tags"] = tags;
        var era = r["era"]?.GetValue<string>()?.Trim().ToLowerInvariant() ?? "";
        o["era"] = Eras.Contains(era) ? era : null; o["fantasy"] = r["fantasy"] is JsonValue fv && fv.TryGetValue<bool>(out var fb) && fb;
        var ids = d.Clusters.Select(c => c.Id).ToHashSet(); var cast = new JsonArray(); bool f = false, m = false;
        var canon = SamePerson(r["samePerson"], ids, d); int Cn(int id) => canon.GetValueOrDefault(id, id);
        o["samePerson"] = new JsonArray(canon.GroupBy(kv => kv.Value).Select(g => (JsonNode)new JsonArray(new[] { g.Key }.Concat(g.Select(kv => kv.Key).OrderBy(x => x)).Select(x => (JsonNode)x).ToArray())).ToArray());
        foreach (var e in (r["cast"] as JsonArray) ?? [])
        {
            if (e is not JsonObject c || c["cluster"] is not JsonValue cv || !cv.TryGetValue<int>(out var cid) || !ids.Contains(cid) || canon.ContainsKey(cid) || cast.Any(x => x!["cluster"]!.GetValue<int>() == cid)) continue;
            string Role(string? s)
            {
                s = (s ?? "").Trim().ToLowerInvariant();
                if (s == "female lead" && !f) { f = true; return s; }
                if (s == "male lead" && !m) { m = true; return s; }
                return s == "main" ? "main" : "supporting";
            }
            var ch = c["character"] as JsonObject;
            cast.Add(new JsonObject
            {
                ["cluster"] = cid, ["character"] = ch == null ? null : new JsonObject { ["zh"] = ch["zh"]?.GetValue<string>() ?? "", ["pinyin"] = ch["pinyin"]?.GetValue<string>() ?? "", ["en"] = ch["en"]?.GetValue<string>() ?? "" },
                ["role"] = Role(c["role"]?.GetValue<string>()), ["roleAtStart"] = Clean(c["roleAtStart"]?.GetValue<string>() ?? c["role"]?.GetValue<string>() ?? "supporting"),
                ["description"] = Clean(En(c["description"])), ["actor"] = c["actor"]?.GetValue<string>() is { Length: > 0 } a && d.Credits.Any(x => x.Contains(a)) ? a : null,
                ["label"] = new JsonObject { ["en"] = Clean(En(c["label"])) },
            });
        }
        o["cast"] = cast;
        var rels = new JsonArray(); double cut = Cutoff(d.Duration);
        foreach (var e in (r["relations"] as JsonArray) ?? [])
        {
            if (e is not JsonObject x || x["from"] is not JsonValue fv2 || x["to"] is not JsonValue tv || !fv2.TryGetValue<int>(out var from) || !tv.TryGetValue<int>(out var to)) continue;
            if (!ids.Contains(from) || !ids.Contains(to)) continue; from = Cn(from); to = Cn(to); if (from == to) continue;
            double since = x["since"] is JsonValue sv && sv.TryGetValue<double>(out var sd) ? sd : 0; double? until = x["until"] is JsonValue uv && uv.TryGetValue<double>(out var ud) ? ud : null;
            bool spoiler = (x["spoiler"] is JsonValue pv && pv.TryGetValue<bool>(out var pb) && pb) || since > cut;
            rels.Add(new JsonObject { ["from"] = from, ["to"] = to, ["type"] = Clean(x["type"]?.GetValue<string>() ?? "").ToLowerInvariant(), ["label"] = new JsonObject { ["en"] = Clean(En(x["label"])) }, ["since"] = since, ["until"] = until, ["spoiler"] = spoiler, ["implied"] = x["implied"] is JsonValue iv && iv.TryGetValue<bool>(out var ib) && ib });
        }
        o["relations"] = rels;
        o["picks"] = r["picks"] is JsonObject p ? JsonNode.Parse(p.ToJsonString()) : new JsonObject();
        o["expression"] = CleanExpression(r["expression"]);
        o["poster"] = r["poster"] is JsonValue pv2 && pv2.TryGetValue<int>(out var pi) ? pi : null;
        return o;
    }

    public static AiCall BuildCheck(JsonObject res, double cutoff, string model) => new("spoiler-check", model, @$"You are a spoiler checker for a catalogue site. The default page shows the teaser, the cast as known at the start and the relations marked spoiler=false, to people who have not watched. Cutoff: the first {cutoff:F0} s of the story.
Does the teaser reveal any twist, death, identity reveal or ending? Does any cast roleAtStart/description/label, or any relation with spoiler=false, give away something established after the cutoff or a hidden identity?
Answer ONE JSON object: {{""teaserReveals"":false,""reasons"":[],""flaggedCast"":[cluster ids],""flaggedRelations"":[indexes into relations]}}.
teaser: {res["teaser"]!["en"]}
cast: {JsonSerializer.Serialize(res["cast"]!.AsArray().Select(c => new { cluster = c!["cluster"]!.GetValue<int>(), roleAtStart = c["roleAtStart"]!.GetValue<string>(), description = c["description"]!.GetValue<string>(), label = c["label"]!["en"]!.GetValue<string>() }))}
relations: {JsonSerializer.Serialize(res["relations"]!.AsArray().Select((r, i) => new { i, from = r!["from"]!.GetValue<int>(), to = r["to"]!.GetValue<int>(), type = r["type"]!.GetValue<string>(), r2 = r["since"]!.GetValue<double>(), spoiler = r["spoiler"]!.GetValue<bool>() }).Where(x => !x.spoiler))}", []);

    public static AiCall BuildRewrite(JsonObject res, JsonObject check, string model) => new("spoiler-rewrite", model, @$"Rewrite the flagged parts so they give nothing away: the teaser must stay a premise only (2-4 sentences, English); flagged cast descriptions and roleAtStart/label must describe only what a viewer knows in the opening. Never use the words villain or antagonist.
Reasons: {check["reasons"]?.ToJsonString()}
Answer ONE JSON object: {{""teaser"":""..."",""cast"":[{{""cluster"":0,""roleAtStart"":"""",""description"":"""",""label"":""""}}]}}, listing only the flagged cast entries.
teaser: {res["teaser"]!["en"]}
flagged cast: {JsonSerializer.Serialize(res["cast"]!.AsArray().Where(c => (check["flaggedCast"] as JsonArray)?.Any(x => x!.GetValue<int>() == c!["cluster"]!.GetValue<int>()) == true).Select(c => c!))}", []);

    /// <summary>Applies a spoiler check to the result: flagged relations become spoilers, and one rewrite call fixes the teaser and cast. Returns the number of AI calls made.</summary>
    public static async Task<int> SpoilerCheckAsync(IChat chat, string model, JsonObject res, DescribeInput d, CancellationToken ct)
    {
        int calls = 0;
        for (int round = 0; round < 2; round++)
        {
            var check = ParseObject(await chat.ChatAsync(model, JsonValue.Create(BuildCheck(res, Cutoff(d.Duration), model).Prompt)!, ct)); calls++;
            foreach (var i in (check["flaggedRelations"] as JsonArray) ?? [])
                if (i is JsonValue v && v.TryGetValue<int>(out var ix) && ix >= 0 && ix < res["relations"]!.AsArray().Count) res["relations"]![ix]!["spoiler"] = true;
            bool teaser = check["teaserReveals"] is JsonValue tv && tv.TryGetValue<bool>(out var tb) && tb;
            var fc = (check["flaggedCast"] as JsonArray) ?? [];
            if (!teaser && fc.Count == 0) { res["spoilerCheck"] = new JsonObject { ["rounds"] = round + 1, ["clean"] = true }; return calls; }
            var rw = ParseObject(await chat.ChatAsync(model, JsonValue.Create(BuildRewrite(res, check, model).Prompt)!, ct)); calls++;
            if (teaser && rw["teaser"]?.GetValue<string>() is { Length: > 0 } nt) res["teaser"] = new JsonObject { ["en"] = Bad.Replace(nt, "").Trim() };
            foreach (var e in (rw["cast"] as JsonArray) ?? [])
            {
                if (e is not JsonObject c || c["cluster"] is not JsonValue cv || !cv.TryGetValue<int>(out var cid)) continue;
                var tgt = res["cast"]!.AsArray().FirstOrDefault(x => x!["cluster"]!.GetValue<int>() == cid); if (tgt == null) continue;
                if (c["roleAtStart"]?.GetValue<string>() is { Length: > 0 } r) tgt["roleAtStart"] = Bad.Replace(r, "").Trim();
                if (c["description"]?.GetValue<string>() is { Length: > 0 } ds) tgt["description"] = Bad.Replace(ds, "").Trim();
                if (c["label"]?.GetValue<string>() is { Length: > 0 } lb) tgt["label"] = new JsonObject { ["en"] = Bad.Replace(lb, "").Trim() };
            }
        }
        res["spoilerCheck"] = new JsonObject { ["rounds"] = 2, ["clean"] = false };
        return calls;
    }

    /// <summary>The whole step: one batched vision call, normalisation, spoiler check and rewrite. The result is describe.json's body.</summary>
    public static async Task<JsonObject> RunAsync(IChat chat, string textModel, string visionModel, DescribeInput d, CancellationToken ct)
    {
        var main = BuildMain(d, visionModel);
        var reply = await chat.ChatAsync(visionModel, ToContent(main), ct);
        var res = Normalize(ParseObject(reply), d);
        int calls = 1 + await SpoilerCheckAsync(chat, textModel, res, d, ct);
        res["v"] = 1; res["promptVersion"] = PromptVersion; res["jobId"] = d.JobId;
        res["models"] = new JsonObject { ["text"] = textModel, ["vision"] = visionModel };
        res["aiCalls"] = calls; res["cutoffSeconds"] = Cutoff(d.Duration); res["at"] = Protocol.NowUtc();
        return res;
    }
}
