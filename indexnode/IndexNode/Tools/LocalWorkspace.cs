namespace IndexNode;

/// <summary>A tool workspace on the local disk: a fresh folder under the temp directory, deleted when disposed.</summary>
public sealed class LocalWorkspace : IToolWorkspace
{
    public string Dir { get; }
    public string Name { get; }

    public LocalWorkspace(string name)
    {
        Name = name;
        Dir = Path.Combine(Path.GetTempPath(), "indexnode", name);
        if (Directory.Exists(Dir)) Directory.Delete(Dir, true);
        Directory.CreateDirectory(Dir);
    }

    private string P(string path) => Path.Combine(Dir, path);

    public Task<bool> ExistsAsync(string path, CancellationToken ct) => Task.FromResult(File.Exists(P(path)));

    public Task<IReadOnlyList<string>> ListAsync(string dir, string glob, CancellationToken ct)
    {
        var d = P(dir);
        IReadOnlyList<string> r = Directory.Exists(d) ? Directory.GetFiles(d, glob).Select(f => Path.GetRelativePath(Dir, f).Replace('\\', '/')).ToList() : [];
        return Task.FromResult(r);
    }

    public Task<string> ReadTextAsync(string path, CancellationToken ct) => File.ReadAllTextAsync(P(path), ct);
    public Task WriteTextAsync(string path, string text, CancellationToken ct) => File.WriteAllTextAsync(P(path), text, ct);
    public Task<long> TotalBytesAsync(CancellationToken ct) => Task.FromResult(Directory.EnumerateFiles(Dir, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length));
    public Task<long> SizeAsync(string path, CancellationToken ct) => Task.FromResult(new FileInfo(P(path)).Length);
    public Task<string> LocalPathAsync(string path, CancellationToken ct) => Task.FromResult(P(path));

    public ValueTask DisposeAsync()
    {
        try { Directory.Delete(Dir, true); } catch { }
        return ValueTask.CompletedTask;
    }
}
