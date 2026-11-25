using System.Text.Json.Nodes;

namespace IndexNode;

/// <summary>One sample frame's best match: frame index in the sample, the upload, the matched time in it and the cosine (only frames at or above precheck.tau).</summary>
public sealed record FrameHit(int Frame, string Upload, double Time, double Cosine);

/// <summary>A known copy of a drama (a group member) as the pre-check sees it.</summary>
public sealed record GroupCopy(string Upload, string? SpeechLanguage, bool? Dubbed, double? Duration = null);

/// <summary>
/// 0.13.2: the pre-check's PROPOSED membership (the search-index job is the only writer of groups). Kind: join (an existing group, through <see cref="JoinUpload"/>), mixed (one contiguous part of an upload, flagged, own group, never canonical), new.
/// MatchShare / DupShare: the fraction of sample frames whose best match is in the group with cosine >= 0.75 / >= 0.85. Offset: median (matched time - sample time), seconds.
/// </summary>
public sealed record GroupProposal(string Kind, string? Group, string? JoinUpload, double Offset, double MatchShare, double DupShare, int Hits, double Share, bool Mixed, IReadOnlyList<GroupCopy> Copies)
{
    public const string JoinKind = "join", MixedKind = "mixed", NewKind = "new";
    public static GroupProposal None => new(NewKind, null, null, 0, 0, 0, 0, 0, false, []);

    public JsonObject ToJson() => new()
    {
        ["kind"] = Kind, ["group"] = Group, ["joinUpload"] = JoinUpload, ["offset"] = Math.Round(Offset, 2), ["matchShare"] = Math.Round(MatchShare, 4), ["dupShare"] = Math.Round(DupShare, 4),
        ["hits"] = Hits, ["share"] = Math.Round(Share, 4), ["mixed"] = Mixed,
        ["copies"] = new JsonArray(Copies.Select(c => (JsonNode?)new JsonObject { ["upload"] = c.Upload, ["speechLanguage"] = c.SpeechLanguage, ["dubbed"] = c.Dubbed }).ToArray()),
    };
    public static GroupProposal? FromJson(JsonObject? o) => o == null ? null : new((string?)o["kind"] ?? NewKind, (string?)o["group"], (string?)o["joinUpload"], (double?)o["offset"] ?? 0, (double?)o["matchShare"] ?? 0, (double?)o["dupShare"] ?? 0, (int?)o["hits"] ?? 0, (double?)o["share"] ?? 0, (bool?)o["mixed"] ?? false,
        (o["copies"] as JsonArray ?? []).OfType<JsonObject>().Select(c => new GroupCopy((string)c["upload"]!, (string?)c["speechLanguage"], (bool?)c["dubbed"])).ToList());
}

/// <summary>A member of a stored group (index/groups/).</summary>
public sealed class GroupMember
{
    public string Upload { get; set; } = "";
    public double? Offset { get; set; }
    public double MatchShare { get; set; }
    public double DupShare { get; set; }
    public string? SpeechLanguage { get; set; }
    public bool? Dubbed { get; set; }
    public bool Mixed { get; set; }
    public double? Duration { get; set; }
    public string? ProcessedAt { get; set; }
    public string? JoinedThrough { get; set; }
    /// <summary>"index": placed by the index build's full check; "precheck": a short-circuited upload placed from its pre-check record (replaced by a full check once it is processed).</summary>
    public string? CheckedBy { get; set; }
    /// <summary>0.13.15, the member's relation to the group's canonical (written by the index job's fold from the drama's grouping record): the share of its own frames the group covers, the share of the canonical's timeline it spans, and the kind (copy / cut / part / longer / mixed).</summary>
    public double? MemberCovered { get; set; }
    public double? CanonicalCovered { get; set; }
    public string? Kind { get; set; }
    /// <summary>Transient (never serialized): the stretches of the grouping record this build folded, for <see cref="GroupRelation"/>.</summary>
    public IReadOnlyList<RelStretch>? Stretches { get; set; }
    public double? CoveredShare { get; set; }
}

