using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text.Json;
using Spike.Contract;

namespace Spike.Loader;

public sealed record Manifest(string version, string sha256, string entry);

public sealed class HashMismatchException(string expected, string actual)
    : Exception($"sha256 mismatch: manifest {expected}, downloaded {actual}");

/// <summary>A collectible context holding one loaded plugin.</summary>
public sealed class LoadedPlugin
{
    private sealed class Ctx(string name) : AssemblyLoadContext(name, isCollectible: true) { }

    private Ctx? _ctx;
    public IPlugin? Plugin { get; private set; }
    public string Version { get; }
    public long LoadMs { get; }

    internal LoadedPlugin(Manifest m, byte[] dll)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        _ctx = new Ctx("plugin-" + m.version);
        using var ms = new MemoryStream(dll, writable: false);
        var asm = _ctx.LoadFromStream(ms);   // Spike.Contract resolves from the default context
        var type = asm.GetTypes().FirstOrDefault(t => typeof(IPlugin).IsAssignableFrom(t) && !t.IsAbstract
                       && (string.IsNullOrEmpty(m.entry) || t.FullName == m.entry))
            ?? throw new InvalidOperationException("no IPlugin type" + (m.entry is { Length: > 0 } ? " named " + m.entry : ""));
        Plugin = (IPlugin)Activator.CreateInstance(type)!;
        Version = m.version;
        LoadMs = sw.ElapsedMilliseconds;
    }

    /// <summary>Drops every reference and unloads. Returns a weak reference to watch collection.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public WeakReference Unload()
    {
        var wr = new WeakReference(_ctx, trackResurrection: true);
        Plugin = null;
        _ctx!.Unload();
        _ctx = null;
        return wr;
    }
}

public static class PluginLoader
{
    public static string Sha256Hex(byte[] data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    public static Manifest ParseManifest(string json) =>
        JsonSerializer.Deserialize<Manifest>(json) ?? throw new InvalidOperationException("empty manifest");

    /// <summary>Verifies the DLL against the manifest hash and only then loads it. Nothing is loaded on mismatch.</summary>
    public static LoadedPlugin VerifyAndLoad(Manifest manifest, byte[] dll)
    {
        var actual = Sha256Hex(dll);
        if (!string.Equals(actual, manifest.sha256.Trim(), StringComparison.OrdinalIgnoreCase))
            throw new HashMismatchException(manifest.sha256, actual);
        return new LoadedPlugin(manifest, dll);
    }

    /// <summary>Downloads manifest then the DLL next to it (manifest "dll" name defaults to the manifest URL with .json -> .dll).</summary>
    public static async Task<(Manifest, byte[])> FetchAsync(HttpClient http, string manifestUrl, string? dllUrl = null)
    {
        var m = ParseManifest(await http.GetStringAsync(manifestUrl));
        dllUrl ??= manifestUrl.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
            ? manifestUrl[..^5] + ".dll" : manifestUrl + ".dll";
        return (m, await http.GetByteArrayAsync(dllUrl));
    }
}
