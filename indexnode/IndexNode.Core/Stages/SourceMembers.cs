using System.Text.Json;
using System.Text.Json.Nodes;

namespace IndexNode;

/// <summary>One member of a source: what the listing said, and when refresh first listed it.</summary>
public sealed record SourceMember(string Id, string? Title = null, double? Duration = null, string? ChannelId = null, string? ChannelName = null, string? Availability = null, string? FirstSeen = null);

/// <summary>
/// 0.13.2: index/sources/&lt;id&gt;/members.json is the one file of a source (seen.json is gone): the ids the source has listed, newest first, each with a firstSeen. Written only by the holder of the source's lease (the refresh claim),
/// conditional on the ETag it read (a 412 re-reads and merges again). "Is this video new?" is "not in members".
/// </summary>
public static class SourceMembers
{
    public sealed record Doc(string? Etag, List<SourceMember> Members, string? LookedUpAt);

    public static async Task<Doc> ReadAsync(IStore store, Paths paths, string sourceId, CancellationToken ct)
    {
        var c = await store.GetAsync(paths.SourceMembers(sourceId), ct);
        if (c == null) return new Doc(null, [], null);
        var o = JsonNode.Parse(c.Data) as JsonObject ?? throw new InvalidDataException($"{paths.SourceMembers(sourceId)} is not a JSON object");
        return new Doc(c.ETag, o["members"]?.Deserialize<List<SourceMember>>(Protocol.Json) ?? [], (string?)o["lookedUpAt"]);
    }

    /// <summary>
    /// Writes the merge of <paramref name="listed"/> into the file, conditional on its ETag; a 412 reads again and merges again. A playlist is rewritten in full (members it no longer lists go); a channel keeps what it has, newest first.
    /// A member keeps the firstSeen it had. Returns the number of members written.
    /// </summary>
    public static async Task<int> MergeAsync(IStore store, Paths paths, JobDescriptor job, IReadOnlyList<SourceMember> listed, bool lookup, string at, CancellationToken ct)
    {
        for (int attempt = 0; attempt < 20; attempt++)
        {
            var cur = await ReadAsync(store, paths, job.JobId, ct);
            var old = cur.Members.GroupBy(m => m.Id, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
            var members = listed.Select(m => m with { FirstSeen = old.GetValueOrDefault(m.Id)?.FirstSeen ?? m.FirstSeen ?? at }).ToList();
            if (job.Kind == "channel") members.AddRange(cur.Members.Where(h => members.All(m => m.Id != h.Id)));
            var doc = new { v = 2, sourceId = job.JobId, kind = job.Kind, id = job.Id, name = job.Name, updatedAt = at, lookedUpAt = lookup ? at : cur.LookedUpAt ?? at, members };
            if (await store.PutIfMatchAsync(paths.SourceMembers(job.JobId), JsonSerializer.SerializeToUtf8Bytes(doc, Protocol.Json), cur.Etag, ct)) return members.Count;
        }
        throw new IOException($"{job.JobId}: members.json kept changing (is another node refreshing it?)");
    }

    /// <summary>Adds the ids as members (id only, firstSeen = <paramref name="firstSeen"/>) where the file has no entry for them, then returns how many were added. Conditional like <see cref="MergeAsync"/>.</summary>
    public static async Task<int> AddIdsAsync(IStore store, Paths paths, string sourceId, IEnumerable<string> ids, string firstSeen, CancellationToken ct, bool dryRun = false)
    {
        var want = ids.Distinct(StringComparer.Ordinal).ToList();
        for (int attempt = 0; attempt < 20; attempt++)
        {
            var c = await store.GetAsync(paths.SourceMembers(sourceId), ct);
            JsonObject root = c == null ? new JsonObject { ["v"] = 2, ["sourceId"] = sourceId, ["updatedAt"] = firstSeen } : JsonNode.Parse(c.Data) as JsonObject ?? throw new InvalidDataException($"{paths.SourceMembers(sourceId)} is not a JSON object");
            var arr = root["members"] as JsonArray ?? new JsonArray();
            var have = arr.OfType<JsonObject>().Select(m => (string?)m["id"]).ToHashSet(StringComparer.Ordinal);
            var add = want.Where(i => !have.Contains(i)).ToList();
            if (add.Count == 0 || dryRun) return add.Count;
            foreach (var i in add) arr.Add(new JsonObject { ["id"] = i, ["firstSeen"] = firstSeen });
            root["members"] = arr;
            if (await store.PutIfMatchAsync(paths.SourceMembers(sourceId), JsonSerializer.SerializeToUtf8Bytes(root), c?.ETag, ct)) return add.Count;
        }
        throw new IOException($"{sourceId}: members.json kept changing");
    }

    /// <summary>First touch of a source: the ids of an old seen.json (and of the ids the job body still carries) become members, and seen.json is deleted. Returns the ids added; null when there was nothing to migrate.</summary>
    public static async Task<int?> MigrateSeenAsync(IStore store, Paths paths, string sourceId, IEnumerable<string>? bodySeen, string now, CancellationToken ct, bool dryRun = false)
    {
        var name = paths.SourceSeen(sourceId);
        var blob = await store.GetAsync(name, ct);
        var ids = (bodySeen ?? []).ToList(); string at = now;
        if (blob != null)
        {
            ids.AddRange(JsonSerializer.Deserialize<List<string>>(blob.Data, Protocol.Json) ?? []);
            if ((await store.ListAsync(name, false, false, ct)).FirstOrDefault(b => b.Name == name)?.Created is { } cr) at = Protocol.Utc(cr);
        }
        if (ids.Count == 0 && blob == null) return null;
        int added = await AddIdsAsync(store, paths, sourceId, ids, at, ct, dryRun);
        if (!dryRun && blob != null) await store.DeleteAsync(name, ct);
        return added;
    }
}
