using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace IndexNode.Hosting.Medusa;

/// <summary>
/// The fallback until Medusa 1.10 ships /v2: plain video downloads over /v1 jobs and media. yt-dlp "download" calls become a v1 job (the last argument is the URL,
/// -f height picks the quality); video.mp4 and a minimal video.info.json are written into a folder of the app sandbox. Subtitle, comment and listing calls
/// answer "not supported" (exit 2 with a message), ffprobe is answered from the media item. No v1 token: /v1 has none.
/// </summary>
public sealed class MedusaV1Tools(HttpClient http, string sandboxDir, string? baseUrl = null) : ITools
{
    private readonly string baseUrl = (baseUrl ?? MedusaClient.DefaultBase).TrimEnd('/');
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(1);
    private readonly Dictionary<string, JsonObject> items = new();

    public Task<IToolWorkspace> CreateWorkspaceAsync(string name, CancellationToken ct)
    {
        var dir = Path.Combine(sandboxDir, "v1-" + Regex.Replace(name, "[^A-Za-z0-9_-]", "_") + "-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(dir);
        return Task.FromResult<IToolWorkspace>(new LocalWorkspace(name, dir));
    }

    public Task<ProcResult> RunYtDlpAsync(IEnumerable<string> args, CancellationToken ct, Action<string>? onStdout = null) =>
        Task.FromResult(new ProcResult(2, "", "ERROR: Medusa /v1 fallback cannot list or look up sources (needs Medusa 1.10 /v2)"));

    public async Task<ProcResult> RunAsync(IToolWorkspace ws, string tool, IReadOnlyList<string> args, CancellationToken ct, Action<string>? onStdout = null)
    {
        var w = (LocalWorkspace)ws;
        if (tool == "ffmpeg") return new ProcResult(2, "", "ffmpeg is not available over Medusa /v1");
        if (tool == "ffprobe")
        {
            var file = args.LastOrDefault() ?? "";
            var item = items.GetValueOrDefault(w.Dir);
            if (item == null || !File.Exists(Path.Combine(w.Dir, file))) return new ProcResult(1, "", "no such file");
            var joined = string.Join(' ', args);
            if (joined.Contains("height")) return new ProcResult(0, ((int?)item["height"] ?? 0) + "\n", "");
            return new ProcResult(0, ((double?)item["durationSec"] ?? 0).ToString(System.Globalization.CultureInfo.InvariantCulture) + "\n", "");
        }
        if (args.Contains("--skip-download") || args.Contains("--flat-playlist"))
            return new ProcResult(0, "", "");   // subtitles are not offered by /v1 plain video downloads: the download stage treats "no tracks" as fine
        var url = args.LastOrDefault(a => a.StartsWith("http", StringComparison.OrdinalIgnoreCase)) ?? throw new InvalidOperationException("no url in the yt-dlp arguments");
        int height = 480;
        var fi = args.ToList().IndexOf("-f"); if (fi >= 0 && fi + 1 < args.Count && Regex.Match(args[fi + 1], @"height<=?(\d+)") is { Success: true } hm) height = int.Parse(hm.Groups[1].Value);
        var quality = height >= 1080 ? "1080p" : height >= 720 ? "720p" : height >= 480 ? "480p" : "360p";

        using (var post = new HttpRequestMessage(HttpMethod.Post, baseUrl + "/v1/jobs") { Content = new StringContent(new JsonObject { ["url"] = url, ["want"] = new JsonArray("video"), ["quality"] = quality }.ToJsonString(), Encoding.UTF8, "application/json") })
        {
            using var pr = await http.SendAsync(post, ct);
            var pt = await pr.Content.ReadAsStringAsync(ct);
            if (!pr.IsSuccessStatusCode) return new ProcResult(1, "", $"Medusa refused the job: HTTP {(int)pr.StatusCode} {pt}");
            var id = (string?)JsonNode.Parse(pt)?["id"] ?? throw new InvalidOperationException("Medusa job answer has no id");
            string state;
            string? message;
            try
            {
                while (true)
                {
                    var j = JsonNode.Parse(await http.GetStringAsync(baseUrl + "/v1/jobs/" + id, ct))!;
                    state = (string?)j["state"] ?? "queued"; message = (string?)j["message"];
                    if ((double?)j["progress"] is { } pg) onStdout?.Invoke($"[download] {pg * 100:0.0}% of Medusa job");
                    if (state is "done" or "failed") { if (state == "failed") return new ProcResult(1, "", $"ERROR: {(string?)j["code"]} {message}"); break; }
                    await Task.Delay(PollInterval, ct);
                }
            }
            catch (OperationCanceledException) { try { await http.DeleteAsync(baseUrl + "/v1/media/" + id, CancellationToken.None); } catch { } throw; }
            var media = JsonNode.Parse(await http.GetStringAsync(baseUrl + "/v1/jobs/" + id + "/result", ct))!.AsObject();
            var mediaId = (string?)media["mediaId"] ?? id;
            var path = (string?)media["video"]?["path"] ?? "/v1/media/" + mediaId + "/video";
            using var vr = await http.GetAsync(baseUrl + path, HttpCompletionOption.ResponseHeadersRead, ct);
            vr.EnsureSuccessStatusCode();
            await using (var src = await vr.Content.ReadAsStreamAsync(ct))
            await using (var dst = File.Create(Path.Combine(w.Dir, "video.mp4"))) await src.CopyToAsync(dst, ct);
            var info = new JsonObject { ["id"] = (string?)media["videoId"] ?? mediaId, ["title"] = (string?)media["title"], ["duration"] = (double?)media["durationSec"], ["webpage_url"] = url, ["extractor"] = (string?)media["extractor"] };
            File.WriteAllText(Path.Combine(w.Dir, "video.info.json"), info.ToJsonString());
            media["durationSec"] ??= 0;
            items[w.Dir] = media;
            try { await http.DeleteAsync(baseUrl + "/v1/media/" + mediaId, ct); } catch { /* the phone keeps it until the user deletes it */ }
        }
        return new ProcResult(0, "", "");
    }
}

/// <summary>A plain folder as a workspace (the v1 fallback and tests).</summary>
public sealed class LocalWorkspace(string name, string dir) : IToolWorkspace
{
    public string Name { get; } = name;
    public string Dir { get; } = dir;
    public Task<bool> ExistsAsync(string path, CancellationToken ct) => Task.FromResult(File.Exists(Path.Combine(Dir, path)));
    public Task<IReadOnlyList<string>> ListAsync(string dir, string glob, CancellationToken ct)
    {
        var d = Path.Combine(Dir, dir);
        IReadOnlyList<string> r = Directory.Exists(d) ? Directory.GetFiles(d, glob).Select(f => Path.GetRelativePath(Dir, f).Replace('\\', '/')).OrderBy(x => x, StringComparer.Ordinal).ToList() : [];
        return Task.FromResult(r);
    }
    public Task<string> ReadTextAsync(string path, CancellationToken ct) => File.ReadAllTextAsync(Path.Combine(Dir, path), ct);
    public Task WriteTextAsync(string path, string text, CancellationToken ct) => File.WriteAllTextAsync(Path.Combine(Dir, path), text, ct);
    public Task<long> TotalBytesAsync(CancellationToken ct) => Task.FromResult(new DirectoryInfo(Dir).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length));
    public Task<long> SizeAsync(string path, CancellationToken ct) => Task.FromResult(new FileInfo(Path.Combine(Dir, path)).Length);
    public Task<string> LocalPathAsync(string path, CancellationToken ct) => Task.FromResult(Path.Combine(Dir, path));
    public ValueTask DisposeAsync() { try { Directory.Delete(Dir, true); } catch { } return ValueTask.CompletedTask; }
}
