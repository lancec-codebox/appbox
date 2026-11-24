namespace IndexNode;

/// <summary>
/// 0.13.32 "indexnode queue whatif --stage S [--set versions.cast=2] [--set stage.actors=run] [--top N] [--ids]": read-only. The queue state (snapshot + tail) and the in-memory control overrides go through the engine's OWN readiness rules
/// (<see cref="StageDeps"/>): which subjects would be READY for the stage now, in claim order, and why the others are not (counted by blocker; the counts add up to the subjects looked at). Nothing is written.
/// </summary>
public static class WhatIf
{
    public sealed record Report(string Stage, int Total, int Ready, List<string> First, Dictionary<string, int> Blockers, List<string> Notes);

    /// <summary>Applies --set overrides to a copy of the control file: versions.&lt;step&gt;=N, stage.&lt;stage&gt;=run|drain|stop, mode=, aiDramas=, dynamicRefresh=, classify=.</summary>
    public static ControlFile Apply(ControlFile c, IEnumerable<string> sets)
    {
        var cur = ControlFile.Parse(c.ToBytes());
        foreach (var set in sets)
        {
            var eq = set.IndexOf('='); if (eq <= 0) throw new ArgumentException($"--set {set}: expected key=value");
            var k = set[..eq].Trim(); var v = set[(eq + 1)..].Trim();
            if (k.StartsWith("versions.", StringComparison.OrdinalIgnoreCase)) { var step = k[9..]; if (v.Equals("off", StringComparison.OrdinalIgnoreCase)) cur.Versions?.Remove(step); else (cur.Versions ??= new(StringComparer.Ordinal))[step] = int.Parse(v); }
            else if (k.StartsWith("stage.", StringComparison.OrdinalIgnoreCase)) { (cur.Stages ??= new())[Protocol.CanonicalStage(k[6..])] = v.ToLowerInvariant(); }
            else if (k.Equals("mode", StringComparison.OrdinalIgnoreCase)) cur.Mode = v.ToLowerInvariant();
            else if (k.Equals("aiDramas", StringComparison.OrdinalIgnoreCase)) cur.AiDramas = v.ToLowerInvariant();
            else if (k.Equals("dynamicRefresh", StringComparison.OrdinalIgnoreCase)) cur.DynamicRefresh = v.ToLowerInvariant();
            else if (k.Equals("classify", StringComparison.OrdinalIgnoreCase)) cur.Classify = v.ToLowerInvariant();
            else throw new ArgumentException($"--set {k}: versions.<step>, stage.<stage>, mode, aiDramas, dynamicRefresh or classify");
        }
        return cur;
    }

    static readonly string[] SeqOrdered = ["group", "actors", "cast"];