/// <summary>One covered run of a member (own seconds [From, To]) matched in <see cref="Source"/> at <see cref="Offset"/> (source time - own time).</summary>
public sealed record RelStretch(double From, double To, string Source, double Offset);

/// <summary>
/// 0.13.15: how a member relates to its group's canonical, derived from numbers the group step already measured.
/// memberCovered = the share of the member's frames the group covers (its grouping record). canonicalCovered = the union of the member's stretches mapped onto the canonical's clock
/// (own time + the stretch's offset in its source + that source's offset to the canonical; the canonical's own stretches map by their offset) divided by the canonical's duration.
/// Kinds: mixed (a mixed cut, as before); copy = both >= <see cref="Threshold"/> and the durations within the duplicate window; longer = canonicalCovered >= 0.8 and memberCovered &lt; 0.8;
/// part = memberCovered >= 0.8, canonicalCovered &lt; 0.8 and ONE contiguous span on the canonical clock; cut = the same with several (scattered) spans; anything else stays mixed.
/// </summary>
public static class GroupRelation
{
    public const double Threshold = 0.8;

    /// <summary>The union of the mapped intervals on the canonical clock, runs closer than <paramref name="gap"/> seconds merged (one contiguous span), clipped to [0, canonicalDuration].</summary>
    public static List<(double From, double To)> Spans(IEnumerable<(double From, double To)> mapped, double canonicalDuration, double gap)
    {
        var iv = mapped.Select(x => (From: Math.Max(0, Math.Min(x.From, x.To)), To: Math.Min(canonicalDuration, Math.Max(x.From, x.To)))).Where(x => x.To > x.From).OrderBy(x => x.From).ToList();
        var res = new List<(double From, double To)>();
        foreach (var x in iv) { if (res.Count > 0 && x.From <= res[^1].To + gap) res[^1] = (res[^1].From, Math.Max(res[^1].To, x.To)); else res.Add(x); }
        return res;
    }

    public static (double? CanonicalCovered, string? Kind, int Spans) Derive(bool mixed, double? memberCovered, double memberDuration, double canonicalDuration, IEnumerable<(double From, double To)> mappedStretches, PreCheckSettings s)
    {
        if (mixed) return (null, "mixed", 0);
        if (memberCovered == null || canonicalDuration <= 0) return (null, null, 0);
        var spans = Spans(mappedStretches, canonicalDuration, s.OffsetTolerance);
        double cc = Math.Min(1, spans.Sum(x => x.To - x.From) / canonicalDuration);
        double lo = canonicalDuration - s.DurationPercent * canonicalDuration, hi = canonicalDuration + Math.Max(s.DurationSlackPercent * canonicalDuration, s.DurationSlackSeconds);
        bool mem = memberCovered >= Threshold, can = cc >= Threshold;
        string kind = mem && can && memberDuration >= lo && memberDuration <= hi ? "copy"
            : can && !mem ? "longer"
            : mem && !can ? (spans.Count <= 1 ? "part" : "cut")
            : "mixed";
        return (Math.Round(cc, 4), kind, spans.Count);
    }
}

public sealed class DramaGroup
{
    public string Id { get; set; } = "";
    public List<GroupMember> Members { get; set; } = [];
    public string? Canonical { get; set; }
    public string? CanonicalWhy { get; set; }
}

/// <summary>What the index job knows of an upload it has not placed yet: its record's proposal plus the facts the election needs.</summary>
public sealed record NewUpload(string Id, string? SpeechLanguage, bool? Dubbed, double? Duration, string? ProcessedAt, GroupProposal? Proposal, bool ShortCircuit = false)
{
    /// <summary>0.13.15: from the drama's grouping record: the share of its frames the group covers and its covered runs (null: no record, or a short-circuited upload).</summary>
    public double? CoveredShare { get; init; }
    public IReadOnlyList<RelStretch>? Stretches { get; init; }
}

/// <summary>The group table: pure logic (proposal from per-frame matches, election, applying proposals at an index build, union for the one-time build) plus its JSON.</summary>
public sealed class GroupSet
{
    public int IndexVersion { get; set; }
    public string? BuiltAt { get; set; }
    public List<DramaGroup> Groups { get; set; } = [];

