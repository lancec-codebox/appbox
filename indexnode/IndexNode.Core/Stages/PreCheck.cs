using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace IndexNode;

/// <summary>0.12.0 download pre-check settings (config keys precheck.*; every threshold is a setting).</summary>
public sealed class PreCheckSettings
{
    public bool Enabled { get; set; } = true;
    /// <summary>A sample frame counts as a match of an upload when its best cosine there is at least this.</summary>
    public double Tau { get; set; } = 0.70;
    /// <summary>Top share at or above this (and the duration rule) skips the download.</summary>
    public double SkipShare { get; set; } = 0.80;
    /// <summary>Top share in [PartialShare, SkipShare) is another cut of the same drama.</summary>
    public double PartialShare { get; set; } = 0.60;
    /// <summary>The duration rule: |duration - matched duration| &lt;= max(DurationPercent of the matched duration, DurationMinSeconds).</summary>
    public double DurationPercent { get; set; } = 0.15;
    public double DurationMinSeconds { get; set; } = 600;
    /// <summary>Centers whose member blocks are read per frame.</summary>
    public int TopCenters { get; set; } = 100;
    public double WindowSeconds { get; set; } = 300;
    /// <summary>Centre of the sample window as a fraction of the duration (0.5 = the middle).</summary>
    public double WindowPosition { get; set; } = 0.5;
    /// <summary>The sample video is the best stream of at most this height (config: precheck.video-height).</summary>
    public int VideoHeight { get; set; } = 360;
    /// <summary>yt-dlp format of the sample's audio, for the language ID: the lowest-bitrate audio-only format ('wa' = worst audio, on YouTube 249 opus ~50 kbit/s); a format id such as 249 also works (config: precheck.audio-format).</summary>
    public string AudioFormat { get; set; } = "wa";
    /// <summary>At most this many sample frames go through SSCD, evenly subsampled; 0 = all (config: precheck.frames, 0 or 'all').</summary>
    public int Frames { get; set; } = 60;
    public static int ParseFrames(string v) => v.Trim().Equals("all", StringComparison.OrdinalIgnoreCase) ? 0 : Math.Max(0, int.Parse(v, CultureInfo.InvariantCulture));
    /// <summary>Seconds of audio WITH A LANGUAGE the language ID gathers: ~15 s chunks from the middle outwards, each one SenseVoice pass, nospeech chunks dropped, stop at this many seconds (60 = 4 chunks); 0 = every chunk (config: 0 or 'all'). A window with less uses what it has.</summary>
    public double LanguageSeconds { get; set; } = 60;
    /// <summary>Under this much speech the language is unknown: speechLanguage null, languageSource 'no-speech', the metadata hint kept (null counts as zh for the canonical rule).</summary>
    public double LanguageMinSeconds { get; set; } = 10;
    public static double ParseLanguageSeconds(string v) => v.Trim().Equals("all", StringComparison.OrdinalIgnoreCase) ? 0 : Math.Max(0, double.Parse(v, CultureInfo.InvariantCulture));
    /// <summary>The AI flag: score &gt;= AiFlag (flag_max1) sets ai=true; AiStrict (flag_max0) is recorded next to it.</summary>
    public double AiFlag { get; set; } = 0.479;
    public double AiStrict { get; set; } = 0.529;
    /// <summary>The frame count the AI thresholds above were calibrated at (60 evenly subsampled frames). With another precheck.frames they do not apply: the record says so and a warning is logged.</summary>
    public const int AiThresholdFrames = 60;
    /// <summary>Null when the thresholds apply to this run; else the warning (frames setting, or the frames actually scored, differs from the calibration).</summary>
    public static string? AiThresholdWarning(PreCheckSettings s, int framesScored)
    {
        if (s.Frames != AiThresholdFrames) return $"precheck.frames is {(s.Frames == 0 ? "all" : s.Frames.ToString())}: the AI thresholds ({s.AiFlag}/{s.AiStrict}) were calibrated at {AiThresholdFrames} frames and may not apply";
        if (framesScored != AiThresholdFrames && framesScored > 0 && framesScored < AiThresholdFrames) return $"only {framesScored} frames scored (thresholds calibrated at {AiThresholdFrames}): the AI score may not match them";
        return null;
    }
    /// <summary>How long a node trusts its cached search index before it re-checks head.yaml.</summary>
    public int IndexRecheckMinutes { get; set; } = 10;
    /// <summary>0.13.2 drama groups. Join the group with the most matched frames at >= this many distinct-frame hits (config precheck.group-min-hits).</summary>
    public int GroupMinHits { get; set; } = 5;
    /// <summary>0.13.6 (config groups.check-frames): the full check of an upload searches at most this many of its frames, evenly spaced over the upload; 0 = all.</summary>
    public int GroupCheckFrames { get; set; } = 240;
    /// <summary>0.13.10 (config retain.enabled / retain.t / retain.f): a drama whose frames are covered (best cosine >= T against earlier dramas) for at least F of them, and that joins a group, keeps only its uncovered frames in index/search/retained/.</summary>
    public bool RetainEnabled { get; set; } = true;
    public double RetainT { get; set; } = 0.90;
    public double RetainF { get; set; } = 0.8;
    /// <summary>0.13.6 (config groups.cache-mb): byte cap of the member-block cache the groups checks share (LRU; a dropped block is read again).</summary>
    public int GroupCacheMb { get; set; } = 2048;
    /// <summary>0.13.6 (config groups.max-new-per-build): at most this many new uploads are full-checked per search-index build; the rest wait for the next one.</summary>
    public int GroupMaxNewPerBuild { get; set; } = 100;
    /// <summary>MIXED needs the hits under this share of the samples ...</summary>
    public double MixedMaxShare { get; set; } = 0.60;
    /// <summary>... at a consistent time offset (hits within this many seconds of the median offset, 80% of them) ...</summary>
    public double OffsetTolerance { get; set; } = 30;
    /// <summary>... covering one contiguous part: the hits fill at least this fraction of the frames from the first to the last hit.</summary>
    public double ContiguousFill { get; set; } = 0.7;
    /// <summary>Match share = frames with cosine >= this in the group; duplicate share = >= DupCosine (recorded, not decisive).</summary>
    public double MatchCosine { get; set; } = 0.75;
    public double DupCosine { get; set; } = 0.85;
}

