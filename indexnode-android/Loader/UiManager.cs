using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace IndexNode.Hosting;

/// <summary>ui.json on the rolling release: the page's version, the SHA-256 of the page file, its file name beside the manifest, and the oldest host it works on.</summary>
public sealed record UiManifest(string version, string sha256, string page, string? minHost);

/// <summary>
/// 0.1.8: the page (the WebView UI) updates like the plugin, so a new screen does not need a new APK (owner, 2026-11-25: "We seem to be changing the outer app a lot").
/// CI publishes the page of main as ui.html with ui.json on the rolling release whenever its version (the page's own meta tag "ui-version") is new.
/// The app fetches the manifest on start and from Settings; a page that is newer than both the one in the APK and the one it has, whose hash matches and whose own meta tags agree with the manifest,
/// is kept in <see cref="WwwDir"/> (served to the WebView under /ui/). The version before it stays on disk. A downloaded page that does not come alive (no bridge call soon after it loads)
/// is marked bad and never loaded again; the page in the APK is always the last resort. State: dir/state.json = {current, previous, currentSha, previousSha, bad[]}.
/// </summary>
public sealed class UiManager
{
    public const string RollingManifestUrl = "https://github.com/lancec-codebox/appbox/releases/download/indexnode-android-plugin/ui.json";
    public const int MaxPageBytes = 4 * 1024 * 1024;

    readonly string dir; readonly Version host; readonly object gate = new();
    /// <summary>The version of the page inside the APK (its meta tag), "0" when it has none.</summary>
    public string Bundled { get; }
    public string? LastError { get; private set; }
    /// <summary>The folder the WebView serves under /ui/.</summary>
    public string WwwDir => Path.Combine(dir, "www");

    public UiManager(string dir, string? bundledVersion, string hostVersion)
    {
        this.dir = dir; Bundled = string.IsNullOrWhiteSpace(bundledVersion) ? "0" : bundledVersion.Trim(); host = Version.Parse(hostVersion);
        Directory.CreateDirectory(WwwDir);
    }

    sealed record State(string? Current, string? Previous, string? CurrentSha, string? PreviousSha, string[]? Bad);
    string StatePath => Path.Combine(dir, "state.json");
    State Read() { try { return JsonSerializer.Deserialize<State>(File.ReadAllText(StatePath)) ?? new(null, null, null, null, null); } catch { return new(null, null, null, null, null); } }
    void Write(State s) { var tmp = StatePath + ".tmp"; File.WriteAllText(tmp, JsonSerializer.Serialize(s)); File.Move(tmp, StatePath, true); }
    public static string FileOf(string version) => "page-" + Regex.Replace(version, "[^0-9A-Za-z.]", "_") + ".html";
    static string Sha(byte[] b) => Convert.ToHexString(SHA256.HashData(b)).ToLowerInvariant();

    /// <summary>The content of a meta tag of the page (ui-version, ui-min-host), or null.</summary>
    public static string? Meta(string html, string name) =>
        Regex.Match(html, "<meta\\s+name=\"" + Regex.Escape(name) + "\"\\s+content=\"([^\"]*)\"", RegexOptions.IgnoreCase) is { Success: true } m ? m.Groups[1].Value.Trim() : null;

    public static UiManifest ParseManifest(string json)
    {
        var m = JsonSerializer.Deserialize<UiManifest>(json) ?? throw new InvalidDataException("ui.json is empty");
        if (string.IsNullOrWhiteSpace(m.version) || !Regex.IsMatch(m.sha256 ?? "", "^[0-9a-fA-F]{64}$") || !Regex.IsMatch(m.page ?? "", @"^[A-Za-z0-9._-]{1,80}$")) throw new InvalidDataException("ui.json: version, sha256 (64 hex) and page (a plain file name) are required");
        return m;
    }