    public Dictionary<string, string> GroupOf() { var d = new Dictionary<string, string>(StringComparer.Ordinal); foreach (var g in Groups) foreach (var m in g.Members) d[m.Upload] = g.Id; return d; }

    /// <summary>Canonical election: Chinese audio only (a null language counts as zh), then longest, then first processed (then id, for a stable answer). Mixed cuts never. Returns the id (null: no Chinese-audio copy) and why.</summary>
    public static (string? Id, string Why) Elect(IReadOnlyList<GroupMember> members)
    {
        var full = members.Where(m => !m.Mixed).ToList();
        if (full.Count == 0) return (null, "no canonical: every member is a mixed cut");
        var zh = full.Where(m => (PreCheck.NormLang(m.SpeechLanguage) ?? "zh") == "zh").ToList();
        if (zh.Count == 0) return (null, $"no canonical: no Chinese-audio copy ({full.Count} dub{(full.Count == 1 ? "" : "s")}: {string.Join(", ", full.Select(m => m.SpeechLanguage ?? "?").Distinct())})");
        var ordered = zh.OrderByDescending(m => m.Duration ?? -1).ThenBy(m => m.ProcessedAt ?? "\uffff", StringComparer.Ordinal).ThenBy(m => m.Upload, StringComparer.Ordinal).ToList();
        var win = ordered[0];
        string why = $"Chinese audio ({PreCheck.NormLang(win.SpeechLanguage) ?? "language unknown, counts as zh"})";
        if (zh.Count > 1)
        {
            bool tie = ordered.Skip(1).Any(m => (m.Duration ?? -1) == (win.Duration ?? -1));
            why += tie ? $", longest ties at {win.Duration:0}s: first processed ({win.ProcessedAt ?? "?"}) of {zh.Count} Chinese copies" : $", longest ({win.Duration:0}s) of {zh.Count} Chinese copies";
        }
        else why += ", the only Chinese copy";
        return (win.Upload, why);
    }

    /// <summary>Elects every group again (a group changed or a member's facts did). Returns the ids whose canonical changed.</summary>
    public List<string> ElectAll()
    {
        var changed = new List<string>();
        foreach (var g in Groups) { var (id, why) = Elect(g.Members); if (id != g.Canonical) changed.Add(g.Id); g.Canonical = id; g.CanonicalWhy = why; }
        return changed;
    }

