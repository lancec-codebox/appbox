using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;

namespace IndexNode;

/// <summary>
/// 0.13.21: the sample stage's inline check. After a sample download (files uploaded) it POSTs {id} to a serve instance's /v1/check (a standalone sandbox that wakes on HTTP; the check runs in region there) and gets
/// {decision, record} back. The sandbox writes nothing: this side holds the lease, so it writes the record into video.json and the queue lines (the same lines the check stage writes).
/// Any failure (timeout, 5xx, 401, network error, bad body) returns null with one logged reason: the sample stage then adds the check entry as before.
/// </summary>
public sealed class InlineCheckClient(HttpClient http, string url, string? token, TimeSpan timeout)
{
    long lastCallTicks;   // per client (the node makes one): the last call's time, for the wake detection
    /// <summary>Test hook: the clock for the wake detection.</summary>
    public static Func<DateTimeOffset> Now { get; set; } = () => DateTimeOffset.UtcNow;
    public static readonly TimeSpan WakeAfter = TimeSpan.FromMinutes(2);
    public string Url => url;


    public sealed record Answer(string Decision, JsonObject Record, double Seconds, bool Wake);

    /// <summary>The check; null (and a log line saying why) when it did not answer 200 with a record.</summary>
    public async Task<Answer?> TryAsync(string id, CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var now = Now(); var prev = Interlocked.Exchange(ref lastCallTicks, now.UtcTicks);
        bool wake = prev == 0 || now.UtcTicks - prev > WakeAfter.Ticks;
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct); cts.CancelAfter(timeout);
            using var req = new HttpRequestMessage(HttpMethod.Post, url + "/v1/check") { Content = new StringContent(new JsonObject { ["id"] = id, ["excludeSelf"] = false }.ToJsonString(), Encoding.UTF8, "application/json") };
            if (!string.IsNullOrEmpty(token)) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var res = await http.SendAsync(req, cts.Token);
            var text = await res.Content.ReadAsStringAsync(cts.Token);
            if (res.StatusCode != HttpStatusCode.OK) { Log.Info($"sample {id}: inline check failed ({(int)res.StatusCode} {Short(text)}); adding the check entry"); return null; }
            var o = JsonNode.Parse(text) as JsonObject; var rec = o?["record"] as JsonObject; var d = (string?)o?["decision"] ?? (string?)rec?["decision"];
            if (rec == null || string.IsNullOrEmpty(d)) { Log.Info($"sample {id}: inline check answered without a record; adding the check entry"); return null; }
            var secs = sw.Elapsed.TotalSeconds;
            Log.Info($"sample {id}: inline check {d} in {secs:0.0}s{(wake ? " (wake: first call after more than 2 min idle)" : "")}");
            return new Answer(d, rec, secs, wake);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { Log.Info($"sample {id}: inline check timed out after {timeout.TotalSeconds:0}s; adding the check entry"); return null; }
        catch (Exception e) when (e is HttpRequestException or System.Text.Json.JsonException or IOException or InvalidOperationException) { Log.Info($"sample {id}: inline check failed ({Redact.Sas(e.Message)}); adding the check entry"); return null; }
    }

    static string Short(string s) { s = Redact.Sas(s.Replace('\n', ' ')); return s.Length > 160 ? s[..160] : s; }

    /// <summary>The record into video.json (the caller holds the lease), the same facts the check stage's RecordAsync writes.</summary>
    public static Task RecordAsync(IStore store, Paths paths, string id, JsonObject rec, CancellationToken ct) => VideoJson.UpdateAsync(store, paths, id, root =>
    {
        root["precheck"] = JsonNode.Parse(rec.ToJsonString());
        var sp = rec["speech"] as JsonObject; var ai = rec["ai"] as JsonObject;
        if ((string?)sp?["speechLanguage"] is { Length: > 0 } lang) VideoJson.SetSpeech(root, lang, (bool?)sp?["dubbed"], "precheck");
        if ((double?)ai?["score"] is { } score) VideoJson.SetAi(root, score, (bool?)ai?["flag"], "precheck");
    }, ct);

    /// <summary>The lines the check stage's outcome amounts to, to go in the sample's own final append: the check entry, finished (duplicate/subset: state duplicate, metadata-only, no download; anything else: done, and the full download added).</summary>
    public static void AddOutcome(JobContext ctx, JsonObject rec)
    {
        var id = ctx.Job.JobId; var decision = (string?)rec["decision"]; var at = Protocol.NowUtc();
        var c = new JobDescriptor { Stage = "check", JobId = id, Url = ctx.Job.Url ?? "https://www.youtube.com/watch?v=" + id, MaxHeight = ctx.Job.MaxHeight, Format = ctx.Job.Format, Subs = ctx.Job.Subs, MaxComments = ctx.Job.MaxComments, Priority = ctx.Job.Priority, AddedAt = at, Pub = ctx.Job.Pub, PubSource = ctx.Job.PubSource };
        ctx.FollowUps.Add(QLine.Add(id, "check", c, at));
        if (PreCheckDecision.IsSkip(decision))
        {
            var top = (rec["top"] as JsonArray)?.OfType<JsonObject>().FirstOrDefault();
            var sp = rec["speech"] as JsonObject; var share = (double?)top?["share"] ?? 0;
            c.FinishedAt = at; c.DuplicateOf = (string?)rec["matchUpload"] ?? (string?)top?["upload"] ?? "?"; c.DuplicateShare = share;
            c.DuplicateSpeechLanguage = (string?)sp?["speechLanguage"]; c.DuplicateDubbed = (bool?)sp?["dubbed"]; c.DuplicateLanguageSource = (string?)sp?["languageSource"]; c.DuplicateMetadataLanguage = (string?)sp?["metadataLanguage"]; c.DuplicateUrl = ctx.Job.Url;
            ctx.FollowUps.Add(new QLine { T = "done", Stage = "check", State = PreCheckDecision.Duplicate, Job = c, Why = string.Join("; ", (rec["reasons"] as JsonArray)?.Select(r => r?.ToString()) ?? []), Group = (string?)rec["groupId"], GroupOffset = (double?)rec["groupOffset"], MatchShare = (double?)rec["matchShare"], DupShare = (double?)rec["dupShare"], Mixed = (bool?)(rec["group"] as JsonObject)?["mixed"] is true ? true : null });
            Log.Info($"sample {id}: inline check {decision}: the subject ends metadata-only, nothing is downloaded");
            return;
        }
        ctx.FollowUps.Add(new QLine { T = "done", Stage = "check", Job = c });
        var d = new JobDescriptor { Stage = "download", JobId = id, Url = c.Url, MaxHeight = ctx.Job.MaxHeight, Format = ctx.Job.Format, Subs = ctx.Job.Subs, MaxComments = ctx.Job.MaxComments, Priority = ctx.Job.Priority, AddedAt = at, Pub = ctx.Job.Pub, PubSource = ctx.Job.PubSource };
        ctx.FollowUps.Add(QLine.Add(id, "download", d, at));
        Log.Info($"sample {id}: inline check {decision}: the full download is queued");
    }
}
