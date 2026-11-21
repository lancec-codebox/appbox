using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace IndexNode;

public interface IChat
{
    Task<string> ChatAsync(string model, JsonNode content, CancellationToken ct);
    /// <summary>A JSON-object answer. The default asks for text and parses it, retrying with the parse error; AiClient asks for it as a forced tool call.</summary>
    Task<JsonObject> JsonAsync(string model, JsonNode content, string tool, CancellationToken ct) => AiJson.ViaTextAsync(this, model, content, ct);
}

/// <summary>JSON answers from a model: parse failures are retried with the error passed back (owner 2026-11-14: one bad reply failed a whole describe).</summary>
public static class AiJson
{
    public const int Retries = 2;

    /// <summary>The original request plus the failed reply and why it failed, asking for a corrected object.</summary>
    public static JsonNode WithError(JsonNode content, string reply, string error)
    {
        var note = "\n\nYour previous answer could not be used: " + error + "\nPrevious answer (start):\n" + (reply.Length > 1500 ? reply[..1500] : reply)
                 + "\nAnswer again with ONLY the corrected, complete JSON object.";
        if (content is JsonArray parts) { var c = (JsonArray)parts.DeepClone(); c.Add(new JsonObject { ["type"] = "text", ["text"] = note.TrimStart() }); return c; }
        return JsonValue.Create(content.GetValue<string>() + note)!;
    }

    public static async Task<JsonObject> ViaTextAsync(IChat chat, string model, JsonNode content, CancellationToken ct)
    {
        string? last = null; Exception? err = null;
        for (int k = 0; k <= Retries; k++)
        {
            last = await chat.ChatAsync(model, k == 0 ? content : WithError(content, last!, err!.Message), ct);
            try { return Describe.ParseObject(last); }
            catch (Exception e) when (e is JsonException or FormatException) { err = e; Log.Info($"ai {model}: reply {k + 1} did not parse ({Short(e.Message)}); {(k < Retries ? "asking again with the error" : "giving up")}"); }
        }
        throw new FormatException($"the AI reply did not parse after {Retries + 1} tries: {err!.Message}");
    }

    public static string Short(string s) => s.Length > 160 ? s[..160] + "..." : s;
}

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

/// <summary>The describe step: titles, tags, era, teaser, synopsis, cast and relations in one batched vision call, then one twist check that can correct the teaser and cast.</summary>
public static class Describe
{
    /// <summary>The describe prompt version, kept with the prompt text in Prompts/describe.yaml (with its history).</summary>
    public static string PromptVersion => PromptFile.Load("describe").Version;
    public static readonly string[] Eras = ["ancient", "republic", "1950s-60s", "1970s", "1980s-90s", "modern", "futuristic"];
    /// <summary>The model's expression scores as { key: [0..10 or null] }: clamped, anything else null. Keys are cluster ids and "poster".</summary>
    /// <summary>The model's covered head shots as { cluster id: [sheet indexes] }: integers 0..99 only, anything else dropped.</summary>
    public static JsonObject CleanMasked(JsonNode? n)
    {
        var o = new JsonObject();
        if (n is JsonObject m)
            foreach (var (k, v) in m)
                if (int.TryParse(k, out _) && v is JsonArray a)
                    o[k] = new JsonArray(a.Select(x => x is JsonValue xv && xv.TryGetValue<int>(out var i) && i >= 0 && i < 100 ? (int?)i : null).Where(i => i != null).Distinct().Select(i => (JsonNode)i!.Value).ToArray());
        return o;
    }

    /// <summary>Sets each candidate's "masked" from describe's covered head shots (describe-3 and later); older describe outputs leave the cast stage's flags.</summary>
    public static void ApplyMasks(JsonArray? cands, JsonObject describe, int cluster)
    {
        if (cands == null || describe["masked"] is not JsonObject m) return;
        var covered = (m[cluster.ToString()] as JsonArray)?.Select(x => x!.GetValue<int>()).ToHashSet() ?? [];
        foreach (var c in cands.OfType<JsonObject>()) c["masked"] = c["k"] is JsonValue kv && kv.TryGetValue<int>(out var k) && covered.Contains(k);
    }