    /// <summary>
    /// Applies the new uploads' proposals. A join goes to the CURRENT group of the proposed upload (it may have moved since: group-follow; a target placed in this same batch is waited for); mixed and new start their own group (id g-&lt;first member&gt;).
    /// <paramref name="refresh"/> brings every known member's language/duration up to date first. Everything is re-elected.
    /// </summary>
    public void Apply(IEnumerable<NewUpload> news, Func<string, (string? Lang, bool? Dubbed, double? Duration)?>? refresh = null, bool breakCycles = false)
    {
        if (refresh != null) foreach (var g in Groups) foreach (var m in g.Members) if (refresh(m.Upload) is { } r) { m.SpeechLanguage = r.Lang; m.Dubbed = r.Dubbed; m.Duration = r.Duration ?? m.Duration; }
        var of = GroupOf(); var pending = news.Where(n => !of.ContainsKey(n.Id)).OrderBy(n => n.ProcessedAt ?? "\uffff", StringComparer.Ordinal).ThenBy(n => n.Id, StringComparer.Ordinal).ToList();
        var ids = pending.Select(n => n.Id).ToHashSet(StringComparer.Ordinal);
        GroupMember Mem(NewUpload n, bool mixed) => new() { Upload = n.Id, Offset = n.Proposal?.Offset, MatchShare = n.Proposal?.MatchShare ?? 0, DupShare = n.Proposal?.DupShare ?? 0, SpeechLanguage = n.SpeechLanguage, Dubbed = n.Dubbed, Mixed = mixed, Duration = n.Duration, CoveredShare = n.CoveredShare, Stretches = n.Stretches, ProcessedAt = n.ProcessedAt, JoinedThrough = n.Proposal?.JoinUpload, CheckedBy = n.ShortCircuit ? "precheck" : "index" };
        void Own(NewUpload n) { var g = new DramaGroup { Id = "g-" + n.Id, Members = [Mem(n, n.Proposal?.Mixed == true || n.Proposal?.Kind == GroupProposal.MixedKind)] }; Groups.Add(g); of[n.Id] = g.Id; }
        bool progress = true;
        while (pending.Count > 0 && progress)
        {
            progress = false;
            foreach (var n in pending.ToList())
            {
                var p = n.Proposal;
                if (p == null || p.Kind != GroupProposal.JoinKind || p.JoinUpload == null || p.Mixed) { Own(n); pending.Remove(n); progress = true; continue; }
                if (of.TryGetValue(p.JoinUpload, out var gid)) { Groups.First(g => g.Id == gid).Members.Add(Mem(n, false)); of[n.Id] = gid; pending.Remove(n); progress = true; }
                else if (!ids.Contains(p.JoinUpload) || p.JoinUpload == n.Id) { Own(n); pending.Remove(n); progress = true; }   // the target is not in any group and is not coming: starts its own
            }
        }
        // a join cycle (A joins B, B joins A: both are in the batch, as in the one-time build over a whole index): the first by time owns the group and the rest follow it; otherwise nobody can go first and each stands alone
        while (breakCycles && pending.Count > 0)
        {
            var first = pending[0]; Own(first); pending.Remove(first);
            bool again = true;
            while (again && pending.Count > 0)
            {
                again = false;
                foreach (var n in pending.ToList())
                {
                    var p = n.Proposal;
                    if (p?.JoinUpload != null && !p.Mixed && of.TryGetValue(p.JoinUpload, out var gid)) { Groups.First(g => g.Id == gid).Members.Add(Mem(n, false)); of[n.Id] = gid; pending.Remove(n); again = true; }
                }
            }
        }
        foreach (var n in pending) Own(n);   // a join cycle: nobody can go first
        ElectAll();
    }

    /// <summary>
    /// 0.13.15: fills memberCovered / canonicalCovered / kind of every member that has a grouping record's numbers (a member folded earlier keeps its stored ones). The canonical clock: a stretch of a member covers its own [From, To] mapped to its source's time
    /// (+ the stretch offset), and from the source onto the canonical (+ the source's own offset to the canonical, followed along the chain up to <c>maxHops</c> hops); a stretch whose source is not in the group counts as not mapped.
    /// </summary>
    public void Relate(PreCheckSettings s, int maxHops = 8)
    {
        foreach (var g in Groups)
        {
            var canon = g.Members.FirstOrDefault(m => m.Upload == g.Canonical);
            var by = g.Members.ToDictionary(m => m.Upload, StringComparer.Ordinal);
            // offset of a member to the canonical clock: canonical time = own time + OffsetToCanon(member)
            double? ToCanon(string up, int hops)
            {
                if (up == g.Canonical) return 0;
                if (hops > maxHops || !by.TryGetValue(up, out var m) || m.Offset == null) return null;
                return m.Offset.Value + (m.JoinedThrough != null && m.JoinedThrough != g.Canonical && m.JoinedThrough != up && by.ContainsKey(m.JoinedThrough) ? ToCanon(m.JoinedThrough, hops + 1) ?? 0 : 0);
            }
            foreach (var m in g.Members)
            {
                if (g.Canonical == null || canon?.Duration is not { } cd) { if (m.Mixed) m.Kind = "mixed"; continue; }
                if (m.Upload == g.Canonical) { m.MemberCovered ??= 1; m.CanonicalCovered = 1; m.Kind = "copy"; continue; }
                if (m.Stretches == null) { if (m.Mixed) m.Kind = "mixed"; continue; }
                var mapped = m.Stretches.Select(x => (From: x.From + x.Offset + (ToCanon(x.Source, 0) ?? 0), To: x.To + x.Offset + (ToCanon(x.Source, 0) ?? 0)));
                var (cc, kind, _) = GroupRelation.Derive(m.Mixed, m.CoveredShare ?? m.MemberCovered, m.Duration ?? 0, cd, mapped, s);
                m.MemberCovered = m.CoveredShare ?? m.MemberCovered; m.CanonicalCovered = cc; m.Kind = kind;
            }
        }
    }

