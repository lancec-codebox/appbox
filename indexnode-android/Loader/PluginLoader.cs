using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace IndexNode.Hosting;

/// <summary>The rolling-release manifest: {version, sha256, dll, minHost}. "dll" is a file name next to the manifest (or a full URL); "entry" optionally names the IPlugin type.</summary>
public sealed record Manifest(
    [property: JsonPropertyName("version")] string version,
    [property: JsonPropertyName("sha256")] string sha256,
    [property: JsonPropertyName("dll")] string? dll = null,
    [property: JsonPropertyName("minHost")] string? minHost = null,
    [property: JsonPropertyName("entry")] string? entry = null);

public sealed class HashMismatchException(string expected, string actual)
    : Exception($"sha256 mismatch: manifest {expected}, downloaded {actual}");

public sealed class SmokeTestException(string version, string why) : Exception($"plugin {version} failed its smoke test: {why}");

/// <summary>A collectible context holding one loaded plugin.</summary>
public sealed class LoadedPlugin
{
    private sealed class Ctx(string name) : AssemblyLoadContext(name, isCollectible: true) { }

    private Ctx? _ctx;
    private Func<AssemblyLoadContext, AssemblyName, Assembly?>? resolver;
    public IPlugin? Plugin { get; private set; }
    public string Version { get; }
    public string Source { get; internal set; } = "downloaded";
    public long LoadMs { get; }

    internal LoadedPlugin(Manifest m, byte[] dll)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        _ctx = new Ctx("plugin-" + m.version);
        try
        {
            using var ms = new MemoryStream(dll, writable: false);
            var asm = _ctx.LoadFromStream(ms);   // IndexNode.Contract resolves from the default context: the host's types are shared
            // everything else the plugin needs (IndexNode.Core ...) travels inside the plugin DLL as embedded resources "asm.<Name>.dll": one file, one hash
            resolver = (c, name) =>
            {
                using var rs = asm.GetManifestResourceStream("asm." + name.Name + ".dll");
                return rs == null ? null : c.LoadFromStream(rs);
            };
            _ctx.Resolving += resolver;
            var type = asm.GetTypes().FirstOrDefault(t => typeof(IPlugin).IsAssignableFrom(t) && !t.IsAbstract
                           && (string.IsNullOrEmpty(m.entry) || t.FullName == m.entry))
                ?? throw new InvalidOperationException("no IPlugin type" + (m.entry is { Length: > 0 } ? " named " + m.entry : ""));
            Plugin = (IPlugin)Activator.CreateInstance(type)!;
        }
        catch { Unload(); throw; }
        Version = m.version;
        LoadMs = sw.ElapsedMilliseconds;
    }

    /// <summary>Drops every reference and unloads. Returns a weak reference to watch collection.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public WeakReference Unload()
    {
        var wr = new WeakReference(_ctx, trackResurrection: true);
        Plugin = null;
        if (_ctx != null && resolver != null) _ctx.Resolving -= resolver;
        resolver = null;
        _ctx?.Unload();
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

    public static string DllUrl(string manifestUrl, Manifest m)
    {
        if (!string.IsNullOrEmpty(m.dll))
            return Uri.TryCreate(m.dll, UriKind.Absolute, out var abs) ? abs.ToString() : new Uri(new Uri(manifestUrl), m.dll).ToString();
        return manifestUrl.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? manifestUrl[..^5] + ".dll" : manifestUrl + ".dll";
    }

    public static async Task<(Manifest, byte[])> FetchAsync(HttpClient http, string manifestUrl, CancellationToken ct = default)
    {
        var m = ParseManifest(await http.GetStringAsync(manifestUrl, ct));
        return (m, await http.GetByteArrayAsync(DllUrl(manifestUrl, m), ct));
    }
}