    /// <summary>A character is masked when most of its head shots are covered (describe-3 and later); null for older describe outputs.</summary>
    public static bool? ClusterMasked(JsonObject describe, int cluster, int shots = CastStage.Candidates) =>
        describe["masked"] is JsonObject m ? ((m[cluster.ToString()] as JsonArray)?.Count ?? 0) > shots / 2 : null;

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
        var pf = PromptFile.Load("describe"); var sb = new System.Text.StringBuilder();
        sb.AppendLine(pf.Text("intro"));
        sb.AppendLine(pf.Fill("title", ("title", d.Title)));
        sb.AppendLine(pf.Fill("duration", ("duration", d.Duration.ToString("F0"))));
        sb.AppendLine(pf.Fill("description", ("description", Trunc(d.Description, 3000))));
        sb.AppendLine(pf.Fill("text-track", ("textSource", d.TextSource), ("textTrack", TextTrack.Compress(d.Cues))));
        if (d.Credits.Count > 0) sb.AppendLine(pf.Fill("credits", ("credits", string.Join('\n', d.Credits))));
        sb.AppendLine(pf.Fill("clusters", ("clusters", string.Join("; ", d.Clusters.Select(c => $"{c.Id}: {c.ScreenSeconds:F0}s, {c.FirstSeen:F0}")))));
        sb.AppendLine(pf.Text("images"));
        sb.AppendLine(pf.Fill("rules", ("relationKinds", string.Join(", ", pf.List("relation-kinds"))), ("exampleTitles", "  " + string.Join(" | ", pf.List("example-titles")))));
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
        if (AiClient.ErrorMessage(s) is { } em) throw new FormatException("the AI endpoint answered with an error: " + Redact.Sas(Trunc(em, 300)));
        if (a < 0 || b <= a) throw new FormatException("the AI reply holds no JSON object; it starts: " + Redact.Sas(Trunc(s, 300)));
        // the FIRST complete object from the first '{': a reply with a second object or prose after it (braces included) still parses
        // (first '{' to last '}' failed on -CesE-eJY8s: "'{' is invalid after a value")
        var bytes = System.Text.Encoding.UTF8.GetBytes(s[a..]);
        var reader = new Utf8JsonReader(bytes, new JsonReaderOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
        using var doc = JsonDocument.ParseValue(ref reader);
        return (JsonObject)FromElement(doc.RootElement)!;
    }

    /// <summary>A node from a parsed element, tolerant of what JsonNode.Parse refuses: a repeated key keeps its first value (the model repeated one: "An item with the same key has already been added. Key: }") and a junk key (empty, or no letter or digit) is dropped.</summary>
    public static JsonNode? FromElement(JsonElement e)
    {
        switch (e.ValueKind)
        {
            case JsonValueKind.Object:
                var o = new JsonObject();
                foreach (var p in e.EnumerateObject())
                {
                    if (!p.Name.Any(char.IsLetterOrDigit) || o.ContainsKey(p.Name)) continue;
                    o[p.Name] = FromElement(p.Value);
                }
                return o;
            case JsonValueKind.Array:
                var a = new JsonArray();
                foreach (var x in e.EnumerateArray()) a.Add(FromElement(x));
                return a;
            default:
                return JsonNode.Parse(e.GetRawText());
        }
    }

    /// <summary>A text field of a model answer: a string, or the "en"/"text" of an object ("The node must be of type 'JsonValue'" came from reading an object as a string); anything else is missing (null).</summary>
    public static string? Txt(JsonNode? n) => n switch
    {
        JsonValue v when v.TryGetValue<string>(out var s) => s,
        JsonValue v when v.TryGetValue<double>(out var d) => d.ToString(System.Globalization.CultureInfo.InvariantCulture),
        JsonObject o => Txt(o["en"] ?? o["text"]),
        JsonArray a => a.Select(Txt).FirstOrDefault(x => !string.IsNullOrEmpty(x)),
        _ => null,
    };

    /// <summary>An integer from a node: a number, a numeric string, or an object holding "cluster"; else null.</summary>
    public static int? AsInt(JsonNode? n) => n switch
    {
        JsonValue v when v.TryGetValue<int>(out var i) => i,
        JsonValue v when v.TryGetValue<string>(out var s) && int.TryParse(s, out var j) => j,
        JsonObject o => AsInt(o["cluster"]),
        _ => null,
    };

    static readonly Regex Cjk = new(@"[\u3400-\u9fff]");
    public static bool HasCjk(string? s) => s != null && Cjk.IsMatch(s);