    /// <summary>Connected components of the links (the one-time build). Every id in <paramref name="ids"/> is in exactly one component.</summary>
    public static List<List<string>> Components(IEnumerable<string> ids, IEnumerable<(string A, string B)> links)
    {
        var parent = new Dictionary<string, string>(StringComparer.Ordinal); foreach (var i in ids) parent[i] = i;
        string Find(string x) { while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; } return x; }
        foreach (var (a, b) in links) if (parent.ContainsKey(a) && parent.ContainsKey(b)) { var ra = Find(a); var rb = Find(b); if (ra != rb) parent[string.CompareOrdinal(ra, rb) < 0 ? rb : ra] = string.CompareOrdinal(ra, rb) < 0 ? ra : rb; }
        return parent.Keys.GroupBy(Find).Select(g => g.OrderBy(x => x, StringComparer.Ordinal).ToList()).OrderByDescending(c => c.Count).ThenBy(c => c[0], StringComparer.Ordinal).ToList();
    }

    public JsonObject ToJson() => new()
    {
        ["v"] = 1, ["indexVersion"] = IndexVersion, ["builtAt"] = BuiltAt,
        ["groups"] = new JsonArray(Groups.Select(g => (JsonNode?)new JsonObject
        {
            ["id"] = g.Id, ["canonical"] = g.Canonical, ["canonicalWhy"] = g.CanonicalWhy,
            ["members"] = new JsonArray(g.Members.Select(m => (JsonNode?)new JsonObject
            {
                ["upload"] = m.Upload, ["offset"] = m.Offset == null ? null : Math.Round(m.Offset.Value, 2), ["matchShare"] = Math.Round(m.MatchShare, 4), ["dupShare"] = Math.Round(m.DupShare, 4), ["speechLanguage"] = m.SpeechLanguage, ["dubbed"] = m.Dubbed,
                ["mixed"] = m.Mixed, ["duration"] = m.Duration, ["processedAt"] = m.ProcessedAt, ["joinedThrough"] = m.JoinedThrough, ["checkedBy"] = m.CheckedBy,
                ["memberCovered"] = m.MemberCovered == null ? null : Math.Round(m.MemberCovered.Value, 4), ["canonicalCovered"] = m.CanonicalCovered == null ? null : Math.Round(m.CanonicalCovered.Value, 4), ["kind"] = m.Kind,
            }).ToArray()),
        }).ToArray()),
    };

    public static GroupSet FromJson(JsonObject o) => new()
    {
        IndexVersion = (int?)o["indexVersion"] ?? 0, BuiltAt = (string?)o["builtAt"],
        Groups = (o["groups"] as JsonArray ?? []).OfType<JsonObject>().Select(g => new DramaGroup
        {
            Id = (string)g["id"]!, Canonical = (string?)g["canonical"], CanonicalWhy = (string?)g["canonicalWhy"],
            Members = (g["members"] as JsonArray ?? []).OfType<JsonObject>().Select(m => new GroupMember { Upload = (string)m["upload"]!, Offset = (double?)m["offset"], MatchShare = (double?)m["matchShare"] ?? 0, DupShare = (double?)m["dupShare"] ?? 0, SpeechLanguage = (string?)m["speechLanguage"], Dubbed = (bool?)m["dubbed"], Mixed = (bool?)m["mixed"] ?? false, Duration = (double?)m["duration"], ProcessedAt = (string?)m["processedAt"], JoinedThrough = (string?)m["joinedThrough"], CheckedBy = (string?)m["checkedBy"], MemberCovered = (double?)m["memberCovered"], CanonicalCovered = (double?)m["canonicalCovered"], Kind = (string?)m["kind"] }).ToList(),
        }).ToList(),
    };
}

