using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace IndexNode;

/// <summary>0.12.0 download pre-check settings (config keys precheck.*; every threshold is a setting).</summary>
public sealed class PreCheckSettings
{
    public bool Enabled { get; set; } = true;
    /// <summary>0.13.56 (owner, 2026-11-25: "at least we should have a check"): at the start of processing, an upload whose stored verdict is older than the published index is checked again on its stored video. Off: the stored verdict is used as it is (before 0.13.56).</summary>
    public bool AgainAtProcessing { get; set; } = true;
    /// <summary>A sample frame counts as a match of an upload when its best cosine there is at least this.</summary>
    public double Tau { get; set; } = 0.70;
    /// <summary>Top share at or above this (and the duration rule) skips the download.</summary>
    public double SkipShare { get; set; } = 0.80;
    /// <summary>Top share in [PartialShare, SkipShare) is another cut of the same drama.</summary>
    public double PartialShare { get; set; } = 0.60;
    /// <summary>The duration rule: |duration - matched duration| &lt;= max(DurationPercent of the matched duration, DurationMinSeconds).</summary>
    public double DurationPercent { get; set; } = 0.15;
    public double DurationMinSeconds { get; set; } = 600;
    /// <summary>0.13.13, the asymmetric rule: a download is a duplicate only when L - DurationPercent*L &lt;= duration &lt;= L + max(DurationSlackPercent*L, DurationSlackSeconds), L = the duration of the LONGEST member of the matched upload's group (the matched upload itself without a group). Longer than that: processed.</summary>
    public double DurationSlackPercent { get; set; } = 0.02;
    public double DurationSlackSeconds { get; set; } = 60;

