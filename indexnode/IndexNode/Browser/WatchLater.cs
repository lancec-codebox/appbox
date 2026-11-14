using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace IndexNode;

public enum SourceState { Ok, SignedOut }

/// <summary>One cookie as the browser reports it (Storage.getCookies). The value is a secret: never log it.</summary>
public sealed record BrowserCookie(string Name, string Value, string Domain, string Path, double Expires, bool HttpOnly, bool Secure);

public sealed record SeedVideo(string Id, string? Title, string? ChannelId, string? ChannelName);
public sealed record SeedChannel(string ChannelId, string? Name, int Count);

/// <summary>
/// Watch Later is only a source of seeds (channels worth following); it never creates download jobs. The browser connection is used
/// only to get the cookies of the dedicated profile; yt-dlp reads the playlist with them from a private, short-lived cookie file.
/// </summary>
public static class WatchLater
{
    public const string ListUrl = "https://www.youtube.com/playlist?list=WL";
    public const string LibraryUrl = "https://www.youtube.com/feed/library";
    public const string SignedInExpression = "(function(){ try { return !!(window.ytcfg && ytcfg.get && ytcfg.get('LOGGED_IN') === true); } catch (e) { return false; } })()";
    public const string SignedOutMessage = "Watch Later: profile not signed in, run `indexnode browser login`";

    /// <summary>`browser status`: asks the library page whether YouTube considers the profile signed in.</summary>
    public static async Task<bool> IsSignedInAsync(IPage page, CancellationToken ct)
    {
        await page.NavigateAsync(LibraryUrl, ct);
        var v = await page.EvaluateAsync(SignedInExpression, ct);
        return v.ValueKind == JsonValueKind.True;
    }

    private static bool IsGoogleDomain(string d) => d.TrimStart('.').EndsWith("youtube.com", StringComparison.OrdinalIgnoreCase) || d.TrimStart('.').EndsWith("google.com", StringComparison.OrdinalIgnoreCase);

    /// <summary>The cookies yt-dlp needs for YouTube (youtube.com and google.com only).</summary>
    public static List<BrowserCookie> ForYouTube(IEnumerable<BrowserCookie> all) => all.Where(c => IsGoogleDomain(c.Domain)).ToList();

    /// <summary>A signed-in profile has Google's session cookies; no navigation needed.</summary>
    public static bool LooksSignedIn(IEnumerable<BrowserCookie> cookies) =>
        cookies.Any(c => IsGoogleDomain(c.Domain) && c.Name is "SAPISID" or "__Secure-3PSID" or "__Secure-1PSID" or "SID" && c.Value.Length > 0);

    /// <summary>Netscape cookie-file text for yt-dlp (<c>#HttpOnly_</c> prefix for HttpOnly cookies; 0 expiry for session cookies).</summary>
    public static string ToNetscape(IEnumerable<BrowserCookie> cookies)
    {
        var sb = new StringBuilder("# Netscape HTTP Cookie File\n");
        foreach (var c in cookies)
        {
            if (c.Name.AsSpan().IndexOfAny('\t', '\n', '\r') >= 0 || c.Value.AsSpan().IndexOfAny('\t', '\n', '\r') >= 0) continue;
            var expires = c.Expires > 0 ? ((long)c.Expires).ToString(CultureInfo.InvariantCulture) : "0";
            sb.Append(c.HttpOnly ? "#HttpOnly_" : "").Append(c.Domain).Append('\t')
              .Append(c.Domain.StartsWith('.') ? "TRUE" : "FALSE").Append('\t')
              .Append(string.IsNullOrEmpty(c.Path) ? "/" : c.Path).Append('\t')
              .Append(c.Secure ? "TRUE" : "FALSE").Append('\t').Append(expires).Append('\t')
              .Append(c.Name).Append('\t').Append(c.Value).Append('\n');
        }
        return sb.ToString();
    }