    /// <summary>The English fields of a raw describe reply that hold Chinese characters, or null: teaser, synopsis, title.en, fullTitle.en, character.en and each cast roleAtStart, description and label.</summary>
    public static string? CjkProblem(JsonObject r)
    {
        var bad = new List<string>();
        void Chk(string name, JsonNode? n) { if (HasCjk(Txt(n))) bad.Add(name); }
        Chk("teaser.en", r["teaser"]); Chk("synopsis.en", r["synopsis"]); Chk("title.en", (r["title"] as JsonObject)?["en"]); Chk("fullTitle.en", r["fullTitle"]);
        foreach (var c in (r["cast"] as JsonArray) ?? [])
            if (c is JsonObject o) { var id = o["cluster"]; Chk($"cast {id} roleAtStart", o["roleAtStart"]); Chk($"cast {id} description", o["description"]); Chk($"cast {id} label.en", o["label"]); Chk($"cast {id} character.en", (o["character"] as JsonObject)?["en"]); }
        return bad.Count == 0 ? null : "these English fields contain Chinese characters: " + string.Join(", ", bad) + ". Translate them to English, or give a name in pinyin; Chinese only belongs in title.zh and character.zh.";
    }

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

    public const int MaxHint = 80;
    public static readonly string[] RelationKinds = ["parent", "grandparent", "sibling", "child", "cousin", "aunt-uncle", "spouse", "betrothed", "lover", "ex", "servant", "master", "friend", "rival", "enemy", "colleague", "other"];
    public static string CleanKind(JsonNode? n) { var k = (Txt(n) ?? "").Trim().ToLowerInvariant().Replace('_', '-').Replace(' ', '-'); return RelationKinds.Contains(k) ? k : "other"; }
    public static readonly string[] TitleSources = ["youtube", "translated", "written"];