    /// <summary>Centers whose member blocks are read per frame.</summary>
    public int TopCenters { get; set; } = 100;
    /// <summary>0.13.21 (config precheck.vote-uploads): vote-first lookup: after the bit scan the uploads are ranked by how many frames have them among their top rows, and only the top this-many are read and scored exactly. 0 = read every upload with a top row (the old behaviour). Applies to the pre-check, the check and the group step.</summary>
    public int VoteUploads { get; set; } = 20;
    /// <summary>0.13.58: the screenshot search (/v1/search, `indexnode search`) opens the frames of at most this many uploads, the ones whose centers stand best; a request may ask for another number (maxUploads). 0 = every upload behind the top centers.</summary>
    public int SearchUploads { get; set; } = 0;   // off: see CenterSearch.QueryAsync (measured 2026-11-25: the right upload can stand last among the 200 behind the centers)
    /// <summary>0.13.58: the screenshot search reads only the blocks of the matched centers by range instead of whole frames files. For a search service with no local copy of the frames (a container that scales to zero). Off: whole files, kept in the local cache.</summary>
    public bool SearchRanged { get; set; }
    public double WindowSeconds { get; set; } = 300;
    /// <summary>Centre of the sample window as a fraction of the duration (0.5 = the middle).</summary>
    public double WindowPosition { get; set; } = 0.5;
    /// <summary>0.13.15 (config precheck.window-positions): the two-segment sample: one window-seconds segment centred at each of these fractions of the duration; precheck.frames is the TOTAL, split evenly between them.</summary>
    public double[] WindowPositions { get; set; } = [0.30, 0.70];
    /// <summary>0.13.15 (config precheck.order-tolerance-seconds): the gap between the segments' times in the matched upload may be this much shorter than their gap in the new video (a shorter cut cannot stretch it).</summary>
    public double OrderToleranceSeconds { get; set; } = 120;
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
    /// 0.13.51 (owner, 2026-11-24): the flag is 0.62, no longer the calibration's flag_max1 (0.479). Of 46 uploads held in one run at 0.479 the owner judged 36 live action (all scored 0.65 or less, heavy beauty filters)
    /// and 10 AI (all 0.63 or more): 0.62 would have held the 10 and 4 of the 36. AiStrict stays the calibration's flag_max0 and is only recorded; it is now BELOW the flag.
    public double AiFlag { get; set; } = 0.62;
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
    /// <summary>0.13.57 (owner, 2026-11-25: "we may only want to scan frames between the 25% and 75% marks to sort of avoid the ends when we're doing joins"; "we shouldn't be classifying things strictly based off of end credits"):
    /// where the WHOLE upload is compared (the group step, the groups build), a group is admitted on the hit frames between these two marks of the upload's length only. 0 and 1 switch the rule off.
    /// On 2026-11-25 six episodes of one series had joined each other on their closing credits alone (19 to 22 of 240 sampled frames, all in the last minutes).</summary>
    public double GroupMiddleFrom { get; set; } = 0.25;
    public double GroupMiddleTo { get; set; } = 0.75;
    /// <summary>0.13.6 (config groups.check-frames): the full check of an upload searches at most this many of its frames, evenly spaced over the upload; 0 = all.</summary>
    public int GroupCheckFrames { get; set; } = 240;
    /// <summary>0.13.10 (config retain.enabled / retain.t / retain.f): a drama whose frames are covered (best cosine >= T against earlier dramas) for at least F of them, and that joins a group, keeps only its uncovered frames in index/search/retained/. 0.13.22: T defaults to 0.80 (was 0.90): real screenshots score about 0.62 against their own frame and at most about 0.49 against another drama, so a frame with a >= 0.80 match in its group is a duplicate.</summary>
    public bool RetainEnabled { get; set; } = true;
    public double RetainT { get; set; } = 0.80;
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
/// <summary>0.13.15: how one segment of the sample matched one upload: the share of the segment's frames matched there at Tau and the median matched time in that upload.</summary>
public sealed record SegmentShare(double Share, double MedianTime, int Frames);

/// <summary>0.13.15: one sample segment (positions as fractions of the duration, start/end in the new video's seconds) and its per-upload shares.</summary>
public sealed record PreCheckSegment(double Position, double Start, double End, int Frames, IReadOnlyDictionary<string, SegmentShare> Shares)
{
    public double Mid => (Start + End) / 2;
}

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
    /// <summary>0.13.15: the sample's segments with their per-upload shares (null: an older record or a one-window analysis; the decision then reads the top matches as a single segment).</summary>
    public IReadOnlyList<PreCheckSegment>? Segments { get; init; }
    /// <summary>0.13.2: the frames that matched (cosine >= tau), the group of every upload they hit (null = none yet) and each hit group's known copies; the proposal is made from them by <see cref="DramaGroups.Propose"/>.</summary>
    public IReadOnlyList<FrameHit> Hits { get; init; } = [];
    public IReadOnlyDictionary<string, string?> UploadGroups { get; init; } = new Dictionary<string, string?>();
    public IReadOnlyDictionary<string, IReadOnlyList<GroupCopy>> GroupMembers { get; init; } = new Dictionary<string, IReadOnlyList<GroupCopy>>();
    public GroupProposal? Group { get; init; }
    /// <summary>Centers of the excluded upload that the lookup left out (--exclude-self).</summary>
    public int Excluded { get; init; }
    public static PreCheckAnalysis Skipped(string reason) => new(reason, 0, 0, 0, [], 0, new Dictionary<string, double>());
}

/// <summary>0.13.15: one cut sample segment: the clip, its place in the new video and its position (fraction of the duration).</summary>
public sealed record SampleSegment(string Path, double Start, double End, double Position);

/// <summary>Implemented by the desktop node (frames, SSCD and the search index live in the IndexNode project, not in Core).</summary>
public interface IPreCheckAnalyzer
{
    /// <summary>Null when the pre-check can run here; else why not (no search index, no SSCD model). Checked before anything is downloaded.</summary>
    Task<string?> UnavailableAsync(PreCheckSettings s, CancellationToken ct);
    /// <summary>0.13.56: when the published search index this analyzer answers from was built. Null: not known (no index, or an analyzer that has none), and then no verdict counts as older than it.</summary>
    Task<DateTimeOffset?> IndexBuiltAtAsync(CancellationToken ct) => Task.FromResult<DateTimeOffset?>(null);
    Task<PreCheckAnalysis> AnalyzeAsync(string samplePath, PreCheckSettings s, int threads, Action<string> progress, CancellationToken ct);
    /// <summary>0.13.15: the two-segment analysis (frames of every segment in one SSCD batch, one lookup, per-segment shares, every member of the candidate groups measured); the language ID is NOT run (see <see cref="PreCheck.SpeechNeeded"/>).</summary>
    Task<PreCheckAnalysis> AnalyzeSegmentsAsync(IReadOnlyList<SampleSegment> segments, PreCheckSettings s, int threads, Action<string> progress, CancellationToken ct);
    /// <summary>The language ID of one clip (the first segment's audio).</summary>
    Task<SpeechResult> SpeechAsync(string samplePath, PreCheckSettings s, int threads, CancellationToken ct);
}

