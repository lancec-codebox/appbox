using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace IndexNode;

/// <summary>index/groups/: v&lt;N&gt;.json, one per search index version (written only by the search-index job, or once by `indexnode groups build`), and head.yaml naming the latest. Old versions stay.</summary>
public static class GroupStore
{
    public static string File(Paths p, int version) => $"{p.Prefix}groups/v{version}.json";

    public static async Task<GroupSet?> LoadAsync(IStore store, Paths paths, int indexVersion, CancellationToken ct)
    {
        // the groups of the newest version at or below this index version (the index stamps its uploads with them)
        for (int v = indexVersion; v >= Math.Max(1, indexVersion - 3); v--)
            if (await store.GetAsync(File(paths, v), ct) is { } b && JsonNode.Parse(b.Data) is JsonObject o) return GroupSet.FromJson(o);
        return null;
    }

    public static async Task<bool> WriteAsync(IStore store, Paths paths, GroupSet g, int indexVersion, CancellationToken ct)
    {
        g.IndexVersion = indexVersion;
        var data = Encoding.UTF8.GetBytes(g.ToJson().ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        return await store.PutAsync(File(paths, indexVersion), data, null, null, true, ct, "application/json");   // create only: a second writer of the same version loses
    }
}