/// <summary>One candidate upload: the share of sample frames whose best match is in it with cosine &gt;= tau.</summary>
public sealed record PreCheckMatch(string Upload, double Share, double BestCosine, double BestTime, int Frames, string? SpeechLanguage = null, bool? Dubbed = null, double? Duration = null);

/// <summary>What the analyzer measured on the sample clip (frames, SSCD, lookup, AI probe). SkippedReason set: nothing was measured.</summary>
/// <summary>The spoken-language measurement (SenseVoice language ID on ~60 s of the sample's audio): majority language and dubbed flag, or why it could not run (then the record falls back to the metadata hint).</summary>
/// <summary>One ~15 s audio chunk of the language ID: SenseVoice's audio-event tag ('speech', 'bgm', 'applause', 'laughter', ... lower case; '' when none) and language token ('und' when nospeech or no text).</summary>
public sealed record LangChunk(double Start, double Seconds, string Event, string Language);

public sealed record SpeechResult(string? Language, bool Dubbed, int Segments, double SpeechSeconds, double AudioSeconds, string? Unavailable)
{
    /// <summary>~15 s chunks that had a language / were nospeech or empty (no VAD: the audio is cut into chunks from the middle outwards).</summary>
    public int ChunksUsed { get; init; }
    public int ChunksNoSpeech { get; init; }
    /// <summary>Why the language is null: 'no-speech' or 'unclear' (null when it is set, or the model did not run).</summary>
    public string? Source { get; init; }
    /// <summary>Every chunk tried (start, event, language), for the audit trail in precheck.json.</summary>
    public IReadOnlyList<LangChunk> Chunks { get; init; } = [];
}

public sealed record PreCheckAnalysis(string? SkippedReason, int IndexVersion, int Frames, int NoHit, IReadOnlyList<PreCheckMatch> Top, double AiScore, IReadOnlyDictionary<string, double> Seconds)
{
    public SpeechResult? Speech { get; init; }
    /// <summary>0.13.2: the frames that matched (cosine >= tau), the group of every upload they hit (null = none yet) and each hit group's known copies; the proposal is made from them by <see cref="DramaGroups.Propose"/>.</summary>
    public IReadOnlyList<FrameHit> Hits { get; init; } = [];
    public IReadOnlyDictionary<string, string?> UploadGroups { get; init; } = new Dictionary<string, string?>();
    public IReadOnlyDictionary<string, IReadOnlyList<GroupCopy>> GroupMembers { get; init; } = new Dictionary<string, IReadOnlyList<GroupCopy>>();
    public GroupProposal? Group { get; init; }
    /// <summary>Centers of the excluded upload that the lookup left out (--exclude-self).</summary>
    public int Excluded { get; init; }
    public static PreCheckAnalysis Skipped(string reason) => new(reason, 0, 0, 0, [], 0, new Dictionary<string, double>());
}

