using System.Text.Json;
using System.Text.Json.Nodes;

namespace IndexNode;

/// <summary>Creating, finding and deciding unclassified download jobs, and the singleton classify job.</summary>
public static class ClassifyJobs
{
    public const string SingletonId = "all";
    public static string SingletonBlob(Paths paths) => paths.Job("classify", SingletonId);

    /// <summary>Creates the singleton index/jobs/classify/all.json when it is missing (create-only: one blob however many creators race); a copy that ended failed or done is made pending again.</summary>
    public static async Task EnsureSingletonAsync(IStore store, Paths paths, CancellationToken ct = default, string? now = null)
    {
        var at = now ?? Protocol.NowUtc();
        var job = new JobDescriptor { Stage = "classify", JobId = SingletonId, Name = "classify all", AddedAt = at };
        var name = SingletonBlob(paths);
        if (await store.PutAsync(name, job.ToBytes(), Tags.Job("classify", "pending", at), null, true, ct)) return;
        var tags = await store.GetTagsAsync(name, ct);
        if (tags == null || tags.GetValueOrDefault(Tags.State) is not ("failed" or "done")) return;
        if (await store.GetAsync(name, ct) is { } c && JobDescriptor.Parse(c.Data) is { } old)
        {
            old.Attempts = 0; old.LastError = null; old.Permanent = null;
            try { await store.PutAsync(name, old.ToBytes(), Tags.Job("classify", "pending", tags.GetValueOrDefault(Tags.Added) ?? at, at), null, false, ct); } catch (StorageException) { } // leased by someone else: it is running
        }
    }

    /// <summary>Writes the download job as unclassified (create-only) and makes sure the singleton exists. False when the job was already there.</summary>
    public static async Task<bool> CreateUnclassifiedAsync(IStore store, Paths paths, JobDescriptor d, CancellationToken ct = default)
    {
        var made = await store.PutAsync(paths.Job("download", d.JobId), d.ToBytes(), Tags.Job("download", Tags.Unclassified, d.AddedAt, null, d.Priority), null, true, ct);
        if (made) await EnsureSingletonAsync(store, paths, ct);
        return made;
    }

    /// <summary>The unclassified download jobs, oldest first (idx_added, then name).</summary>
    public static async Task<List<BlobEntry>> ListUnclassifiedAsync(IStore store, Paths paths, CancellationToken ct)
    {
        IEnumerable<BlobEntry> found;
        try { found = await Tags.FindFullAsync(store, Tags.UnclassifiedWhere, ct); }
        catch (ForbiddenException) { found = (await store.ListAsync(paths.JobsDir("download"), true, false, ct)).Where(b => b.Tags.GetValueOrDefault(Tags.State) == Tags.Unclassified); }
        return found.Where(b => b.Name.StartsWith(paths.JobsDir("download"), StringComparison.Ordinal) && b.Tags.GetValueOrDefault(Tags.State) == Tags.Unclassified)
            .OrderBy(b => b.Tags.GetValueOrDefault(Tags.Added) ?? "", StringComparer.Ordinal).ThenBy(b => b.Name, StringComparer.Ordinal).ToList();
    }

    private static readonly string[] DeadAvailability = ["private", "deleted", "unavailable"];
    private static readonly string[] DeadTitles = ["[Private video]", "[Deleted video]"];

    /// <summary>Private, deleted or unavailable according to the listing, or a flat listing entry with no duration and a placeholder title.</summary>
    public static bool IsUnavailable(JobDescriptor d) =>
        d.Availability != null && DeadAvailability.Contains(d.Availability.Trim().ToLowerInvariant())
        || d.Duration == null && d.Title != null && DeadTitles.Contains(d.Title.Trim(), StringComparer.OrdinalIgnoreCase);

    public static string UnavailableReason(JobDescriptor d) => d.Availability != null && DeadAvailability.Contains(d.Availability.Trim().ToLowerInvariant()) ? $"listed as {d.Availability.Trim().ToLowerInvariant()}" : $"listed as {d.Title}";

    /// <summary>Records a decision on the download job: candidate -> pending (tags kept, idx_classified set); not a candidate -> failed + permanent, reason "classify: kind reason".</summary>
    public static async Task ApplyAsync(IStore store, Paths paths, string blob, IReadOnlyDictionary<string, string> tags, JobDescriptor d, bool candidate, string kind, string reason, string at, CancellationToken ct)
    {
        var t = new Dictionary<string, string>(tags) { [Tags.State] = candidate ? "pending" : "failed", [Tags.Classified] = at };
        if (candidate) { await store.SetTagsAsync(blob, t, null, ct); return; }
        var why = ($"classify: {kind} {reason}").Trim(); if (why.Length > 300) why = why[..300];
        d.Reason = why; d.LastError = why; d.Permanent = true; d.FinishedAt = at;
        await store.PutAsync(blob, d.ToBytes(), t, null, false, ct);
    }
}
