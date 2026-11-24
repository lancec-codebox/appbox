using Xunit;

namespace IndexNode.Tests;

/// <summary>
/// 0.13.8 guard: the ghost-aware reader is RestStore (every wrapper forwards to it). Nothing else may read blob bytes from Azure: no HttpClient or Azure SDK blob client outside the storage layer,
/// and none of the old per-call-site ghost fixes (snapshot bookkeeping, ghost hydration) outside it either. A new reader that bypassed the retriever would fail here.
/// </summary>
public class OneReaderGuardTests
{
    static string Root()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !Directory.Exists(Path.Combine(d.FullName, "IndexNode.Core"))) d = d.Parent;
        return d?.FullName ?? throw new InvalidOperationException("repo root not found");
    }

    static IEnumerable<(string File, int Line, string Text)> Lines(params string[] dirs) =>
        dirs.SelectMany(dir => Directory.EnumerateFiles(Path.Combine(Root(), dir), "*.cs", SearchOption.AllDirectories))
            .Where(f => !f.Contains("/obj/") && !f.Contains("/bin/") && !f.Contains("\\obj\\") && !f.Contains("\\bin\\"))
            .SelectMany(f => File.ReadLines(f).Select((t, i) => (f, i + 1, t)));

    static bool Retriever(string f) => f.Replace('\\', '/').EndsWith("/Storage/RestStore.cs") || f.Replace('\\', '/').EndsWith("/Storage/NexisAccess.cs") || f.Replace('\\', '/').EndsWith("/Storage/NexisGhost.cs");

    [Fact]
    public void Only_the_retriever_talks_to_blob_storage_for_reads()
    {
        var bad = Lines("IndexNode", "IndexNode.Core").Where(l => !Retriever(l.File) && !l.Text.TrimStart().StartsWith("//") && !l.Text.TrimStart().StartsWith("///"))
            .Where(l => System.Text.RegularExpressions.Regex.IsMatch(l.Text, @"\bnew\s+(BlobClient|BlockBlobClient|BlobBaseClient|BlobContainerClient)\b|\.BlobUri\(|DownloadStreamingAsync|\.OpenReadAsync\("))
            .Select(l => $"{Path.GetFileName(l.File)}:{l.Line}: {l.Text.Trim()}").ToList();
        Assert.True(bad.Count == 0, "blob reads outside the one ghost-aware reader:\n" + string.Join("\n", bad));
    }

    [Fact]
    public void The_per_call_site_ghost_fixes_are_folded_into_the_retriever()
    {
        // snapshot bookkeeping and ghost hydration belong to the retriever alone; callers only ask the store to read
        var bad = Lines("IndexNode", "IndexNode.Core").Where(l => !Retriever(l.File) && !l.Text.TrimStart().StartsWith("//"))
            .Where(l => l.Text.Contains("ReadSnapshots") || l.Text.Contains("GhostBlobFor") || l.Text.Contains("EnsureReadableAsync") || l.Text.Contains("HydrateGhost") || l.Text.Contains("CommitGhostAsync") && !l.File.EndsWith("NexisAccess.cs"))
            .Select(l => $"{Path.GetFileName(l.File)}:{l.Line}: {l.Text.Trim()}").ToList();
        Assert.True(bad.Count == 0, "ghost handling outside the retriever:\n" + string.Join("\n", bad));
    }

    [Fact]
    public void RestStore_is_the_only_IStore_that_sends_http_requests()
    {
        var bad = Lines("IndexNode", "IndexNode.Core").Where(l => !l.File.Replace('\\', '/').EndsWith("/Storage/RestStore.cs") && !l.File.Replace('\\', '/').EndsWith("/Storage/MediaTable.cs") /* 0.13.37: the table client is an IMediaTable, not an IStore */ && l.Text.Contains("http.SendAsync") && l.File.Replace('\\', '/').Contains("/Storage/"))
            .Select(l => $"{Path.GetFileName(l.File)}:{l.Line}").ToList();
        Assert.True(bad.Count == 0, "another store sends HTTP: " + string.Join(", ", bad));
    }
}