    /// <summary>The downloaded page to load, as (version, file name inside <see cref="WwwDir"/>): the current one, else the one before it; each only while it is newer than the page in the APK,
    /// not marked bad, on disk, and still the bytes that were verified. Null: load the page in the APK.</summary>
    public (string Version, string File)? PageToLoad()
    {
        State s; lock (gate) s = Read();
        foreach (var (v, sha) in new[] { (s.Current, s.CurrentSha), (s.Previous, s.PreviousSha) })
        {
            if (v == null || sha == null || (s.Bad ?? []).Contains(v) || !PluginManager.IsNewer(v, Bundled)) continue;
            var f = Path.Combine(WwwDir, FileOf(v));
            try { if (File.Exists(f) && Sha(File.ReadAllBytes(f)) == sha) return (v, FileOf(v)); } catch (IOException) { }
        }
        return null;
    }

    /// <summary>The version of the page that <see cref="PageToLoad"/> gives, or the bundled one, and where it is from.</summary>
    public (string Version, string Source) Active => PageToLoad() is { } p ? (p.Version, "downloaded") : (Bundled, "bundled");

    /// <summary>
    /// Fetches the manifest and, when it names a page newer than the active one, the page: hash, size and the page's own meta tags are checked before anything is kept.
    /// Outcomes as the plugin's: "updated" (load it now), "current", "needs-newer-host", "failed" (nothing changed).
    /// </summary>
    public async Task<UpdateResult> CheckAsync(Func<string, CancellationToken, Task<byte[]>> get, string manifestUrl, CancellationToken ct = default)
    {
        try
        {
            var m = ParseManifest(Encoding.UTF8.GetString(await get(manifestUrl, ct)));
            if (m.minHost is { Length: > 0 } mh && Version.TryParse(mh, out var need) && need > host) return new("needs-newer-host", m.version, $"page {m.version} needs app {mh}, this is {host}");
            State s; lock (gate) s = Read();
            if ((s.Bad ?? []).Contains(m.version)) return new("failed", m.version, $"page {m.version} did not work on this phone before; waiting for a newer one");
            var active = Active.Version;
            if (m.version == active || !PluginManager.IsNewer(m.version, active)) return new("current", active, null);
            var bytes = await get(new Uri(new Uri(manifestUrl), m.page).ToString(), ct);
            if (bytes.Length == 0 || bytes.Length > MaxPageBytes) throw new InvalidDataException($"the page is {bytes.Length} bytes");
            if (!string.Equals(Sha(bytes), m.sha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("the page's hash is not the manifest's");
            var html = Encoding.UTF8.GetString(bytes);
            if (!html.TrimStart().StartsWith("<!doctype html", StringComparison.OrdinalIgnoreCase) || Meta(html, "ui-version") != m.version) throw new InvalidDataException("the file is not the page the manifest describes (its ui-version tag differs)");
            lock (gate)
            {
                File.WriteAllBytes(Path.Combine(WwwDir, FileOf(m.version)), bytes);
                s = Read(); var keep = PageToLoad();   // the page in use until now becomes the one before
                Write(new State(m.version, keep?.Version, m.sha256.ToLowerInvariant(), keep == null ? null : keep.Value.Version == s.Current ? s.CurrentSha : s.PreviousSha, s.Bad));
                foreach (var f in Directory.GetFiles(WwwDir, "page-*.html")) { var n = Path.GetFileName(f); if (n != FileOf(m.version) && (keep == null || n != keep.Value.File)) { try { File.Delete(f); } catch { } } }
            }
            LastError = null;
            return new("updated", m.version, null);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception e) { LastError = e.Message; return new("failed", null, e.Message); }
    }

    /// <summary>A downloaded page that loaded but never called the app: it is not loaded again (its file goes), and the one before it, or the page in the APK, is used.</summary>
    public void MarkBad(string version)
    {
        lock (gate)
        {
            var s = Read(); var bad = (s.Bad ?? []).Append(version).Distinct().TakeLast(20).ToArray();
            Write(s.Current == version ? new State(s.Previous, null, s.PreviousSha, null, bad) : s.Previous == version ? s with { Previous = null, PreviousSha = null, Bad = bad } : s with { Bad = bad });
            try { File.Delete(Path.Combine(WwwDir, FileOf(version))); } catch { }
        }
    }
}
