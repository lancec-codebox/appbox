using System.Text.Json;
using System.Text.RegularExpressions;

namespace IndexNode;

public enum SourceState { Ok, SignedOut }

/// <summary>Reads the owner's Watch Later list through the dedicated browser profile. Cookie values are never read.</summary>
public static partial class WatchLater
{
    public const string ListUrl = "https://www.youtube.com/playlist?list=WL";
    public const string LibraryUrl = "https://www.youtube.com/feed/library";

    public const string SignedInExpression = "(function(){ try { return !!(window.ytcfg && ytcfg.get && ytcfg.get('LOGGED_IN') === true); } catch (e) { return false; } })()";
    public const string InitialDataExpression = "JSON.stringify(window.ytInitialData || null)";
    // scrolls to the bottom, gives the next continuation 1.5 s to load, and returns the watch links rendered so far
    public const string ScrollExpression = "(async function(){ window.scrollTo(0, document.documentElement.scrollHeight); await new Promise(r => setTimeout(r, 1500)); return JSON.stringify(Array.from(document.querySelectorAll('ytd-playlist-video-renderer a[href*=\"/watch?v=\"]')).map(a => a.href)); })()";

    [GeneratedRegex(@"^[A-Za-z0-9_-]{11}$")] private static partial Regex IdRegex();
    [GeneratedRegex(@"[?&]v=([A-Za-z0-9_-]{11})(?![A-Za-z0-9_-])")] private static partial Regex HrefRegex();

    public static string? IdFromHref(string href) => HrefRegex().Match(href) is { Success: true } m ? m.Groups[1].Value : null;

    /// <summary>
    /// Video ids, in order and without duplicates, from ytInitialData or a continuation response: every playlistVideoRenderer
    /// (and, in the newer layout, video lockupViewModel) anywhere in the tree. continuationItemRenderer entries are ignored.
    /// </summary>
    public static List<string> ParseIds(string json)
    {
        var ids = new List<string>(); var seen = new HashSet<string>();
        if (string.IsNullOrWhiteSpace(json) || json == "null") return ids;
        using var doc = JsonDocument.Parse(json);
        void Add(string? id) { if (id != null && IdRegex().IsMatch(id) && seen.Add(id)) ids.Add(id); }
        void Walk(JsonElement e)
        {
            switch (e.ValueKind)
            {
                case JsonValueKind.Object:
                    foreach (var p in e.EnumerateObject())
                    {
                        if (p.Name == "playlistVideoRenderer" && p.Value.ValueKind == JsonValueKind.Object && p.Value.TryGetProperty("videoId", out var v)) Add(v.GetString());
                        else if (p.Name == "lockupViewModel" && p.Value.ValueKind == JsonValueKind.Object && p.Value.TryGetProperty("contentId", out var cid)
                            && p.Value.TryGetProperty("contentType", out var ct) && (ct.GetString() ?? "").Contains("VIDEO")) Add(cid.GetString());
                        Walk(p.Value);
                    }
                    break;
                case JsonValueKind.Array:
                    foreach (var x in e.EnumerateArray()) Walk(x);
                    break;
            }
        }
        Walk(doc.RootElement);
        return ids;
    }

    public static async Task<bool> IsSignedInAsync(IPage page, CancellationToken ct)
    {
        await page.NavigateAsync(LibraryUrl, ct);
        var v = await page.EvaluateAsync(SignedInExpression, ct);
        return v.ValueKind == JsonValueKind.True;
    }

    /// <summary>The first page comes from ytInitialData; then the page is scrolled until three rounds in a row bring no new id.</summary>
    public static async Task<List<string>> ReadIdsAsync(IPage page, CancellationToken ct, int maxRounds = 500, int quietRounds = 3)
    {
        await page.NavigateAsync(ListUrl, ct);
        var ids = new List<string>(); var seen = new HashSet<string>();
        void Merge(IEnumerable<string> more) { foreach (var i in more) if (seen.Add(i)) ids.Add(i); }
        var init = await page.EvaluateAsync(InitialDataExpression, ct);
        if (init.ValueKind == JsonValueKind.String) Merge(ParseIds(init.GetString()!));
        int quiet = 0;
        for (int round = 0; round < maxRounds && quiet < quietRounds; round++)
        {
            ct.ThrowIfCancellationRequested();
            var before = ids.Count;
            var links = await page.EvaluateAsync(ScrollExpression, ct);
            if (links.ValueKind == JsonValueKind.String)
                using (var doc = JsonDocument.Parse(links.GetString()!))
                    Merge(doc.RootElement.EnumerateArray().Select(h => IdFromHref(h.GetString() ?? "")).Where(i => i != null).Select(i => i!).ToList());
            quiet = ids.Count == before ? quiet + 1 : 0;
        }
        return ids;
    }

    public sealed record Counts(int Found, int New, int Existing);

    /// <summary>Adds a download job for every id that has no job yet (in any state).</summary>
    public static async Task<Counts> EnqueueAsync(IStore store, Paths paths, IEnumerable<string> ids, int maxHeight, CancellationToken ct)
    {
        int found = 0, added = 0;
        foreach (var id in ids.Distinct())
        {
            found++;
            var job = JobDescriptor.NewDownload("https://www.youtube.com/watch?v=" + id, maxHeight, null);
            if (await store.PutAsync(paths.Job("download", job.JobId), job.ToBytes(), Tags.Job("download", "pending", job.AddedAt), null, true, ct)) added++;
        }
        return new Counts(found, added, found - added);
    }

    /// <summary>One poll of the source: signed-out profiles are skipped (the caller logs the hint).</summary>
    public static async Task<(SourceState State, Counts? Counts)> PollAsync(BrowserConfig cfg, HttpClient http, IStore store, Paths paths, int maxHeight, CancellationToken ct)
    {
        await using var browser = await CdpBrowser.OpenAsync(cfg, http, ct);
        var page = await browser.NewPageAsync(ct);
        try
        {
            if (!await IsSignedInAsync(page, ct)) return (SourceState.SignedOut, null);
            var ids = await ReadIdsAsync(page, ct);
            await page.CloseAsync();
            return (SourceState.Ok, await EnqueueAsync(store, paths, ids, maxHeight, ct));
        }
        finally { await page.CloseAsync(); }
    }

    public const string SignedOutMessage = "Watch Later: profile not signed in, run `indexnode browser login`";

    /// <summary>"6h", "30m", "1d", "90s".</summary>
    public static TimeSpan ParseInterval(string s)
    {
        var m = Regex.Match(s.Trim(), @"^(\d+)\s*([smhd])$", RegexOptions.IgnoreCase);
        if (!m.Success) throw new ArgumentException($"bad interval '{s}' (use e.g. 6h, 30m, 1d)");
        var n = int.Parse(m.Groups[1].Value);
        return char.ToLowerInvariant(m.Groups[2].Value[0]) switch { 's' => TimeSpan.FromSeconds(n), 'm' => TimeSpan.FromMinutes(n), 'h' => TimeSpan.FromHours(n), _ => TimeSpan.FromDays(n) };
    }
}
