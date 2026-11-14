using System.Text.Json;
using System.Text.Json.Nodes;

namespace IndexNode;

/// <summary>One JSON snapshot of the whole index for an admin page. Read only: one listing of the jobs, the node blobs, the control file and the newest finished/failed job blobs.</summary>
public static class StatusSnapshot
{
    /// <summary>At most this many newest-added done/failed job blobs per stage are read for elapsed/node/error details.</summary>
    public const int DetailCap = 150;

    public static async Task<JsonObject> BuildAsync(IStore store, Paths paths, DateTimeOffset now, CancellationToken ct)
    {
        var ctl = ControlFile.Parse((await store.GetAsync(paths.Control, ct))?.Data);
        var nodes = new List<NodeInfo>();
        foreach (var b in await store.ListAsync(paths.NodesDir, false, false, ct))
            if (await store.GetAsync(b.Name, ct) is { } c && NodeInfo.Parse(c.Data) is { } n) nodes.Add(n);
        var all = await store.ListAsync(paths.Prefix + "jobs/", true, true, ct);

        var queue = new JsonObject(); var leased = new JsonArray(); var finished = new List<(DateTimeOffset At, JsonObject Row, string Node, string Stage)>(); var failures = new List<(DateTimeOffset At, JsonObject Row)>();
        foreach (var stage in Protocol.Stages)
        {
            var inStage = all.Where(b => b.Tags.GetValueOrDefault(Tags.Stage) == stage).ToList();
            var q = new JsonObject();
            foreach (var s in new[] { "pending", "done", "failed" }) q[s] = inStage.Count(b => b.Tags.GetValueOrDefault(Tags.State) == s);
            q["leased"] = inStage.Count(b => b.LeaseState == "leased"); queue[stage] = q;
            foreach (var b in inStage.Where(b => b.LeaseState == "leased"))
            {
                DateTimeOffset.TryParse(b.Metadata.GetValueOrDefault("heartbeat"), out var hb);
                leased.Add(new JsonObject
                {
                    ["stage"] = stage, ["jobId"] = Path.GetFileNameWithoutExtension(b.Name), ["node"] = b.Metadata.GetValueOrDefault("node"), ["progress"] = b.Metadata.GetValueOrDefault("progress"),
                    ["heartbeat"] = b.Metadata.GetValueOrDefault("heartbeat"), ["heartbeatAgeSeconds"] = hb == default ? null : Math.Round((now - hb).TotalSeconds),
                });
            }
            foreach (var state in new[] { "done", "failed" })
                foreach (var b in inStage.Where(b => b.Tags.GetValueOrDefault(Tags.State) == state).OrderByDescending(b => b.Tags.GetValueOrDefault(Tags.Added) ?? "").Take(DetailCap))
                {
                    if (await store.GetAsync(b.Name, ct) is not { } c) continue;
                    JobDescriptor j; try { j = JobDescriptor.Parse(c.Data); } catch { continue; }
                    if (state == "done")
                    {
                        if (!DateTimeOffset.TryParse(j.FinishedAt, out var at)) continue;
                        long bytes = 0; // bytes of the job's own outputs are not known to the descriptor: the process marker holds them
                        if (stage == "process" && await store.GetAsync(paths.Video(j.JobId, "processed.json"), ct) is { } pm)
                            try { var r = JsonDocument.Parse(pm.Data).RootElement; bytes = (r.TryGetProperty("compactBytes", out var cb) ? cb.GetInt64() : 0); } catch { }
                        finished.Add((at, new JsonObject { ["stage"] = stage, ["jobId"] = j.JobId, ["node"] = j.NodeId, ["elapsedSeconds"] = j.ElapsedSeconds, ["bytes"] = bytes, ["finishedAt"] = j.FinishedAt }, j.NodeId ?? "", stage));
                    }
                    else
                    {
                        DateTimeOffset.TryParse(j.FinishedAt ?? b.Tags.GetValueOrDefault(Tags.Added), out var at);
                        failures.Add((at, new JsonObject { ["stage"] = stage, ["jobId"] = j.JobId, ["node"] = j.NodeId, ["error"] = Redact.Sas(j.LastError ?? ""), ["attempts"] = j.Attempts, ["permanent"] = j.Permanent == true }));
                    }
                }
        }
        finished.Sort((x, y) => y.At.CompareTo(x.At)); failures.Sort((x, y) => y.At.CompareTo(x.At));

        var throughput = new JsonObject();
        foreach (var stage in Protocol.Stages)
            throughput[stage] = new JsonObject { ["lastHour"] = finished.Count(f => f.Stage == stage && now - f.At <= TimeSpan.FromHours(1)), ["last24h"] = finished.Count(f => f.Stage == stage && now - f.At <= TimeSpan.FromHours(24)) };

        var nodeRows = new JsonArray();
        foreach (var n in nodes.OrderBy(n => n.NodeId))
        {
            var lim = ctl.Nodes != null && ctl.Nodes.TryGetValue(n.NodeId, out var l) ? l : null;
            var mine = finished.Where(f => f.Node == n.NodeId).ToList();
            double perHour = mine.Count(f => now - f.At <= TimeSpan.FromHours(1));
            int? used = lim?.Budget != null ? mine.Count : null; int? remaining = lim?.Budget is { } bud ? Math.Max(0, bud - mine.Count) : null;
            double? etaHours = remaining is { } rem && perHour > 0 ? Math.Round(rem / perHour, 1) : null;
            string? nextWindow = null;
            if (lim?.Schedule != null) { try { nextWindow = CronExpr.Parse(lim.Schedule).Next(now.UtcDateTime)?.ToString("yyyy-MM-ddTHH:mm:ssZ"); } catch { } }
            var state = n.EffectiveState(now);
            if (state != "offline" && n.Running.Count == 0 && remaining == 0) state = "budget spent";
            else if (state != "offline" && n.Running.Count == 0 && nextWindow != null && n.State == "waiting") state = "waiting for window";
            else if (state != "offline") state = n.Running.Count > 0 ? "working" : (n.State is "working" or "idle" ? "idle" : n.State);
            var workers = new JsonArray(); foreach (var r in n.Running) workers.Add(new JsonObject { ["worker"] = r.Worker, ["stage"] = r.Stage, ["jobId"] = r.JobId, ["step"] = r.Step, ["progress"] = r.Progress });
            nodeRows.Add(new JsonObject
            {
                ["nodeId"] = n.NodeId, ["name"] = n.Name, ["kind"] = n.Kind, ["version"] = n.Version, ["stages"] = new JsonArray(n.Stages.Select(s => (JsonNode)s).ToArray()), ["state"] = state, ["workers"] = workers,
                ["traffic"] = TrafficJson(n.Traffic, now), ["firstSeen"] = n.FirstSeen, ["placement"] = n.Placement == null ? null : JsonSerializer.SerializeToNode(n.Placement, Protocol.Json),
                ["lastSeen"] = n.LastSeen, ["budget"] = lim?.Budget, ["budgetRemaining"] = remaining, ["nextWindow"] = nextWindow, ["etaBudgetHours"] = etaHours, ["finishedPerHour"] = perHour,
            });
        }
        return new JsonObject
        {
            ["v"] = 1, ["at"] = now.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ"), ["prefix"] = paths.Prefix,
            ["control"] = new JsonObject { ["mode"] = ctl.Mode, ["stages"] = ctl.Stages == null ? null : JsonSerializer.SerializeToNode(ctl.Stages), ["message"] = ctl.Message, ["changedAt"] = ctl.ChangedAt, ["changedBy"] = ctl.ChangedBy },
            ["nodes"] = nodeRows, ["queue"] = queue, ["leased"] = leased,
            ["finished"] = new JsonArray(finished.Take(50).Select(f => (JsonNode)f.Row).ToArray()), ["failures"] = new JsonArray(failures.Take(50).Select(f => (JsonNode)f.Row).ToArray()),
            ["throughput"] = throughput,
        };
    }