/// <summary>Implemented by the desktop node (frames, SSCD and the search index live in the IndexNode project, not in Core).</summary>
public interface IPreCheckAnalyzer
{
    /// <summary>Null when the pre-check can run here; else why not (no search index, no SSCD model). Checked before anything is downloaded.</summary>
    Task<string?> UnavailableAsync(PreCheckSettings s, CancellationToken ct);
    Task<PreCheckAnalysis> AnalyzeAsync(string samplePath, PreCheckSettings s, int threads, Action<string> progress, CancellationToken ct);
}

public sealed record PreCheckDecision(string Decision, IReadOnlyList<string> Reasons, PreCheckMatch? Match)
{
    public const string Duplicate = "duplicate", Partial = "partial", Canonical = "canonical", New = "new", Skipped = "skipped";
}

public static class PreCheck
{
    /// <summary>The one contiguous window, centred at <paramref name="position"/> of the duration and clamped to the video; a video not longer than the window is sampled whole.</summary>
    public static (double Start, double End, bool Whole) Window(double duration, double length, double position)
    {
        if (duration <= length) return (0, duration, true);
        var start = Math.Clamp(duration * position - length / 2, 0, duration - length);
        return (Math.Round(start, 2), Math.Round(start + length, 2), false);
    }

    /// <summary>The yt-dlp arguments of the one sample download (host-independent: the desktop node and an Android host with Medusa's yt-dlp build the same call).</summary>
    public static List<string> SampleArgs(PreCheckSettings s, double start, double end, string url) =>
        ["-S", $"res:{s.VideoHeight}", "-f", $"bv*+{(string.IsNullOrWhiteSpace(s.AudioFormat) ? "wa" : s.AudioFormat.Trim())}/b", "--download-sections", SectionArg(start, end), "--merge-output-format", "mp4", "-o", "sample.%(ext)s", url];

    /// <summary>Indexes of <paramref name="max"/> frames spread evenly over <paramref name="n"/> (all of them when max &lt;= 0 or max &gt;= n).</summary>
    public static int[] SubsampleIndices(int n, int max)
    {
        if (max <= 0 || max >= n) return Enumerable.Range(0, n).ToArray();
        return Enumerable.Range(0, max).Select(k => (int)((k + 0.5) * n / max)).ToArray();
    }

    public const int DubMinChunks = 3; public const double DubAgreement = 2.0 / 3;

    /// <summary>
    /// The language rule on the ~15 s chunks. Only chunks tagged Speech with a language count (BGM, Applause, Laughter and other events, nospeech and empty chunks do not: a song must not make a call).
    /// Majority language by seconds (yue counts as zh). zh: original, not dubbed. A non-Chinese call (dubbed) needs at least <see cref="DubMinChunks"/> counted chunks AND at least 2/3 of them agreeing;
    /// otherwise the language is null, source 'unclear' (null counts as zh, so it never blocks a Chinese canonical). Under <paramref name="minSeconds"/> counted: null, 'no-speech' (or 'unclear' when only music/events were heard).
    /// </summary>
    public static (string? Language, bool Dubbed, string? Source, double Seconds, int Used, int NoSpeech) LanguageVerdict(IReadOnlyList<LangChunk> chunks, double minSeconds)
    {
        static bool Counts(LangChunk c) => c.Event == "speech" && c.Language is not ("und" or "");
        var counted = chunks.Where(Counts).Select(c => c with { Language = c.Language == "yue" ? "zh" : c.Language }).ToList();
        double total = counted.Sum(c => c.Seconds); int none = chunks.Count - counted.Count;
        if (counted.Count == 0 || total < minSeconds)
            return (null, false, chunks.Any(c => c.Event is not ("speech" or "nospeech" or "") && c.Language is not ("und" or "")) && counted.Count == 0 ? "unclear" : "no-speech", total, counted.Count, none);
        var by = counted.GroupBy(c => c.Language).Select(g => (Lang: g.Key, Secs: g.Sum(c => c.Seconds), N: g.Count())).OrderByDescending(x => x.Secs).ThenBy(x => x.Lang, StringComparer.Ordinal).ToList();
        var maj = by[0];
        if (maj.Lang == "zh") return ("zh", false, null, total, counted.Count, none);
        if (counted.Count >= DubMinChunks && maj.N >= DubAgreement * counted.Count - 1e-9) return (maj.Lang, true, null, total, counted.Count, none);
        return (null, false, "unclear", total, counted.Count, none);
    }