    /// <summary>Videos from yt-dlp's --flat-playlist -J output; entries without an id are dropped, duplicates kept once.</summary>
    public static List<SeedVideo> ParsePlaylist(string json)
    {
        var list = new List<SeedVideo>(); var seen = new HashSet<string>();
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("entries", out var entries) || entries.ValueKind != JsonValueKind.Array) return list;
        static string? S(JsonElement e, string n) => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } s ? s : null;
        foreach (var e in entries.EnumerateArray())
        {
            if (e.ValueKind != JsonValueKind.Object || S(e, "id") is not { } id || !seen.Add(id)) continue;
            list.Add(new SeedVideo(id, S(e, "title"), S(e, "channel_id") ?? S(e, "uploader_id"), S(e, "channel") ?? S(e, "uploader")));
        }
        return list;
    }

    /// <summary>Channels with the number of seed videos, most videos first (then by name, so the order is stable).</summary>
    public static List<SeedChannel> CountChannels(IEnumerable<SeedVideo> videos) =>
        videos.Where(v => v.ChannelId != null).GroupBy(v => v.ChannelId!)
            .Select(g => new SeedChannel(g.Key, g.Select(v => v.ChannelName).FirstOrDefault(n => n != null), g.Count()))
            .OrderByDescending(c => c.Count).ThenBy(c => c.Name, StringComparer.Ordinal).ThenBy(c => c.ChannelId, StringComparer.Ordinal).ToList();

    public sealed record SeedResult(SourceState State, int Videos, int Channels);

    private static readonly Regex SignInHint = new(@"(?i)sign in|log ?in|cookies.*(no longer valid|expired)|playlist does not exist|private|This playlist type is unviewable");

    /// <summary>
    /// Gets the cookies, writes them to a Netscape file in a private temp folder, runs yt-dlp on the playlist, deletes the file in a
    /// finally, and stores the two seed files. Nothing is enqueued.
    /// </summary>
    public static async Task<SeedResult> WriteSeedsAsync(IStore store, Paths paths, Func<CancellationToken, Task<IReadOnlyList<BrowserCookie>>> getCookies,
        Func<IReadOnlyList<string>, CancellationToken, Task<ProcResult>> ytdlp, DateTimeOffset now, CancellationToken ct)
    {
        var cookies = ForYouTube(await getCookies(ct));
        if (!LooksSignedIn(cookies)) return new SeedResult(SourceState.SignedOut, 0, 0);
        foreach (var c in cookies) Redact.Register(c.Value);
        string? dir = null;
        try
        {
            dir = PrivateTempDir();
            var file = Path.Combine(dir, "cookies.txt");
            WritePrivate(file, ToNetscape(cookies));
            var r = await ytdlp(["--flat-playlist", "-J", "--no-warnings", "--cookies", file, ListUrl], ct);
            if (r.ExitCode != 0)
            {
                if (SignInHint.IsMatch(r.Stderr)) return new SeedResult(SourceState.SignedOut, 0, 0);
                YtDlp.Throw(r.Stderr);
            }
            var videos = ParsePlaylist(r.Stdout);
            var channels = CountChannels(videos);
            var day = now.UtcDateTime.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
            var at = Protocol.Utc(now);
            await store.PutAsync(paths.Seed($"watch-later-{day}.json"), JsonSerializer.SerializeToUtf8Bytes(new
            {
                v = 1, at, count = videos.Count,
                videos = videos.Select(v => new { id = v.Id, title = v.Title, channelId = v.ChannelId, channelName = v.ChannelName }),
            }), null, null, false, ct, "application/json");
            await store.PutAsync(paths.Seed($"channels-{day}.json"), JsonSerializer.SerializeToUtf8Bytes(new
            {
                v = 1, at, count = channels.Count,
                channels = channels.Select(c => new { channelId = c.ChannelId, name = c.Name, count = c.Count }),
            }), null, null, false, ct, "application/json");
            return new SeedResult(SourceState.Ok, videos.Count, channels.Count);
        }
        finally
        {
            if (dir != null) { try { Directory.Delete(dir, true); } catch { } }
            Redact.ClearKnown();
        }
    }

    private static string PrivateTempDir()
    {
        var dir = Directory.CreateTempSubdirectory("indexnode-cookies-");
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(dir.FullName, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return dir.FullName;
    }

    private static void WritePrivate(string path, string text)
    {
        var o = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows()) o.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        using var fs = new FileStream(path, o);
        fs.Write(new UTF8Encoding(false).GetBytes(text));
    }

    /// <summary>The real thing: cookies from the dedicated profile over CDP, yt-dlp through the tools.</summary>
    public static async Task<SeedResult> RunAsync(BrowserConfig cfg, HttpClient http, Tools tools, IStore store, Paths paths, CancellationToken ct)
    {
        return await WriteSeedsAsync(store, paths,
            async t => { await using var b = await CdpBrowser.OpenAsync(cfg, http, t); return await b.GetCookiesAsync(t); },
            (args, t) => tools.RunYtDlpAsync(args, t), DateTimeOffset.UtcNow, ct);
    }
}