/// <summary>One member checked by the two-segment rule: its shares and matched times per segment, the timing result and why it passed or not.</summary>
public sealed record MemberCheck(string Member, string? Group, double? Duration, IReadOnlyList<double> Shares, IReadOnlyList<double?> Times, bool Passed, string Reason);

public sealed record PreCheckDecision(string Decision, IReadOnlyList<string> Reasons, PreCheckMatch? Match)
{
    /// <summary>0.13.13: the longest member of the matched upload's group (id and duration), the L of the duration rule; null when the rule did not run or L is unknown.</summary>
    public (string Id, double Duration)? MatchedLongest { get; init; }
    /// <summary>0.13.15: the matched upload M (a member of the matched group, or the ungrouped upload) that made the decision authoritative; every member checked, with shares, times and the timing result.</summary>
    public string? MatchedMember { get; init; }
    public IReadOnlyList<MemberCheck> Checked { get; init; } = [];
    public JsonObject? Extra { get; init; }
    public const string Duplicate = "duplicate", Subset = "subset", Longer = "longer", Overlap = "overlap", Canonical = "canonical", New = "new", Skipped = "skipped";
    /// <summary>"partial" is retired (0.13.15): old records still carry it and are read; it is never written.</summary>
    public const string Partial = "partial";
    /// <summary>A decision that skips the download (metadata only): duplicate and subset.</summary>
    public static bool IsSkip(string? d) => d is Duplicate or Subset;
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

    /// <summary>0.13.15: the sample segments: one window per position (a video not longer than one window is sampled whole, as a single segment).</summary>
    public static List<(double Start, double End, double Position)> Windows(double duration, double length, IReadOnlyList<double> positions)
    {
        if (duration <= length || positions.Count == 0) return [(0, duration, 0.5)];
        return positions.Select(p => { var w = Window(duration, length, p); return (w.Start, w.End, p); }).ToList();
    }

    /// <summary>0.13.16: the selector of every sample download: the progressive 360p mp4 (format 18, h264+aac: the section is a plain ranged read, no re-encode), else any progressive stream up to 360p, split streams only as the last resort (split HLS formats are fetched segment by segment by ffmpeg and are far slower).</summary>
    public static string SampleFormat(PreCheckSettings s) => $"18/b[height<={s.VideoHeight}][vcodec!=none][acodec!=none]/bv*[height<={s.VideoHeight}]+{(string.IsNullOrWhiteSpace(s.AudioFormat) || s.AudioFormat.Trim() == "wa" ? "ba" : s.AudioFormat.Trim())}/b";

    /// <summary>0.13.15: one yt-dlp call fetching every segment: a --download-sections per range.</summary>
    public static List<string> SampleArgs(PreCheckSettings s, IReadOnlyList<(double Start, double End)> ranges, string url)
    {
        var a = new List<string> { "-f", SampleFormat(s) };
        foreach (var r in ranges) { a.Add("--download-sections"); a.Add(SectionArg(r.Start, r.End)); }
        a.AddRange(["--merge-output-format", "mp4", "-o", "sample.%(ext)s", url]);
        return a;
    }

    /// <summary>The yt-dlp arguments of the one sample download (host-independent: the desktop node and an Android host with Medusa's yt-dlp build the same call).</summary>
    public static List<string> SampleArgs(PreCheckSettings s, double start, double end, string url) =>
        ["-f", SampleFormat(s), "--download-sections", SectionArg(start, end), "--merge-output-format", "mp4", "-o", "sample.%(ext)s", url];

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

    /// <summary>0.13.13: L = the longest member of the group of <paramref name="top"/> (members with a known duration, the matched upload included), else the matched upload itself. Null when no duration is known.</summary>
    public static (string Id, double Duration)? Longest(PreCheckAnalysis a, PreCheckMatch top, double? matchedDuration)
    {
        (string Id, double Duration)? best = matchedDuration is { } md ? (top.Upload, md) : null;
        if (a.UploadGroups.TryGetValue(top.Upload, out var g) && g != null && a.GroupMembers.TryGetValue(g, out var members))
            foreach (var m in members) if (m.Duration is { } d && (best == null || d > best.Value.Duration)) best = (m.Upload, d);
        return best;
    }