    /// <summary>The yt-dlp info a node sends the service: the fields the record keeps and the language rule reads (audio formats with language and note only; no URLs, no description, no comments).</summary>
    public static JsonObject SlimInfo(JsonObject info)
    {
        var o = new JsonObject();
        foreach (var k in new[] { "id", "title", "channel", "uploader", "channel_id", "upload_date", "height", "language", "duration" }) if (info[k] is { } v) o[k] = v.DeepClone();
        o["subtitles"] = Keys(info["subtitles"]).Aggregate(new JsonObject(), (a, k) => { a[(string)k!] = new JsonArray(); return a; });
        var auto = new JsonObject(); foreach (var kv in (info["automatic_captions"] as JsonObject) ?? []) if (kv.Key.EndsWith("-orig", StringComparison.Ordinal)) auto[kv.Key] = new JsonArray(); o["automatic_captions"] = auto;
        var fmts = new JsonArray();
        foreach (var f in (info["formats"] as JsonArray ?? []).OfType<JsonObject>())
            if (f["vcodec"]?.ToString() is "none" && f["acodec"]?.ToString() is { } ac && ac != "none") fmts.Add(new JsonObject { ["vcodec"] = "none", ["acodec"] = ac, ["language"] = f["language"]?.DeepClone(), ["format_note"] = f["format_note"]?.DeepClone() });
        o["formats"] = fmts;
        return o;
    }

    /// <summary>The decision, the analysis summary and the AI flag out of a precheck.json body (what the service returned).</summary>
    public static (PreCheckDecision Decision, PreCheckAnalysis Analysis, bool Ai) FromRecord(JsonObject rec)
    {
        var top = (rec["top"] as JsonArray ?? []).OfType<JsonObject>().Select(m => new PreCheckMatch((string)m["upload"]!, (double?)m["share"] ?? 0, (double?)m["bestCosine"] ?? 0, (double?)m["bestTime"] ?? 0, (int?)m["frames"] ?? 0, (string?)m["speechLanguage"], (bool?)m["dubbed"])).ToList();
        var reasons = (rec["reasons"] as JsonArray ?? []).Select(r => r?.ToString() ?? "").ToList();
        var up = (string?)rec["matchUpload"];
        var decision = new PreCheckDecision((string?)rec["decision"] ?? PreCheckDecision.Skipped, reasons, up == null ? null : top.FirstOrDefault(m => m.Upload == up));
        var ai = rec["ai"] as JsonObject; var secs = new Dictionary<string, double>();
        foreach (var kv in (rec["seconds"] as JsonObject) ?? []) if (kv.Value is JsonValue v && v.TryGetValue<double>(out var d)) secs[kv.Key] = d;
        var a = new PreCheckAnalysis(null, (int?)rec["indexVersion"] ?? 0, (int?)rec["frames"] ?? 0, (int?)rec["noHit"] ?? 0, top, (double?)ai?["score"] ?? 0, secs) { Group = GroupProposal.FromJson(rec["group"] as JsonObject) };
        return (decision, a, (bool?)ai?["flag"] ?? false);
    }

    public static string SectionArg(double start, double end) => string.Create(CultureInfo.InvariantCulture, $"*{start:0.##}-{end:0.##}");

    /// <summary>zh and yue are one language here (the original); null stays null.</summary>
    public static string? NormLang(string? l) { if (string.IsNullOrWhiteSpace(l)) return null; l = l.Trim().ToLowerInvariant(); return l.StartsWith("zh") || l == "yue" ? "zh" : l; }

    public static bool DurationClose(double duration, double matched, PreCheckSettings s) => Math.Abs(duration - matched) <= Math.Max(s.DurationPercent * matched, s.DurationMinSeconds);