    /// <summary>A node's traffic as JSON: totals, the last-hour and last-24-hour sums, and the hourly buckets.</summary>
    public static JsonObject TrafficJson(TrafficReport? t, DateTimeOffset now)
    {
        t ??= new TrafficReport();
        JsonNode C(TrafficCounts c) => JsonSerializer.SerializeToNode(c, Protocol.Json)!;
        return new JsonObject { ["totals"] = C(t.Totals), ["lastHour"] = C(t.Sum(now, TimeSpan.FromHours(1))), ["last24h"] = C(t.Sum(now, TimeSpan.FromHours(24))), ["hours"] = JsonSerializer.SerializeToNode(t.Hours, Protocol.Json) };
    }

    /// <summary>Atomic replace: write a temp file next to the target, then move it over.</summary>
    public static void WriteAtomic(string file, string json)
    {
        var full = Path.GetFullPath(file); Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        var tmp = full + ".tmp"; File.WriteAllText(tmp, json); File.Move(tmp, full, true);
    }

    public static string Serialize(JsonObject o) => o.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });

    /// <summary>"60s", "5m" or a bare number of seconds.</summary>
    public static TimeSpan ParseEvery(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return TimeSpan.FromSeconds(60);
        s = s.Trim().ToLowerInvariant(); double v = double.Parse(new string(s.TakeWhile(ch => char.IsDigit(ch) || ch == '.').ToArray()), System.Globalization.CultureInfo.InvariantCulture);
        return s.EndsWith('h') ? TimeSpan.FromHours(v) : s.EndsWith('m') ? TimeSpan.FromMinutes(v) : TimeSpan.FromSeconds(v);
    }
}