    public static Report Run(QueueState st, string stage, ControlFile ctl, string nowUtc, ReadyOptions? ro = null, int top = 20, Func<string, bool>? leaseLive = null)
    {
        stage = Protocol.CanonicalStage(stage);
        var opts = (ro ?? new ReadyOptions()) with { Required = ctl.RequiredVersion, Code = _ => int.MaxValue };   // as if this node could make every version: the question is what the swarm would run
        var step = NodeEngine.StandardSteps.Contains(stage) && stage != "process";
        var singleton = stage == ActorsIndexJobs.Stage;
        var subjects = st.Subjects.Values.Where(s => singleton ? s.Id == ActorsIndexJobs.Subject : s.Id != ActorsIndexJobs.Subject).ToList();
        if (singleton && subjects.Count == 0) subjects.Add(new QSubject { Id = ActorsIndexJobs.Subject });
        var level = ctl.Effective(stage); var ready = new List<(QSubject S, int Pri)>(); var bl = new Dictionary<string, int>(StringComparer.Ordinal);
        void Block(string why) => bl[why] = bl.GetValueOrDefault(why) + 1;
        foreach (var s in subjects)
        {
            if (level != Level.Run) { Block($"the stage is off in control ({level.ToString().ToLowerInvariant()})"); continue; }
            var why = Blocker(st, s, leaseLive, stage, step, ctl, opts, nowUtc, out int pri);
            if (why == null) why = NotRunnable(st, s, stage, step, ctl, opts, nowUtc);   // 0.13.36: what the claim would really do
            if (why == null) ready.Add((s, pri)); else Block(why);
        }
        bool bySeq = SeqOrdered.Contains(stage) || step;
        var order = (bySeq ? ready.OrderBy(x => x.S.Seq ?? long.MaxValue).ThenBy(x => x.S.Id, StringComparer.Ordinal) : ready.OrderByDescending(x => x.Pri).ThenBy(x => x.S.Id, StringComparer.Ordinal)).Select(x => x.S.Id).ToList();
        var notes = new List<string> { bySeq ? "claim order: grouping seq, then id" : "claim order: priority, then id" };
        // 0.13.37: the counts answer for a build that can make every version; say so when THIS build cannot make the required one (its nodes would claim none of them)
        if (Array.IndexOf(StepVersions.Steps, stage) >= 0 || Array.IndexOf(StepVersions.Stages, stage) >= 0)
        { int req = ctl.RequiredVersion(stage), code = StepVersions.Code(stage); if (req > code) notes.Add($"this build makes {stage} v{code}; v{req} is required: a node on this build claims none of these"); }
        return new Report(stage, subjects.Count, ready.Count, order.Take(top).ToList(), bl.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal).ToDictionary(kv => kv.Key, kv => kv.Value), notes);
    }

    /// <summary>0.13.36: the claim's own answer (<see cref="StageDeps.ClaimStages"/>, the rule the node uses) for a subject the readiness view called ready: null = a claim would run the stage; else why it would find nothing. The node's group gate and local stage switches are not known here (control's levels are).</summary>
    static string? NotRunnable(QueueState st, QSubject s, string stage, bool step, ControlFile ctl, ReadyOptions ro, string now)
    {
        if (s.Id == ActorsIndexJobs.Subject) return null;
        var claimStage = step ? "process" : stage;
        var stages = StageDeps.ClaimStages(st, s, now, x => ctl.Effective(x) == Level.Run, (sub, e) => !StageDeps.Held(e) && !(e.Hold == "ai" && !ctl.AiDramasOn) && !(e.Stage == "process" && sub.Entries.TryGetValue("check", out var ck) && !ck.Final), ro);
        if (stages != null && stages.Contains(claimStage)) return null;
        return "not runnable: a claim would find nothing to run" + (stages == null ? "" : " for " + claimStage + " (it would run " + string.Join(",", stages) + ")") + (!s.IsFull ? " (not a fully downloaded upload)" : "");
    }

    /// <summary>null = ready; else the one blocker (the first that applies, in a fixed order: lease, hold, then the stage's own).</summary>
    static string? Blocker(QueueState st, QSubject s, Func<string, bool>? leaseLive, string stage, bool step, ControlFile ctl, ReadyOptions ro, string now, out int pri)
    {
        pri = s.Entries.Values.Select(e => e.Priority).DefaultIfEmpty(0).Max();
        if (s.ClaimBy != null && (leaseLive == null || leaseLive(s.Id))) return "leased (a live lease on its active file)";
        if (s.Entries.Values.Any(e => e.Hold == "ai") && !ctl.AiDramasOn && stage is not ("download" or "sample")) return "hold: AI (aiDramas is off)";
        if (step)
        {
            if (!s.IsFull) return "not a fully downloaded upload";
            if (s.Entries.GetValueOrDefault("process") is not { } pe) return "dependency not done: process (no entry)";
            if (StageDeps.StaleSteps(s, ro).Contains(stage)) { pri = pe.Priority; return null; }   // finished below the required version: ready again, in its repair mode
            if (pe.Final) { if (pe.State != "done") return "process " + pe.State; return $"already done at the required version (v{s.VerOf(stage)}, required {Math.Max(ro.Required(stage), 1)})"; }
            if (pe.StepsDone?.Contains(stage) == true) return $"already done at the required version (v{s.VerOf(stage)}, required {Math.Max(ro.Required(stage), 1)})";
            if (stage == "cast" && ro.Required("cast") > 1 && s.Entries.GetValueOrDefault("group") is not { State: "done" }) return "dependency not done: group";
            if (pe.Runnable(now)) { pri = pe.Priority; return null; }   // the ordinary path: the process entry is pending and due
            return pe.State == "pending" ? "not due yet" : "process " + pe.State;
        }
        if (StageDeps.Derived.Contains(stage) && stage != "process" || stage == "process")
        {
            if (StageDeps.ReadyEntry(st, s, stage, now, ro) is { } re) { pri = re.Priority; return null; }
            if (s.Entries.TryGetValue(stage, out var e))
            {
                if (stage == "actors" && e.State == "done" && (e.Ver ?? 1) < ro.Required("actors"))   // a v1 actors done never satisfies a required v2: it waits for its dependencies, then runs again
                    foreach (var (dep, min) in StageDeps.VersionedNeeds["actors"]) if (!StageDeps.NeedMet(s, dep, min)) return $"actors is at v{e.Ver ?? 1}, required v{ro.Required("actors")}: waits for {dep} at v{min} (now v{s.VerOf(dep)})";
                return EntryBlocker(e, now, stage, ro);
            }
            if (stage == "process" && StageDeps.StaleSteps(s, ro).Count > 0) return null;
            if (StageDeps.VersionedNeeds.TryGetValue(stage, out var needs)) foreach (var (dep, min) in needs) if (!StageDeps.NeedMet(s, dep, min)) return min > 1 ? $"version below the minimum: {dep} is at v{s.VerOf(dep)}, needs v{min}" + (s.Entries.ContainsKey("process") && s.Entries["process"].State == "done" ? "" : " (not done yet)") : $"dependency not done: {dep}";
            if (stage == "process") return s.Data != "full" ? "not a fully downloaded upload" : s.Entries.ContainsKey("download") ? "dependency not done: download" : "no download entry";
            if (stage == ActorsIndexJobs.Stage) return "fewer unindexed actors uploads than the threshold, and the oldest has not waited long enough";
            return "no entry";
        }
        if (s.Entries.TryGetValue(stage, out var e2)) { if (e2.Runnable(now)) { pri = e2.Priority; if (StageDeps.AiWait(s, e2, ro) is { } w) return $"dependency not done: {w} (the AI stages come after every processing stage)"; return StageDeps.Held(e2) ? "hold: AI" : null; } return EntryBlocker(e2, now, stage, ro); }
        return "no entry for the stage";
    }

    static string EntryBlocker(QEntry e, string now, string stage, ReadyOptions ro)
    {
        if (e.Hold != null) return "hold: " + e.Hold;
        if (e.State == "done") return stage is "actors" or "cast" ? $"already done at the required version (v{e.Ver ?? 1})" : "already done";
        if (e.State == "pending") return "not due yet";
        return e.State == "failed" ? "failed" : e.State;
    }
}