    /// <summary>The rule. <paramref name="matchedDuration"/>: the matched upload's duration, null when the store does not know it (then a skip is never taken).</summary>
    public static PreCheckDecision Decide(PreCheckAnalysis a, double duration, Func<string, double?> matchedDuration, PreCheckSettings s)
    {
        if (a.SkippedReason != null) return new(PreCheckDecision.Skipped, [a.SkippedReason], null);
        var top = a.Top.OrderByDescending(m => m.Share).ThenByDescending(m => m.BestCosine).FirstOrDefault();
        if (top == null || top.Share < s.PartialShare) return new(PreCheckDecision.New, [top == null ? "no sample frame matched any indexed upload" : $"top share {top.Share:0.00} < {s.PartialShare:0.00}"], top);
        if (top.Share >= s.SkipShare)
        {
            var md = top.Duration ?? matchedDuration(top.Upload);
            if (md == null) return new(PreCheckDecision.Partial, [$"top share {top.Share:0.00} >= {s.SkipShare:0.00} but the duration of {top.Upload} is unknown: not skipped"], top);
            if (DurationClose(duration, md.Value, s))
            {
                // canonical = Chinese audio only. A visual duplicate keeps only its metadata, EXCEPT an original-language upload (zh/yue, measured on the audio) of a drama whose known copies are all dubs: that one is processed fully
                // and becomes the canonical copy. The drama's copies are the members of the group the pre-check proposed to join (0.13.2; the matched upload alone when there is none). A copy with no recorded language counts as zh.
                var mine = a.Speech is { Unavailable: null } sp ? NormLang(sp.Language) ?? "zh" : null;   // no speech found: null counts as zh
                // the group's copies when the pre-check proposed a join (every known copy must be a dub), else the matched upload alone
                var copies = a.Group is { Kind: GroupProposal.JoinKind, Copies.Count: > 0 } gp ? gp.Copies.Select(c => (Up: c.Upload, Lang: NormLang(c.SpeechLanguage) ?? "zh")).ToList() : [(top.Upload, NormLang(top.SpeechLanguage) ?? "zh")];
                var theirs = copies.All(c => c.Lang != "zh") ? copies[0].Lang : "zh";
                var dup = new[] { $"top share {top.Share:0.00} >= {s.SkipShare:0.00}", $"duration {duration:0}s within max({s.DurationPercent:0%}, {s.DurationMinSeconds:0}s) of {top.Upload} ({md.Value:0}s)" };
                if (mine == "zh" && theirs != "zh") return new(PreCheckDecision.Canonical, [dup[0], dup[1], copies.Count > 1 ? $"the new upload is original-language (zh) and all {copies.Count} known copies in the group are dubs ({string.Join(", ", copies.Select(c => c.Up + ":" + c.Lang))}): processed as the canonical copy" : $"the new upload is original-language (zh) and {top.Upload} is a {theirs} dub: processed as the canonical copy"], top);
                return new(PreCheckDecision.Duplicate, dup, top);
            }
            return new(PreCheckDecision.Partial, [$"top share {top.Share:0.00} >= {s.SkipShare:0.00} but duration {duration:0}s differs from {top.Upload} ({md.Value:0}s) by more than max({s.DurationPercent:0%}, {s.DurationMinSeconds:0}s)"], top);
        }
        return new(PreCheckDecision.Partial, [$"top share {top.Share:0.00} in [{s.PartialShare:0.00}, {s.SkipShare:0.00}): another cut of the same drama"], top);
    }

    public const string LangOriginal = "format-original", LangOnly = "format-only", LangVideo = "video", LangNone = "none";