/// <summary>The pre-check's proposal from the per-frame matches (0.13.2).</summary>
public static class DramaGroups
{
    /// <summary>
    /// Join the group with the largest share of matched frames when it has at least <see cref="PreCheckSettings.GroupMinHits"/> distinct-frame hits. MIXED: those hits cover one contiguous part of the sample, under
    /// <see cref="PreCheckSettings.MixedMaxShare"/> of the samples, at a consistent time offset: flagged, never canonical, its own group. Otherwise NEW. An upload with no group yet counts as a group of its own.
    /// </summary>
    /// <param name="ownStart">0.13.57: the times of the upload's first and last compared frame when its WHOLE length was compared (with <paramref name="ownTime"/>): the group is then chosen and admitted on the hits in the middle
    /// part of the upload only (<see cref="PreCheckSettings.GroupMiddleFrom"/> to <see cref="PreCheckSettings.GroupMiddleTo"/>), so shared opening titles and closing credits join nothing.
    /// Once a group is admitted, every hit of it counts for the shares and the offset, as before. Not given (the pre-check's sample windows): every hit counts.</param>
    public static GroupProposal Propose(PreCheckAnalysis a, double windowStart, double windowLength, PreCheckSettings s, Func<int, double>? ownTime = null, double ownStart = 0, double ownEnd = 0)
    {
        int n = a.Frames;
        string Key(string up) => a.UploadGroups.TryGetValue(up, out var g) && g != null ? g : "u:" + up;
        bool middleOnly = ownTime != null && ownEnd > ownStart && (s.GroupMiddleFrom > 0 || s.GroupMiddleTo < 1);
        double lo = ownStart + s.GroupMiddleFrom * (ownEnd - ownStart), hi = ownStart + s.GroupMiddleTo * (ownEnd - ownStart);
        var counted = middleOnly ? a.Hits.Where(h => ownTime!(h.Frame) is var t && t >= lo && t <= hi).ToList() : a.Hits;
        var by = counted.GroupBy(h => Key(h.Upload)).Select(g => (Key: g.Key, Hits: g.OrderBy(h => h.Frame).ToList()))
            .OrderByDescending(x => x.Hits.Count).ThenByDescending(x => x.Hits.Average(h => h.Cosine)).ThenBy(x => x.Key, StringComparer.Ordinal).ToList();
        if (n <= 0 || by.Count == 0 || by[0].Hits.Count < s.GroupMinHits) return GroupProposal.None;
        var key = by[0].Key;
        var hits = middleOnly ? a.Hits.Where(h => Key(h.Upload) == key).OrderBy(h => h.Frame).ToList() : by[0].Hits;
        var join = hits.GroupBy(h => h.Upload).OrderByDescending(g => g.Count()).ThenByDescending(g => g.Max(h => h.Cosine)).ThenBy(g => g.Key, StringComparer.Ordinal).First().Key;
        double share = (double)hits.Count / n;
        var offs = hits.Select(h => h.Time - (ownTime?.Invoke(h.Frame) ?? windowStart + (h.Frame + 0.5) / n * windowLength)).OrderBy(x => x).ToList();
        double med = offs.Count % 2 == 1 ? offs[offs.Count / 2] : (offs[offs.Count / 2 - 1] + offs[offs.Count / 2]) / 2;
        bool consistent = offs.Count(o => Math.Abs(o - med) <= s.OffsetTolerance) >= 0.8 * offs.Count;
        int span = hits[^1].Frame - hits[0].Frame + 1;
        bool contiguous = hits.Count >= s.ContiguousFill * span;
        bool mixed = share < s.MixedMaxShare && contiguous && consistent;
        double match = hits.Count(h => h.Cosine >= s.MatchCosine) / (double)n, dup = hits.Count(h => h.Cosine >= s.DupCosine) / (double)n;
        var copies = a.GroupMembers.TryGetValue(key, out var c) ? c : [new GroupCopy(join, a.Top.FirstOrDefault(m => m.Upload == join)?.SpeechLanguage, a.Top.FirstOrDefault(m => m.Upload == join)?.Dubbed)];
        return new(mixed ? GroupProposal.MixedKind : GroupProposal.JoinKind, key.StartsWith("u:", StringComparison.Ordinal) ? null : key, join, med, match, dup, hits.Count, share, mixed, copies);
    }
}
