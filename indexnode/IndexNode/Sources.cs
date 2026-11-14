using System.Text.Json;
using System.Text.RegularExpressions;

namespace IndexNode;

/// <summary>indexnode sources add | list | remove | import: followed playlists and channels, stored as watch jobs.</summary>
public static partial class Sources
{
    [GeneratedRegex(@"[?&]list=([A-Za-z0-9_-]+)")] private static partial Regex ListParam();
    [GeneratedRegex(@"youtube\.com/channel/(UC[A-Za-z0-9_-]{22})")] private static partial Regex ChannelPath();

    public static string[] ParseTabs(string? s) =>
        string.IsNullOrWhiteSpace(s) ? ["videos", "shorts"] : s.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(t =>
            t is "videos" or "shorts" ? t : throw new ArgumentException($"unknown tab '{t}' (videos, shorts)")).ToArray();

    /// <summary>(kind, id) when the URL names one outright; null when yt-dlp has to resolve it (a @handle).</summary>
    public static (string Kind, string Id)? Classify(string url)
    {
        var l = ListParam().Match(url);
        if (l.Success)
        {
            if (l.Groups[1].Value is "WL" or "LL") throw new ArgumentException("Watch Later is never a source; seed channels from it with: indexnode seeds watch-later");
            return ("playlist", l.Groups[1].Value);
        }
        var c = ChannelPath().Match(url);
        return c.Success ? ("channel", c.Groups[1].Value) : null;
    }

    public static async Task<JobDescriptor> ResolveAsync(Tools tools, string url, string every, string[] tabs, bool follow, int backfill, int maxHeight, CancellationToken ct)
    {
        WatchStage.ParseEvery(every);
        var known = Classify(url);
        var r = await tools.RunYtDlpAsync(["--flat-playlist", "--no-warnings", "--playlist-end", "1", "--print", "%(playlist_title)s\t%(playlist_channel_id)s\t%(channel_id)s\t%(channel)s", known?.Kind == "channel" ? $"https://www.youtube.com/channel/{known.Value.Id}/videos" : url], ct);
        string[] p = r.ExitCode == 0 ? (r.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "").Split('\t') : [];
        string? F(int i) => p.Length > i && p[i].Trim() is { Length: > 0 } v && v != "NA" ? v.Trim() : null;
        if (known is { } k)
        {
            var name = k.Kind == "playlist" ? F(0) : F(3) ?? F(0)?.Replace(" - Videos", "");
            return JobDescriptor.NewWatch(k.Kind, k.Id, name ?? k.Id, every, tabs, follow, backfill, maxHeight);
        }
        // a @handle or /c/ URL: the channel id comes from the listing
        var cid = F(1) ?? F(2);
        if (cid == null) { if (r.ExitCode != 0) YtDlp.Throw(r.Stderr); throw new InvalidOperationException("could not tell which channel or playlist that URL is"); }
        return JobDescriptor.NewWatch("channel", cid, F(3) ?? F(0)?.Replace(" - Videos", "") ?? cid, every, tabs, follow, backfill, maxHeight);
    }

    public static async Task<bool> AddAsync(IStore store, Paths paths, JobDescriptor job, CancellationToken ct) =>
        await store.PutAsync(paths.Job("watch", job.JobId), job.ToBytes(), Tags.Job("watch", "pending", job.AddedAt), null, true, ct);

    public sealed record ImportResult(int Added, int Existed, int Skipped);

    /// <summary>One watch job per channel of a channels.json ([{channelId, channelName, ...}]); existing ones are left alone.</summary>
    public static async Task<ImportResult> ImportAsync(IStore store, Paths paths, byte[] json, string every, string[] tabs, int maxHeight, CancellationToken ct)
    {
        WatchStage.ParseEvery(every);
        var items = JsonDocument.Parse(json).RootElement;
        if (items.ValueKind != JsonValueKind.Array) throw new ArgumentException("channels file must be a JSON array");
        int added = 0, existed = 0, skipped = 0;
        foreach (var it in items.EnumerateArray())
        {
            var id = it.TryGetProperty("channelId", out var i) ? i.GetString() : null;
            if (string.IsNullOrWhiteSpace(id) || !Regex.IsMatch(id, @"^UC[A-Za-z0-9_-]{22}$")) { skipped++; continue; }
            var name = it.TryGetProperty("channelName", out var n) ? n.GetString() : null;
            var job = JobDescriptor.NewWatch("channel", id, string.IsNullOrWhiteSpace(name) ? id : name, every, tabs, false, 0, maxHeight);
            if (await AddAsync(store, paths, job, ct)) added++; else existed++;
        }
        return new ImportResult(added, existed, skipped);
    }

    public static async Task<List<JobDescriptor>> ListAsync(IStore store, Paths paths, CancellationToken ct)
    {
        var list = new List<JobDescriptor>();
        foreach (var b in await store.ListAsync(paths.JobsDir("watch"), false, false, ct))
            if (await store.GetAsync(b.Name, ct) is { } c) list.Add(JobDescriptor.Parse(c.Data));
        return list.OrderBy(j => j.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static string FormatRow(JobDescriptor j) =>
        $"{j.JobId,-40} {j.Kind,-8} {j.Every ?? "1h",-4} last polled {(j.LastPolled ?? "never"),-20} new {j.Found ?? 0,4}  {j.Name}";
}