    /// <summary>A cast member the model calls main is demoted to supporting when its screen time is below this fraction of the lower-screen-time lead's (setting describe.main-min-screen; env INDEXNODE_DESCRIBE_MAIN_MIN_SCREEN overrides).</summary>
    public static double MainMinScreen => double.TryParse(Environment.GetEnvironmentVariable("INDEXNODE_DESCRIBE_MAIN_MIN_SCREEN"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) && v >= 0 ? v : ConfigMainMinScreen ?? 0.10;
    public static double? ConfigMainMinScreen { get; set; }
    public static int? ConfigMainMax { get; set; }
    /// <summary>0.13.15 (setting describe.main-max; env INDEXNODE_DESCRIBE_MAIN_MAX): at most this many main characters per drama; the ones with the most face time stay.</summary>
    public static int MainMax => int.TryParse(Environment.GetEnvironmentVariable("INDEXNODE_DESCRIBE_MAIN_MAX"), out var v) && v >= 0 ? v : ConfigMainMax ?? 6;

    /// <summary>True when the character's English name (the whole label, or any of its words of 2+ letters: Chinese surnames are short) appears in the text (case-insensitive, whole word).</summary>
    public static bool NamedIn(string? label, string? text)
    {
        if (string.IsNullOrWhiteSpace(label) || string.IsNullOrWhiteSpace(text)) return false;
        bool Has(string w) => System.Text.RegularExpressions.Regex.IsMatch(text, @"(?<![\p{L}\p{N}])" + System.Text.RegularExpressions.Regex.Escape(w.Trim()) + @"(?![\p{L}\p{N}])", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        var l = label.Trim();
        return Has(l) || l.Split([' ', '-', '.', ','], StringSplitOptions.RemoveEmptyEntries).Where(w => w.Length >= 2).Any(Has);
    }

    /// <summary>
    /// The main-cast gate. main = the model says main AND (face time >= min x the lesser lead's, OR the character is named in the title, teaser or synopsis: a villain or a future husband with little face time stays main);
    /// then at most <see cref="MainMax"/> main, keeping the most face time (the rest are demoted, "main limit"). <paramref name="named"/>: the title, full title, teaser and synopsis texts.
    /// </summary>
    public static void DemoteLowScreenMains(JsonArray cast, DescribeInput d, double? min = null, IReadOnlyList<string?>? named = null, int? max = null)
    {
        var secs = d.Clusters.ToDictionary(c => c.Id, c => c.ScreenSeconds);
        double S(JsonNode c) => secs.GetValueOrDefault(c["cluster"]!.GetValue<int>());
        var leads = cast.Where(c => c!["role"]!.GetValue<string>() is "female lead" or "male lead").Select(c => S(c!)).ToList();
        if (leads.Count == 0) return;
        double floor = (min ?? MainMinScreen) * leads.Min();
        bool Named(JsonObject c) => named != null && (c["label"] is JsonObject lb ? lb["en"]?.GetValue<string>() : null) is { } n && named.Any(t => NamedIn(n, t));
        foreach (var c in cast.OfType<JsonObject>().Where(c => c["role"]!.GetValue<string>() == "main" && S(c) < floor && !Named(c))) { c["role"] = "supporting"; c["demoted"] = "screen time"; }
        var mains = cast.OfType<JsonObject>().Where(c => c["role"]!.GetValue<string>() == "main").OrderByDescending(S).ThenBy(c => c["cluster"]!.GetValue<int>()).ToList();
        foreach (var c in mains.Skip(max ?? MainMax)) { c["role"] = "supporting"; c["demoted"] = "main limit"; }
    }

    public static readonly string[] EndingKinds = ["complete", "cliffhanger", "truncated"];

    /// <summary>The model's ending as { kind, signs, nextPart }: kind one of <see cref="EndingKinds"/> else null (the model did not say), at most 6 short English signs, nextPart a real boolean else null, nextPartHint a non-empty string of at most <see cref="MaxHint"/> characters else null. Not an object: null.</summary>
    public static JsonObject? CleanEnding(JsonNode? n)
    {
        if (n is not JsonObject e) return null;
        var kind = (Txt(e["kind"]) ?? "").Trim().ToLowerInvariant();
        var signs = new JsonArray();
        foreach (var s in (e["signs"] as JsonArray) ?? [])
            if (signs.Count < 6 && (Txt(s)?.Trim() ?? "") is { Length: > 0 } c) signs.Add(c.Length > 120 ? c[..120] : c);
        return new JsonObject
        {
            ["kind"] = EndingKinds.Contains(kind) ? kind : null,
            ["signs"] = signs,
            ["nextPart"] = e["nextPart"] is JsonValue v && v.TryGetValue<bool>(out var b) ? b : null,
            ["nextPartHint"] = e["nextPartHint"] is JsonValue hv && hv.TryGetValue<string>(out var h) && h.Trim() is { Length: > 0 and <= MaxHint } ht ? ht : null,   // a string of at most 80 characters, else null (never cut)
        };
    }

    /// <summary>Validates and cleans a model reply into the describe result: English-only text, valid era, roles from the allowed set, one lead each.</summary>
    public static JsonObject Normalize(JsonObject r, DescribeInput d)
    {
        string En(JsonNode? n) => Txt(n)?.Trim() ?? "";
        string Clean(string s) => s.Trim();
        var o = new JsonObject
        {
            ["title"] = new JsonObject { ["en"] = En(r["title"]), ["zh"] = (r["title"] as JsonObject)?["zh"] is { } tz ? Txt(tz) ?? "" : "" },
            ["fullTitle"] = new JsonObject { ["en"] = En(r["fullTitle"]) },
            ["teaser"] = new JsonObject { ["en"] = Clean(En(r["teaser"])) },
            ["synopsis"] = new JsonObject { ["en"] = Clean(En(r["synopsis"])) },
        };
        var ts = (Txt(r["titleSource"]) ?? "").Trim().ToLowerInvariant(); o["titleSource"] = TitleSources.Contains(ts) ? ts : null;
        var tags = new JsonArray();
        foreach (var t in (r["tags"] as JsonArray) ?? []) { var s = Txt(t)?.Trim().ToLowerInvariant() ?? ""; if (s.Length > 0 && !Cjk.IsMatch(s) && !tags.Any(x => Txt(x) == s)) tags.Add(s); }
        o["tags"] = tags;
        var era = Txt(r["era"])?.Trim().ToLowerInvariant() ?? "";
        o["era"] = Eras.Contains(era) ? era : null; o["fantasy"] = r["fantasy"] is JsonValue fv && fv.TryGetValue<bool>(out var fb) && fb;
        o["ending"] = CleanEnding(r["ending"]);
        var ids = d.Clusters.Select(c => c.Id).ToHashSet(); var cast = new JsonArray();
        var canon = SamePerson(r["samePerson"], ids, d); int Cn(int id) => canon.GetValueOrDefault(id, id);
        o["samePerson"] = new JsonArray(canon.GroupBy(kv => kv.Value).Select(g => (JsonNode)new JsonArray(new[] { g.Key }.Concat(g.Select(kv => kv.Key).OrderBy(x => x)).Select(x => (JsonNode)x).ToArray())).ToArray());
        foreach (var e in (r["cast"] as JsonArray) ?? [])
        {
            if (e is not JsonObject c || c["cluster"] is not JsonValue cv || !cv.TryGetValue<int>(out var cid) || !ids.Contains(cid) || canon.ContainsKey(cid) || cast.Any(x => x!["cluster"]!.GetValue<int>() == cid)) continue;
            string Role(string? s)
            {
                s = (s ?? "").Trim().ToLowerInvariant();
                return s is "female lead" or "male lead" or "main" ? s : "supporting";
            }
            var ch = c["character"] as JsonObject;
            cast.Add(new JsonObject
            {
                ["cluster"] = cid, ["character"] = ch == null ? null : new JsonObject { ["zh"] = Txt(ch["zh"]) ?? "", ["pinyin"] = Txt(ch["pinyin"]) ?? "", ["en"] = Txt(ch["en"]) ?? "" },
                ["role"] = Role(Txt(c["role"])), ["roleAtStart"] = Clean(Publish.NotIntroduced(Txt(c["roleAtStart"])) ? Txt(c["description"]) ?? "supporting" : Txt(c["roleAtStart"]) ?? Txt(c["role"]) ?? "supporting"),
                ["description"] = Clean(En(c["description"])), ["actor"] = Txt(c["actor"]) is { Length: > 0 } a && d.Credits.Any(x => x.Contains(a)) ? a : null,
                ["label"] = new JsonObject { ["en"] = Clean(En(c["label"])).TrimEnd('.', ' ') },
            });
        }
        DemoteLowScreenMains(cast, d, null, [En(r["title"]), En(r["fullTitle"]), En(r["teaser"]), En(r["synopsis"])]);
        o["cast"] = cast;
        var rels = new JsonArray();
        foreach (var e in (r["relations"] as JsonArray) ?? [])
        {
            if (e is not JsonObject x || x["from"] is not JsonValue fv2 || x["to"] is not JsonValue tv || !fv2.TryGetValue<int>(out var from) || !tv.TryGetValue<int>(out var to)) continue;
            if (!ids.Contains(from) || !ids.Contains(to)) continue; from = Cn(from); to = Cn(to); if (from == to) continue;
            double since = x["since"] is JsonValue sv && sv.TryGetValue<double>(out var sd) ? sd : 0; double? until = x["until"] is JsonValue uv && uv.TryGetValue<double>(out var ud) ? ud : null;
            rels.Add(new JsonObject { ["from"] = from, ["to"] = to, ["kind"] = CleanKind(x["kind"]), ["type"] = Clean(Txt(x["type"]) ?? "").ToLowerInvariant(), ["label"] = new JsonObject { ["en"] = Clean(En(x["label"])) }, ["since"] = since, ["until"] = until, ["implied"] = x["implied"] is JsonValue iv && iv.TryGetValue<bool>(out var ib) && ib });
        }
        o["relations"] = rels;
        o["picks"] = r["picks"] is JsonObject p ? JsonNode.Parse(p.ToJsonString()) : new JsonObject();
        o["expression"] = CleanExpression(r["expression"]);
        o["masked"] = CleanMasked(r["masked"]);
        o["poster"] = r["poster"] is JsonValue pv2 && pv2.TryGetValue<int>(out var pi) ? pi : null;
        return o;
    }

    /// <summary>The one twist check: given the synopsis, the teaser and the cast, does the teaser or any cast field reveal a twist (something the drama withholds and reveals later)? The same reply carries the corrected teaser and cast fields.</summary>
    public static AiCall BuildTwistCheck(JsonObject res, string model)
    {
        var f = PromptFile.Load("twist-check");
        var cast = JsonSerializer.Serialize(res["cast"]!.AsArray().Select(c => new { cluster = c!["cluster"]!.GetValue<int>(), character = c["character"]?["en"]?.GetValue<string>(), roleAtStart = c["roleAtStart"]!.GetValue<string>(), description = c["description"]!.GetValue<string>(), label = c["label"]!["en"]!.GetValue<string>() }));
        var prompt = f.Text("instructions") + "\n" + f.Fill("evidence", ("title", Txt(res["title"]) ?? ""), ("fullTitle", Txt(res["fullTitle"]) ?? ""), ("synopsis", Txt(res["synopsis"]) ?? ""), ("teaser", Txt(res["teaser"]) ?? ""), ("cast", cast));
        return new AiCall("twist-check", model, prompt, []);
    }

    static string Norm(string? s) => System.Text.RegularExpressions.Regex.Replace((s ?? "").ToLowerInvariant(), @"[^\p{L}\p{N}]+", " ").Trim();

    static IEnumerable<string> NamesOf(JsonNode c) =>
        c["character"] is JsonObject ch ? new[] { "en", "pinyin", "zh" }.Select(k => ch[k] is JsonValue v && v.TryGetValue<string>(out var t) ? t.Trim() : "").Where(t => t.Length >= 2) : [];

    /// <summary>True when <paramref name="label"/> contains <paramref name="name"/> other than as the owner of a relation ("Pei Zimo's mother", "裴子墨的母亲").</summary>
    static bool NamesDirectly(string label, string name)
    {
        int i = 0;
        while ((i = label.IndexOf(name, i, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            int e = i + name.Length; var rest = label[e..];
            bool poss = rest.StartsWith("'s", StringComparison.Ordinal) || rest.StartsWith("\u2019s", StringComparison.Ordinal) || rest.StartsWith("s'", StringComparison.Ordinal) || rest.StartsWith("\u7684", StringComparison.Ordinal);
            bool wordStart = i == 0 || !char.IsLetter(label[i - 1]), wordEnd = e >= label.Length || !char.IsLetter(label[e]) || poss;
            if (wordStart && wordEnd && !poss) return true;
            i = e;
        }
        return false;
    }

    /// <summary>Why a rewritten entry for <paramref name="tgt"/> must be rejected, or null: its label names another cast entry's character (and not its own), or its roleAtStart / description equals another entry's.</summary>
    public static string? IdentityProblem(JsonArray cast, JsonNode tgt, string? role, string? desc, string? label)
    {
        var own = NamesOf(tgt).ToList();
        if (label is { Length: > 0 } && !own.Any(n => label.Contains(n, StringComparison.OrdinalIgnoreCase)))
            foreach (var o in cast)
            {
                if (o == null || ReferenceEquals(o, tgt) || o["cluster"]!.GetValue<int>() == tgt["cluster"]!.GetValue<int>()) continue;
                foreach (var n in NamesOf(o)) if (!own.Any(w => w.Contains(n, StringComparison.OrdinalIgnoreCase)) && NamesDirectly(label, n)) return $"label \"{label}\" names cluster {o["cluster"]}'s character {n}";
            }
        foreach (var o in cast)
        {
            if (o == null || ReferenceEquals(o, tgt) || o["cluster"]!.GetValue<int>() == tgt["cluster"]!.GetValue<int>()) continue;
            if (role is { Length: > 0 } && Norm(role) == Norm(o["roleAtStart"]?.GetValue<string>()) && Norm(role).Length > 0) return $"roleAtStart equals cluster {o["cluster"]}'s";
            if (desc is { Length: > 0 } && Norm(desc) == Norm(o["description"]?.GetValue<string>()) && Norm(desc).Length > 0) return $"description equals cluster {o["cluster"]}'s";
        }
        return null;
    }

    /// <summary>The twist check: one AI call. When it says the teaser or the cast reveals a twist, its corrected teaser and cast fields are applied (a cast entry only for an existing cluster, once, and not when it names or copies another character). Returns the number of AI calls made (1).</summary>
    public static async Task<int> TwistCheckAsync(IChat chat, string model, JsonObject res, DescribeInput d, CancellationToken ct)
    {
        var check = await chat.JsonAsync(model, JsonValue.Create(BuildTwistCheck(res, model).Prompt)!, "submit_check", ct);
        bool reveals = (check["revealsTwist"] is JsonValue rv && rv.TryGetValue<bool>(out var rb) && rb) || (check["wrongCharacter"] is JsonValue wv && wv.TryGetValue<bool>(out var wb) && wb);
        var changed = new JsonArray(); var rejected = new JsonArray(); var ignored = new JsonArray(); bool teaserFixed = false;
        if (reveals)
        {
            if (Txt(check["teaser"])?.Trim() is { Length: > 0 } nt && !HasCjk(nt)) { res["teaser"] = new JsonObject { ["en"] = nt }; teaserFixed = true; }
            var seen = new HashSet<int>(); var cast = res["cast"]!.AsArray();
            foreach (var e in (check["cast"] as JsonArray) ?? [])
            {
                if (e is not JsonObject c || AsInt(c["cluster"]) is not { } cid) { ignored.Add(new JsonObject { ["reason"] = "no cluster id" }); continue; }
                if (cast.FirstOrDefault(x => x!["cluster"]!.GetValue<int>() == cid) is not { } tgt) { ignored.Add(new JsonObject { ["cluster"] = cid, ["reason"] = "not in the cast" }); continue; }
                if (!seen.Add(cid)) { ignored.Add(new JsonObject { ["cluster"] = cid, ["reason"] = "duplicate" }); continue; }
                string? r = Txt(c["roleAtStart"]) is { Length: > 0 } r0 ? r0.Trim() : null, ds = Txt(c["description"]) is { Length: > 0 } d0 ? d0.Trim() : null, lb = Txt(c["label"]) is { Length: > 0 } l0 ? l0.Trim() : null;
                if (lb != null) lb = lb.TrimEnd('.', ' ');
                if (new[] { r, ds, lb }.Any(x => x != null && HasCjk(x))) { rejected.Add(new JsonObject { ["cluster"] = cid, ["reason"] = "Chinese characters in an English field" }); continue; }
                if (IdentityProblem(cast, tgt, r, ds, lb) is { } why) { Log.Info($"describe: twist-check fix for cluster {cid} rejected: {why}"); rejected.Add(new JsonObject { ["cluster"] = cid, ["reason"] = why }); continue; }
                if (lb != null && NamesOf(tgt).Where(n => !HasCjk(n)).ToList() is { Count: > 0 } own && !own.Any(n => lb.Contains(n, StringComparison.OrdinalIgnoreCase)) && Txt(tgt["label"]?["en"]) is { } oldLb && own.Any(n => oldLb.Contains(n, StringComparison.OrdinalIgnoreCase)))
                { rejected.Add(new JsonObject { ["cluster"] = cid, ["reason"] = "label would replace the character's name" }); lb = null; }
                if (r != null) tgt["roleAtStart"] = r;
                if (ds != null) tgt["description"] = ds;
                if (lb != null) tgt["label"] = new JsonObject { ["en"] = lb };
                changed.Add(cid);
            }
        }
        res["twistCheck"] = new JsonObject { ["revealed"] = reveals, ["reasons"] = check["reasons"]?.DeepClone(), ["teaserFixed"] = teaserFixed, ["castFixed"] = changed, ["rejected"] = rejected, ["ignored"] = ignored };
        return 1;
    }

    /// <summary>The whole step: one batched vision call, normalisation, twist check. The result is describe.json's body.</summary>
    public static async Task<JsonObject> RunAsync(IChat chat, string textModel, string visionModel, DescribeInput d, CancellationToken ct)
    {
        var main = BuildMain(d, visionModel);
        var content = ToContent(main);
        var raw = await chat.JsonAsync(visionModel, content, "submit_description", ct);
        int calls = 1;
        if (CjkProblem(raw) is { } cjk)
        {
            Log.Info($"describe {d.JobId}: {cjk} Asking again once with the error");
            raw = await chat.JsonAsync(visionModel, AiJson.WithError(content, raw.ToJsonString(), cjk), "submit_description", ct); calls++;
        }
        var res = Normalize(raw, d);
        // the twist check runs on the text model; a provider content filter there (drama plots trip them) moves it to the vision model, which already read the whole drama
        try { calls += await TwistCheckAsync(chat, textModel, res, d, ct); }
        catch (Exception e) when (e.Message.Contains("content_filter", StringComparison.OrdinalIgnoreCase) && visionModel != textModel)
        {
            Log.Info($"describe {d.JobId}: twist check refused by {textModel}'s content filter; running it on {visionModel}");
            calls += 1 + await TwistCheckAsync(chat, visionModel, res, d, ct);
        }
        res["v"] = 1; res["promptVersion"] = PromptVersion; res["jobId"] = d.JobId;
        res["models"] = new JsonObject { ["text"] = textModel, ["vision"] = visionModel };
        res["aiCalls"] = calls; res["at"] = Protocol.NowUtc();
        return res;
    }
}