    /// <summary>The 0.13.14 single-window rule (kept for what-if comparisons and its tests). <paramref name="matchedDuration"/>: the matched upload's duration, null when the store does not know it (then a skip is never taken). <paramref name="legacy"/>: the 0.13.12 symmetric duration rule (what-if comparisons only).</summary>
    public static PreCheckDecision DecideSingle(PreCheckAnalysis a, double duration, Func<string, double?> matchedDuration, PreCheckSettings s, bool legacy = false)
    {

        if (a.SkippedReason != null) return new(PreCheckDecision.Skipped, [a.SkippedReason], null);
        var top = a.Top.OrderByDescending(m => m.Share).ThenByDescending(m => m.BestCosine).FirstOrDefault();
        if (top == null || top.Share < s.PartialShare) return new(PreCheckDecision.New, [top == null ? "no sample frame matched any indexed upload" : $"top share {top.Share:0.00} < {s.PartialShare:0.00}"], top);
        if (top.Share >= s.SkipShare)
        {
            var md = top.Duration ?? matchedDuration(top.Upload);
            if (md == null) return new(PreCheckDecision.Partial, [$"top share {top.Share:0.00} >= {s.SkipShare:0.00} but the duration of {top.Upload} is unknown (L unknown): not skipped"], top);
            var longest = legacy ? null : Longest(a, top, md);
            double lref = legacy ? md.Value : longest!.Value.Duration;   // L (the asymmetric rule) or the matched upload's own duration (the symmetric one)
            string? tooLong = null;
            if (!legacy && duration > lref + Math.Max(s.DurationSlackPercent * lref, s.DurationSlackSeconds))
                tooLong = $"top share {top.Share:0.00} >= {s.SkipShare:0.00} but longer than the group's longest copy ({duration:0} s vs {lref:0} s, {longest!.Value.Id}; slack max({s.DurationSlackPercent:0%}, {s.DurationSlackSeconds:0} s)): processed";
            if (tooLong != null) return new(PreCheckDecision.Partial, [tooLong], top) { MatchedLongest = longest };
            if (legacy ? DurationClose(duration, md.Value, s) : duration >= lref - s.DurationPercent * lref)
            {
                // canonical = Chinese audio only. A visual duplicate keeps only its metadata, EXCEPT an original-language upload (zh/yue, measured on the audio) of a drama whose known copies are all dubs: that one is processed fully
                // and becomes the canonical copy. The drama's copies are the members of the group the pre-check proposed to join (0.13.2; the matched upload alone when there is none). A copy with no recorded language counts as zh.
                var mine = a.Speech is { Unavailable: null } sp ? NormLang(sp.Language) ?? "zh" : null;   // no speech found: null counts as zh
                // the group's copies when the pre-check proposed a join (every known copy must be a dub), else the matched upload alone
                var copies = a.Group is { Kind: GroupProposal.JoinKind, Copies.Count: > 0 } gp ? gp.Copies.Select(c => (Up: c.Upload, Lang: NormLang(c.SpeechLanguage) ?? "zh")).ToList() : [(top.Upload, NormLang(top.SpeechLanguage) ?? "zh")];
                var theirs = copies.All(c => c.Lang != "zh") ? copies[0].Lang : "zh";
                var dup = new[] { $"top share {top.Share:0.00} >= {s.SkipShare:0.00}", legacy ? $"duration {duration:0}s within max({s.DurationPercent:0%}, {s.DurationMinSeconds:0}s) of {top.Upload} ({md.Value:0}s)" : $"duration {duration:0}s within [-{s.DurationPercent:0%}, +max({s.DurationSlackPercent:0%}, {s.DurationSlackSeconds:0}s)] of the group's longest copy {longest!.Value.Id} ({lref:0}s)" };
                if (mine == "zh" && theirs != "zh") return new(PreCheckDecision.Canonical, [dup[0], dup[1], copies.Count > 1 ? $"the new upload is original-language (zh) and all {copies.Count} known copies in the group are dubs ({string.Join(", ", copies.Select(c => c.Up + ":" + c.Lang))}): processed as the canonical copy" : $"the new upload is original-language (zh) and {top.Upload} is a {theirs} dub: processed as the canonical copy"], top) { MatchedLongest = longest };
                return new(PreCheckDecision.Duplicate, dup, top) { MatchedLongest = longest };
            }
            return new(PreCheckDecision.Partial, [legacy ? $"top share {top.Share:0.00} >= {s.SkipShare:0.00} but duration {duration:0}s differs from {top.Upload} ({md.Value:0}s) by more than max({s.DurationPercent:0%}, {s.DurationMinSeconds:0}s)" : $"top share {top.Share:0.00} >= {s.SkipShare:0.00} but shorter than the group's longest copy by more than {s.DurationPercent:0%} ({duration:0} s vs {lref:0} s, {longest!.Value.Id}): processed"], top) { MatchedLongest = longest };
        }
        return new(PreCheckDecision.Partial, [$"top share {top.Share:0.00} in [{s.PartialShare:0.00}, {s.SkipShare:0.00}): another cut of the same drama"], top);
    }

