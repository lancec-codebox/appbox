using System.Text.Json;

namespace IndexNode.Hosting;

public sealed record UpdateResult(string Outcome, string? Version, string? Detail)
{
    /// <summary>"updated", "current", "needs-newer-host", "failed"</summary>
    public bool Switched => Outcome == "updated";
}

/// <summary>
/// The plugin lifecycle: hash-verified manifest, a smoke-test call before switching, the last good version kept on disk, the bundled one as the last resort.
/// State: dir/good.json = {current, previous} (file names), dir/&lt;version&gt;.dll.
/// </summary>
public sealed class PluginManager
{
    public const string RollingManifestUrl = "https://github.com/lancec-codebox/appbox/releases/download/indexnode-android-plugin/manifest.json";

    private readonly string dir;
    private readonly Func<(Manifest Manifest, byte[] Dll)?> bundled;
    private readonly Version hostVersion;
    private readonly object gate = new();
    private readonly List<WeakReference> zombies = new();

    public LoadedPlugin? Current { get; private set; }
    public string? LastError { get; private set; }

    public PluginManager(string dir, Func<(Manifest, byte[])?> bundled, string hostVersion)
    {
        this.dir = dir; this.bundled = bundled; this.hostVersion = Version.Parse(hostVersion);
        Directory.CreateDirectory(dir);
    }

    private sealed record Good(string? Current, string? Previous, string? CurrentSha, string? PreviousSha);

    private string GoodPath => Path.Combine(dir, "good.json");
    private Good ReadGood() { try { return JsonSerializer.Deserialize<Good>(File.ReadAllText(GoodPath)) ?? new(null, null, null, null); } catch { return new(null, null, null, null); } }
    private void WriteGood(Good g) { var tmp = GoodPath + ".tmp"; File.WriteAllText(tmp, JsonSerializer.Serialize(g)); File.Move(tmp, GoodPath, true); }
    private string DllPath(string version) => Path.Combine(dir, "plugin-" + version + ".dll");
    private string ManifestPath(string version) => Path.Combine(dir, "plugin-" + version + ".json");

    /// <summary>The files of a kept version, or null.</summary>
    private (Manifest, byte[])? ReadKept(string? version)
    {
        if (version == null || !File.Exists(DllPath(version)) || !File.Exists(ManifestPath(version))) return null;
        return (PluginLoader.ParseManifest(File.ReadAllText(ManifestPath(version))), File.ReadAllBytes(DllPath(version)));
    }

    private static async Task SmokeAsync(LoadedPlugin p, IHost host)
    {
        string r;
        try { r = await p.Plugin!.SmokeAsync(host); }
        catch (Exception e) { throw new SmokeTestException(p.Version, e.GetType().Name + ": " + e.Message); }
        if (!string.Equals(r?.Trim(), "ok", StringComparison.OrdinalIgnoreCase)) throw new SmokeTestException(p.Version, "answered '" + r + "'");
    }

    private async Task<LoadedPlugin> TryLoadAsync((Manifest m, byte[] dll) f, IHost host, string source)
    {
        var p = PluginLoader.VerifyAndLoad(f.m, f.dll);
        p.Source = source;
        try { await SmokeAsync(p, host); }
        catch { DropQuietly(p); throw; }
        return p;
    }

    private void DropQuietly(LoadedPlugin p) { try { lock (zombies) zombies.Add(p.Unload()); } catch { } }