    /// <summary>
    /// The metadata HINT of the spoken language (owner-accepted: on our corpus 23/24 dubs caught, 1/30 originals wrongly flagged). SenseVoice on the sample's audio decides (<see cref="LanguageVerdict"/>); this is only the fallback when it could not run. Exactly this order:
    /// (1) an audio format whose format_note says "original" or "(default)": its language; (2) else all audio formats carry exactly one language: that one;
    /// (3) else the video's <c>language</c>; (4) else none: language null, source "none", not dubbed. Dubbed = a language is present and it is not zh*.
    /// The source says which rule fired. (TODO: drop this hint once SenseVoice has run on every upload; it is kept in the record as metadataLanguage.)
    /// </summary>
    public static (string? Language, bool Dubbed, string Source) LanguageFromInfo(JsonObject info, DownloadedFormats? fmt)
    {
        static string? L(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
        var tracks = fmt?.AudioTracks ?? [];
        var orig = tracks.Select(x => (Lang: L(x.Language), x.Note)).FirstOrDefault(x => x.Lang != null && x.Note != null && (x.Note.Contains("original", StringComparison.OrdinalIgnoreCase) || x.Note.Contains("(default)", StringComparison.OrdinalIgnoreCase)));
        string? lang; string src;
        if (orig.Lang != null) { lang = orig.Lang; src = LangOriginal; }
        else if (tracks.Count > 0 && tracks.All(x => L(x.Language) != null) && tracks.Select(x => L(x.Language)!).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 1) { lang = L(tracks[0].Language); src = LangOnly; }
        else if (L((string?)info["language"]) is { } vl) { lang = vl; src = LangVideo; }
        else { lang = null; src = LangNone; }
        return (lang, lang != null && !lang.StartsWith("zh", StringComparison.OrdinalIgnoreCase), src);
    }

    static JsonArray Keys(JsonNode? n) => new(((n as JsonObject)?.Select(kv => (JsonNode?)JsonValue.Create(kv.Key)).ToArray()) ?? []);

    /// <summary>index/videos/&lt;id&gt;/precheck.json (v1).</summary>
    public static JsonObject Record(string id, PreCheckDecision d, PreCheckAnalysis a, JsonObject info, DownloadedFormats? fmt, (double Start, double End) window, double duration, PreCheckSettings s, string nodeId, string version, bool ai, double sampleDownloadSeconds, bool downloaded)
    {
        var (mdLang, mdDubbed, mdSrc) = LanguageFromInfo(info, fmt);
        // speechLanguage / dubbed come from the language ID on the audio; the metadata is only a hint, and the fallback when the model could not run (said in the record)
        var sp = a.Speech;
        bool measured = sp is { Unavailable: null, Language: not null };
        string? lang = measured ? sp!.Language : mdLang; bool dubbed = measured ? sp!.Dubbed : mdDubbed;
        bool noSpeech = sp is { Unavailable: null, Language: null }; string noSpeechSrc = sp?.Source ?? "no-speech";
        if (noSpeech) { lang = null; dubbed = false; }
        string? fallback = measured ? null : (sp?.Unavailable ?? (sp != null ? $"under {s.LanguageMinSeconds:0} s of speech in the window" : "language ID not run"));
        var o = new JsonObject
        {
            ["v"] = 1, ["id"] = id, ["at"] = Protocol.NowUtc(), ["decision"] = d.Decision, ["reasons"] = new JsonArray(d.Reasons.Select(r => (JsonNode?)r).ToArray()),
            ["downloaded"] = downloaded,
            ["top"] = new JsonArray(a.Top.Take(5).Select(m => (JsonNode?)new JsonObject { ["upload"] = m.Upload, ["share"] = Math.Round(m.Share, 4), ["bestCosine"] = Math.Round(m.BestCosine, 4), ["bestTime"] = Math.Round(m.BestTime, 1), ["frames"] = m.Frames, ["speechLanguage"] = m.SpeechLanguage, ["dubbed"] = m.Dubbed }).ToArray()),
            ["window"] = new JsonObject { ["start"] = window.Start, ["end"] = window.End, ["length"] = Math.Round(window.End - window.Start, 2) },
            ["frames"] = a.Frames, ["noHit"] = a.NoHit, ["indexVersion"] = a.IndexVersion,
            ["settings"] = new JsonObject { ["tau"] = s.Tau, ["skipShare"] = s.SkipShare, ["partialShare"] = s.PartialShare, ["durationPercent"] = s.DurationPercent, ["durationMinSeconds"] = s.DurationMinSeconds, ["topCenters"] = s.TopCenters, ["windowSeconds"] = s.WindowSeconds, ["videoHeight"] = s.VideoHeight, ["audioFormat"] = s.AudioFormat, ["frames"] = s.Frames, ["languageSeconds"] = s.LanguageSeconds, ["languageMinSeconds"] = s.LanguageMinSeconds, ["windowPosition"] = s.WindowPosition },
            ["video"] = new JsonObject
            {
                ["duration"] = Math.Round(duration, 2), ["claimedHeight"] = (int?)info["height"], ["title"] = (string?)info["title"], ["channel"] = (string?)info["channel"] ?? (string?)info["uploader"], ["channelId"] = (string?)info["channel_id"],
                ["uploadDate"] = (string?)info["upload_date"], ["subtitles"] = Keys(info["subtitles"]), ["autoCaptions"] = new JsonArray(((info["automatic_captions"] as JsonObject)?.Select(kv => kv.Key).Where(k => k.EndsWith("-orig", StringComparison.Ordinal)).Select(k => (JsonNode?)k).ToArray()) ?? []),
                ["audioTracks"] = new JsonArray((fmt?.AudioTracks ?? []).Select(t => (JsonNode?)new JsonObject { ["language"] = t.Language, ["note"] = t.Note }).ToArray()),
            },
            ["speech"] = new JsonObject
            {
                ["speechLanguage"] = lang, ["dubbed"] = dubbed, ["speechSource"] = measured ? "sensevoice" : noSpeech ? noSpeechSrc : "metadata", ["fallbackReason"] = fallback,
                ["metadataLanguage"] = mdLang, ["languageSource"] = noSpeech ? noSpeechSrc : mdSrc, ["metadataSource"] = mdSrc,
                ["segments"] = sp?.Segments, ["chunksUsed"] = sp?.ChunksUsed, ["chunksNoSpeech"] = sp?.ChunksNoSpeech, ["chunks"] = sp == null ? null : new JsonArray(sp.Chunks.Select(c => (JsonNode?)new JsonObject { ["start"] = Math.Round(c.Start, 1), ["event"] = c.Event, ["language"] = c.Language }).ToArray()), ["speechSeconds"] = sp == null ? null : Math.Round(sp.SpeechSeconds, 1), ["audioSeconds"] = sp == null ? null : Math.Round(sp.AudioSeconds, 1),
            },
            ["ai"] = new JsonObject { ["flag"] = ai, ["score"] = Math.Round(a.AiScore, 4), ["model"] = "ai-frames-v3", ["threshold"] = s.AiFlag, ["framesScored"] = a.Frames, ["thresholdFrames"] = PreCheckSettings.AiThresholdFrames, ["thresholdsApply"] = PreCheckSettings.AiThresholdWarning(s, a.Frames) == null, ["warning"] = PreCheckSettings.AiThresholdWarning(s, a.Frames), ["flagMax0"] = s.AiStrict, ["flagMax1"] = s.AiFlag, ["flagStrict"] = a.AiScore >= s.AiStrict },
            ["seconds"] = new JsonObject(a.Seconds.Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)Math.Round(kv.Value, 3)))) { ["sampleDownload"] = Math.Round(sampleDownloadSeconds, 2) },
            ["node"] = nodeId, ["version"] = version,
        };
        if (a.Group is { } g) { o["group"] = g.ToJson(); o["groupId"] = g.Group; o["groupOffset"] = Math.Round(g.Offset, 2); o["matchShare"] = Math.Round(g.MatchShare, 4); o["dupShare"] = Math.Round(g.DupShare, 4); }
        return o;
    }

    /// <summary>The small summary downloaded.json carries (and the skip case): decision, top match, AI flag and score.</summary>
    public static JsonObject Summary(PreCheckDecision d, PreCheckAnalysis? a, bool? ai, PreCheckSettings s)
    {
        var o = new JsonObject { ["decision"] = d.Decision };
        if (d.Decision == PreCheckDecision.Skipped) { o["reason"] = d.Reasons.FirstOrDefault(); return o; }
        if (d.Match is { } m) { o["match"] = m.Upload; if (d.Decision == PreCheckDecision.Canonical) o["duplicateOf"] = m.Upload; o["share"] = Math.Round(m.Share, 3); o["cosine"] = Math.Round(m.BestCosine, 3); }
        if (a != null) { o["indexVersion"] = a.IndexVersion; o["aiScore"] = Math.Round(a.AiScore, 3); }
        if (ai != null) o["ai"] = ai;
        if (a?.Group is { } g) { o["group"] = g.Group; o["groupKind"] = g.Kind; o["groupOffset"] = Math.Round(g.Offset, 2); o["matchShare"] = Math.Round(g.MatchShare, 3); o["dupShare"] = Math.Round(g.DupShare, 3); if (g.Mixed) o["mixed"] = true; }
        return o;
    }
}