    /// <summary>
    /// 0.13.15, the two-segment rule. Candidates: every group (or ungrouped upload) either segment matched; the one with the highest combined share first. Inside a group EVERY member is tried in turn (ordered by the sum of its segment shares) until one passes:
    /// each segment's share against the member >= SkipShare, and the timing is consistent in the member (segment 1 before segment 2, the gap there >= the gap in the new video minus OrderToleranceSeconds). The first member that passes is authoritative:
    /// N within [M - durationPercent*M, M + max(slackPercent*M, slackSeconds)] = duplicate; N shorter = subset (a part of M); N longer = longer (processed). No member passes: the best group share >= PartialShare = overlap, else new.
    /// Duplicate and subset keep the canonical-language exception. The shares of every member come with the analysis (a member the lookup did not list is measured directly from its frames file by the analyzer).
    /// </summary>
    public static PreCheckDecision Decide(PreCheckAnalysis a, double duration, Func<string, double?> matchedDuration, PreCheckSettings s, bool legacy = false)
    {
        if (a.SkippedReason != null) return new(PreCheckDecision.Skipped, [a.SkippedReason], null);
        if (a.Segments is not { Count: > 0 } segs)
        {   // an older / one-window analysis: the single-window rule, its "partial" written as overlap
            var d = DecideSingle(a, duration, matchedDuration, s, legacy);
            return !legacy && d.Decision == PreCheckDecision.Partial ? d with { Decision = PreCheckDecision.Overlap } : d;
        }
        string KeyOf(string up) => a.UploadGroups.TryGetValue(up, out var g) && g != null ? g : "u:" + up;
        var uploads = segs.SelectMany(x => x.Shares.Keys).Distinct(StringComparer.Ordinal).ToList();
        var groups = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var u in uploads) { var k = KeyOf(u); if (!groups.TryGetValue(k, out var l)) groups[k] = l = []; l.Add(u); }
        foreach (var (g, copies) in a.GroupMembers) if (groups.TryGetValue(g, out var l)) foreach (var c in copies) if (!l.Contains(c.Upload)) l.Add(c.Upload);   // every member of a candidate group is tried, listed by the lookup or not
        double Sh(PreCheckSegment sg, string u) => sg.Shares.TryGetValue(u, out var x) ? x.Share : 0;
        double Sum(string u) => segs.Sum(sg => Sh(sg, u));
        double GroupCombined(List<string> ms) => segs.Sum(sg => ms.Max(m => Sh(sg, m)));
        var order = groups.OrderByDescending(kv => GroupCombined(kv.Value)).ThenBy(kv => kv.Key, StringComparer.Ordinal).ToList();
        var checks = new List<MemberCheck>();
        double bestGroupShare = 0; string? bestUpload = null;
        foreach (var (gk, ms0) in order)
        {
            string? gid = gk.StartsWith("u:", StringComparison.Ordinal) ? null : gk;
            foreach (var u in ms0) foreach (var sg in segs) { var v = Sh(sg, u); if (v > bestGroupShare) { bestGroupShare = v; bestUpload = u; } }
            var copies = gid != null && a.GroupMembers.TryGetValue(gid, out var gc) ? gc : [];
            foreach (var m in ms0.OrderByDescending(Sum).ThenBy(x => x, StringComparer.Ordinal))
            {
                double? md = copies.FirstOrDefault(c => c.Upload == m)?.Duration ?? a.Top.FirstOrDefault(t => t.Upload == m)?.Duration ?? matchedDuration(m);
                var shares = segs.Select(sg => Sh(sg, m)).ToList();
                var times = segs.Select(sg => sg.Shares.TryGetValue(m, out var x) && x.Frames > 0 ? (double?)x.MedianTime : null).ToList();
                string reason; bool ok = false;
                if (shares.Any(x => x < s.SkipShare)) reason = "share " + string.Join("/", shares.Select(x => x.ToString("0.00", CultureInfo.InvariantCulture))) + $" below {s.SkipShare:0.00} in a segment";
                else if (md == null) reason = "duration unknown";
                else if (segs.Count >= 2 && times[0] is not { } t1 || segs.Count >= 2 && times[1] is not { }) reason = "no matched time";
                else if (segs.Count >= 2 && !(times[0]!.Value < times[1]!.Value)) reason = $"segments out of order in {m} ({times[0]:0}s then {times[1]:0}s)";
                else if (segs.Count >= 2 && times[1]!.Value - times[0]!.Value < (segs[1].Mid - segs[0].Mid) - s.OrderToleranceSeconds) reason = $"gap in {m} {times[1]!.Value - times[0]!.Value:0}s is shorter than the gap in the new video {segs[1].Mid - segs[0].Mid:0}s by more than {s.OrderToleranceSeconds:0}s";
                else { ok = true; reason = "both segments match in order"; }
                checks.Add(new MemberCheck(m, gid, md, shares, times, ok, reason));
                if (!ok) continue;
                double M = md!.Value, lo = M - s.DurationPercent * M, hi = M + Math.Max(s.DurationSlackPercent * M, s.DurationSlackSeconds);
                var asTop = a.Top.FirstOrDefault(t => t.Upload == m);
                var mt = new PreCheckMatch(m, shares.Average(), asTop?.BestCosine ?? 0, times[0] ?? 0, segs.Sum(x => x.Shares.TryGetValue(m, out var q) ? q.Frames : 0), asTop?.SpeechLanguage ?? copies.FirstOrDefault(c => c.Upload == m)?.SpeechLanguage, asTop?.Dubbed ?? copies.FirstOrDefault(c => c.Upload == m)?.Dubbed, M);
                var longest = Longest(a, mt, M);
                string basis = $"member {m} matches both segments (shares {string.Join("/", shares.Select(x => x.ToString("0.00", CultureInfo.InvariantCulture)))}, times {string.Join("/", times.Select(x => x?.ToString("0") ?? "?"))} s, in order)";
                var extra = new JsonObject { ["memberDuration"] = Math.Round(M, 2), ["duration"] = Math.Round(duration, 2), ["timesInMember"] = new JsonArray(times.Select(x => (JsonNode?)(x == null ? null : Math.Round(x.Value, 1))).ToArray()) };
                PreCheckDecision R(string d, string why) => new(d, [basis, why], mt) { MatchedMember = m, MatchedLongest = longest, Checked = checks.ToList(), Extra = extra };
                if (duration > hi) return R(PreCheckDecision.Longer, $"longer than {m}: {duration:0} s vs {M:0} s + max({s.DurationSlackPercent:0%}, {s.DurationSlackSeconds:0} s): processed");
                string kind = duration < lo ? PreCheckDecision.Subset : PreCheckDecision.Duplicate;
                string why2 = kind == PreCheckDecision.Subset ? $"{duration:0} s is shorter than {M:0} s by more than {s.DurationPercent:0%}: a part of {m}" : $"duration {duration:0} s within [-{s.DurationPercent:0%}, +max({s.DurationSlackPercent:0%}, {s.DurationSlackSeconds:0} s)] of {m} ({M:0} s)";
                if (kind == PreCheckDecision.Subset) extra["subsetOf"] = m;
                // canonical = Chinese audio only: a zh-audio upload whose group copies are all dubs is processed fully and becomes the canonical copy
                var mine = a.Speech is { Unavailable: null } sp ? NormLang(sp.Language) ?? "zh" : null;
                var cs = (copies.Count > 0 ? copies.Select(c => (Up: c.Upload, Lang: NormLang(c.SpeechLanguage) ?? "zh")) : [(m, NormLang(mt.SpeechLanguage) ?? "zh")]).ToList();
                if (mine == "zh" && cs.All(c => c.Lang != "zh")) return R(PreCheckDecision.Canonical, $"{why2}; the new upload is original-language (zh) and all {cs.Count} known copies are dubs ({string.Join(", ", cs.Select(c => c.Up + ":" + c.Lang))}): processed as the canonical copy");
                return R(kind, why2);
            }
        }
        var topM = bestUpload == null ? null : new PreCheckMatch(bestUpload, bestGroupShare, a.Top.FirstOrDefault(t => t.Upload == bestUpload)?.BestCosine ?? 0, 0, 0);
        var note = $"{checks.Count} member(s) checked in {order.Count} candidate group(s), none passed both segments in order";
        if (bestGroupShare >= s.PartialShare) return new(PreCheckDecision.Overlap, [$"best segment share {bestGroupShare:0.00} >= {s.PartialShare:0.00} ({bestUpload}); {note}"], topM) { Checked = checks };
        return new(PreCheckDecision.New, [bestUpload == null ? "no sample frame matched any indexed upload" : $"best segment share {bestGroupShare:0.00} < {s.PartialShare:0.00}", note], topM) { Checked = checks };
    }

    /// <summary>0.13.15: the language ID can change the decision only for an authoritative skip (duplicate or subset) whose group copies are ALL dubs (the canonical-language exception); everything else skips it (the full download is transcribed anyway).</summary>
    public static bool SpeechNeeded(PreCheckAnalysis a, PreCheckDecision d)
    {
        if (!PreCheckDecision.IsSkip(d.Decision) || d.MatchedMember == null) return false;
        var copies = a.GroupMembers.Values.FirstOrDefault(c => c.Any(x => x.Upload == d.MatchedMember));
        var langs = copies != null ? copies.Select(c => NormLang(c.SpeechLanguage) ?? "zh") : [NormLang(d.Match?.SpeechLanguage) ?? "zh"];
        return langs.All(l => l != "zh");
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
            ["settings"] = new JsonObject { ["tau"] = s.Tau, ["skipShare"] = s.SkipShare, ["partialShare"] = s.PartialShare, ["durationPercent"] = s.DurationPercent, ["durationMinSeconds"] = s.DurationMinSeconds, ["durationSlackPercent"] = s.DurationSlackPercent, ["durationSlackSeconds"] = s.DurationSlackSeconds, ["topCenters"] = s.TopCenters, ["windowSeconds"] = s.WindowSeconds, ["videoHeight"] = s.VideoHeight, ["audioFormat"] = s.AudioFormat, ["frames"] = s.Frames, ["languageSeconds"] = s.LanguageSeconds, ["languageMinSeconds"] = s.LanguageMinSeconds, ["windowPosition"] = s.WindowPosition, ["windowPositions"] = new JsonArray(s.WindowPositions.Select(x => (JsonNode?)x).ToArray()), ["orderToleranceSeconds"] = s.OrderToleranceSeconds },
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
        if (a.Segments is { Count: > 0 } sgs)
            o["segments"] = new JsonArray(sgs.Select(x => (JsonNode?)new JsonObject
            {
                ["position"] = x.Position, ["start"] = Math.Round(x.Start, 2), ["end"] = Math.Round(x.End, 2), ["frames"] = x.Frames,
                ["shares"] = new JsonObject(x.Shares.OrderByDescending(kv => kv.Value.Share).Take(12).Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)new JsonObject { ["share"] = Math.Round(kv.Value.Share, 4), ["time"] = Math.Round(kv.Value.MedianTime, 1), ["frames"] = kv.Value.Frames }))),
            }).ToArray());
        if (d.MatchedMember != null) { o["matchedMember"] = d.MatchedMember; o["matchUpload"] = d.MatchedMember; }
        if (d.Extra is { } ex) foreach (var kv in ex) o[kv.Key] = kv.Value?.DeepClone();
        if (d.Checked.Count > 0)
            o["membersChecked"] = new JsonArray(d.Checked.Select(c => (JsonNode?)new JsonObject
            {
                ["member"] = c.Member, ["group"] = c.Group, ["duration"] = c.Duration == null ? null : Math.Round(c.Duration.Value, 2), ["shares"] = new JsonArray(c.Shares.Select(x => (JsonNode?)Math.Round(x, 4)).ToArray()),
                ["times"] = new JsonArray(c.Times.Select(x => (JsonNode?)(x == null ? null : Math.Round(x.Value, 1))).ToArray()), ["passed"] = c.Passed, ["reason"] = c.Reason,
            }).ToArray());
        if (a.Speech == null) o["speech"]!["speechSource"] = a.Segments != null ? "skipped (not needed)" : o["speech"]!["speechSource"];
        if (d.MatchedLongest is { } ml) o["matchedLongest"] = new JsonObject { ["id"] = ml.Id, ["duration"] = Math.Round(ml.Duration, 2) };
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
