using System.Text.Json.Nodes;

namespace IndexNode;

/// <summary>
/// 0.13.37: stage start/stop marks in the media table. One Insert Or Merge Entity (PATCH, no read, no existence check: one request whether or not the row exists yet) per start and per stop into the partition `stage-&lt;name&gt;` (RowKey = upload id).
/// The stored output (v, mode, at, node) changes only on done; the latest run (run_s, run_v, run_mode, run_at, run_node, run_ep) at every start and stop. A done also merges the stage's
/// own property (named after the stage, a JSON string of the stored-output object) into the upload row. Times are the server's (the Date of the queue append that recorded the line); with none known the time is left out, never the node clock.
/// Order: the queue append first, then the merge; a failed merge is logged and retried once, and never fails the step.
/// </summary>
public static class StageMarks
{
    public const string Prefix = "stage-";
    public static string Partition(string stage) => Prefix + stage;

    static JsonObject Time(string name, DateTimeOffset? at, JsonObject o)
    {
        if (at is { } t) { o[name] = t.UtcDateTime.ToString("o"); o[name + "@odata.type"] = "Edm.DateTime"; }
        return o;
    }

    /// <summary>The properties of a run line. state: running|done|failed|transient|released.</summary>
    public static JsonObject Run(string state, int version, string mode, string node, int epoch, DateTimeOffset? at) =>
        Time("run_at", at, new JsonObject { ["run_s"] = state, ["run_v"] = version, ["run_mode"] = mode, ["run_node"] = node, ["run_ep"] = epoch });

    /// <summary>The stage partition's properties on done: the stored output together with run_s=done.</summary>
    public static JsonObject Done(int version, string mode, string node, int epoch, DateTimeOffset? at)
    {
        var o = Run("done", version, mode, node, epoch, at);
        o["v"] = version; o["mode"] = mode; o["node"] = node; Time("at", at, o);
        return o;
    }

    /// <summary>The per-stage property of the upload row: the stored-output object as a JSON string.</summary>
    public static string StoredJson(int version, string mode, string node, DateTimeOffset? at)
    {
        var o = new JsonObject { ["v"] = version, ["mode"] = mode };
        if (at is { } t) o["at"] = t.UtcDateTime.ToString("o");
        o["node"] = node; return o.ToJsonString();
    }

    /// <summary>A start or a failed / transient / released stop: one merge into the stage partition (run_* only).</summary>
    public static Task RunAsync(IMediaTable? table, string stage, string id, string state, int version, string mode, string node, int epoch, DateTimeOffset? at, CancellationToken ct) =>
        table == null ? Task.CompletedTask : Retry(c => table.InsertOrMergeAsync(Partition(stage), id, Run(state, version, mode, node, epoch, at), c), stage, id, state, ct);

    /// <summary>A done: the stage partition (stored output + run) and the upload row's property of that stage: 2 merges.</summary>
    public static async Task DoneAsync(IMediaTable? table, string stage, string id, int version, string mode, string node, int epoch, DateTimeOffset? at, CancellationToken ct)
    {
        if (table == null) return;
        await Retry(c => table.InsertOrMergeAsync(Partition(stage), id, Done(version, mode, node, epoch, at), c), stage, id, "done", ct);
        await Retry(c => table.InsertOrMergeAsync(MediaTables.Upload, id, new JsonObject { [stage] = StoredJson(version, mode, node, at) }, c), stage, id, "done (upload row)", ct);
    }

    /// <summary>The whole mark (its retries and the HTTP client's own) is given this long, then dropped: a dead table never holds a step or a node's shutdown.</summary>
    public static TimeSpan Deadline { get; set; } = TimeSpan.FromSeconds(10);
    static async Task Retry(Func<CancellationToken, Task> merge, string stage, string id, string what, CancellationToken outer)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(outer); cts.CancelAfter(Deadline);
        for (int attempt = 1; attempt <= 2; attempt++)
        {
            try { await merge(cts.Token); return; }
            catch (OperationCanceledException) { if (cts.IsCancellationRequested && !outer.IsCancellationRequested) Log.Info($"stage mark {stage} {id} {what}: dropped after {Deadline.TotalSeconds:0}s"); return; }
            catch (Exception e) { Log.Info($"stage mark {stage} {id} {what}: {(attempt == 1 ? "failed, retrying once" : "failed, giving up")} ({e.Message})"); }
        }
    }
}