    /// <summary>Loads the last good plugin, else the one before it, else the bundled one. Throws when none works.</summary>
    public async Task<LoadedPlugin> EnsureLoadedAsync(IHost host)
    {
        lock (gate) { if (Current != null) return Current; }
        var errors = new List<string>();
        var g = ReadGood();
        foreach (var ver in new[] { g.Current, g.Previous })
        {
            if (ver == null) continue;
            try
            {
                if (ReadKept(ver) is not { } f) { errors.Add(ver + ": files missing"); continue; }
                var p = await TryLoadAsync(f, host, "downloaded");
                lock (gate) Current = p;
                return p;
            }
            catch (Exception e) { errors.Add(ver + ": " + e.Message); host.Log("plugin " + ver + " not usable: " + e.Message); }
        }
        if (bundled() is { } b)
        {
            try { var p = await TryLoadAsync(b, host, "bundled"); lock (gate) Current = p; return p; }
            catch (Exception e) { errors.Add("bundled " + b.Manifest.version + ": " + e.Message); }
        }
        LastError = string.Join("; ", errors);
        throw new InvalidOperationException("no usable plugin: " + LastError);
    }

    /// <summary>
    /// Fetches the manifest and, when its version differs from the loaded one, the DLL: verify the hash, load it beside the current plugin, call its smoke test,
    /// and only then switch (the old one is unloaded, kept on disk as the previous version). Any failure leaves the running plugin untouched.
    /// </summary>
    public async Task<UpdateResult> CheckAndUpdateAsync(HttpClient http, string manifestUrl, IHost host, CancellationToken ct = default)
    {
        try
        {
            var m = PluginLoader.ParseManifest(await http.GetStringAsync(manifestUrl, ct));
            if (m.minHost is { Length: > 0 } mh && Version.TryParse(mh, out var need) && need > hostVersion)
                return new("needs-newer-host", m.version, $"plugin {m.version} needs host {mh}, this is {hostVersion}");
            string? cur; lock (gate) cur = Current?.Version;
            // switch only to a newer version; a bundled plugin that is running because the downloaded ones failed may be replaced by any manifest that verifies
            bool fallback; lock (gate) fallback = Current?.Source == "bundled" && ReadGood().Current != null;
            if (cur == m.version || !fallback && cur != null && !IsNewer(m.version, cur)) return new("current", m.version, null);
            var dll = await http.GetByteArrayAsync(PluginLoader.DllUrl(manifestUrl, m), ct);
            var cand = await TryLoadAsync((m, dll), host, "downloaded");   // hash mismatch or smoke failure: nothing changes
            File.WriteAllBytes(DllPath(m.version), dll); File.WriteAllText(ManifestPath(m.version), JsonSerializer.Serialize(m));
            var g = ReadGood();
            LoadedPlugin? old;
            lock (gate) { old = Current; Current = cand; }
            if (old != null) DropQuietly(old);
            var prev = g.Current != m.version ? g.Current : g.Previous;
            WriteGood(new Good(m.version, prev, m.sha256, null));
            foreach (var f in Directory.GetFiles(dir, "plugin-*"))   // only the current and previous version stay
            {
                var n = Path.GetFileNameWithoutExtension(f)["plugin-".Length..];
                if (n != m.version && n != prev) { try { File.Delete(f); } catch { } }
            }
            host.Log($"plugin switched {cur ?? "(none)"} -> {m.version}");
            return new("updated", m.version, null);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception e)
        {
            LastError = e.Message;
            host.Log("plugin update refused: " + e.Message);
            return new("failed", null, e.Message);
        }
    }

    /// <summary>True when version a is greater than b. Versions compare as System.Version ("1" means 1.0); a version that does not parse counts as different, so it is newer.</summary>
    public static bool IsNewer(string a, string b)
    {
        static Version? V(string s) => Version.TryParse(s.Contains('.') ? s : s + ".0", out var v) ? v : null;
        return V(a) is { } va && V(b) is { } vb ? va > vb : a != b;
    }

    /// <summary>Unloads the current plugin (the next EnsureLoaded loads it again). True when its context was collected.</summary>
    public bool Unload()
    {
        LoadedPlugin? p; lock (gate) { p = Current; Current = null; }
        if (p == null) return false;
        var wr = DropAndUnload(p);
        for (int i = 0; i < 20 && wr.IsAlive; i++) { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); if (wr.IsAlive) Thread.Sleep(50); }
        return !wr.IsAlive;
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static WeakReference DropAndUnload(LoadedPlugin p) => p.Unload();
}
