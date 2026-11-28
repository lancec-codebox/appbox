using System.Net;
using System.Net.Http.Headers;

namespace IndexNode;

/// <summary>
/// 0.13.91 (owner 2026-11-27: "yes you can use azure files as a persistent cache"; the drama store's premium share nexisdrivewus2files/media): a copy of a node's local caches
/// that survives a container restart. Files are named by a path relative to the mirror's own folder. None of its calls count against the blob store's op limits: it is a
/// separate account and a cache, never the source of truth (a copy that disagrees with the store is dropped by the reader's own checks and written again).
/// </summary>
public interface ICacheMirror
{
    /// <summary>The file's length, or null when it is not there.</summary>
    Task<long?> LengthAsync(string rel, CancellationToken ct);
    /// <summary>The whole file into <paramref name="localPath"/> (written to a temporary name and moved); false when it is not there.</summary>
    Task<bool> DownloadAsync(string rel, string localPath, CancellationToken ct);
    /// <summary>Sets the file's length, creating it (and its folders) when it is not there. New bytes read as zero until written.</summary>
    Task SetLengthAsync(string rel, long length, CancellationToken ct);
    /// <summary>Writes bytes at an offset inside the file's current length.</summary>
    Task WriteAsync(string rel, long offset, ReadOnlyMemory<byte> data, CancellationToken ct);
}

public static class CacheMirror
{
    /// <summary>The process's mirror (env INDEXNODE_CACHE_MIRROR_URL: a share URL with a SAS that can read, write, create and delete files; INDEXNODE_CACHE_MIRROR_PREFIX: the folder
    /// inside the share, default "cache/&lt;node name&gt;"). Null: no mirror.</summary>
    public static ICacheMirror? Current { get; set; }

    public static ICacheMirror? FromEnvironment(string nodeName)
    {
        var url = Environment.GetEnvironmentVariable("INDEXNODE_CACHE_MIRROR_URL");
        if (string.IsNullOrWhiteSpace(url)) return null;
        Redact.Register(url); var q = url.IndexOf('?'); if (q > 0) Redact.Register(url[(q + 1)..]);
        var prefix = Environment.GetEnvironmentVariable("INDEXNODE_CACHE_MIRROR_PREFIX");
        if (string.IsNullOrWhiteSpace(prefix)) prefix = "cache/" + (string.IsNullOrWhiteSpace(nodeName) ? "node" : nodeName);
        return new AzureFilesMirror(new HttpClient { Timeout = TimeSpan.FromMinutes(10) }, url.Trim(), prefix.Trim('/'));
    }

    /// <summary>A whole local file into the mirror (set the length, then the bytes in ranges).</summary>
    public static async Task UploadAsync(ICacheMirror m, string rel, string localPath, CancellationToken ct)
    {
        await using var fs = new FileStream(localPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 20, true);
        await m.SetLengthAsync(rel, fs.Length, ct);
        var buf = new byte[AzureFilesMirror.Batch]; long at = 0; int n;   // big reads; WriteAsync sends them as parallel 4 MiB ranges
        while ((n = await fs.ReadAsync(buf, ct)) > 0) { await m.WriteAsync(rel, at, buf.AsMemory(0, n), ct); at += n; }
    }
}

/// <summary>Azure Files over REST with a SAS (no SDK, no account key): HEAD / GET a file, create folders and files, set a file's length, write ranges of at most 4 MiB.</summary>
public sealed class AzureFilesMirror : ICacheMirror
{
    /// <summary>Azure Files takes at most 4 MiB per write (Put Range); ranges of one file are written <see cref="Parallel"/> at a time, from batches of <see cref="Batch"/> bytes.</summary>
    public const int MaxRange = 4 << 20, Parallel = 8, Batch = 64 << 20;
    const string Version = "2023-11-03";
    readonly HttpClient http; readonly string baseUrl, query, prefix;

    /// <param name="shareSasUrl">https://&lt;account&gt;.file.core.windows.net/&lt;share&gt;?&lt;sas&gt;</param>
    public AzureFilesMirror(HttpClient http, string shareSasUrl, string prefix)
    {
        this.http = http; var q = shareSasUrl.IndexOf('?');
        if (q < 0) throw new ArgumentException("the cache mirror URL needs a SAS (…?sv=…)");
        baseUrl = shareSasUrl[..q].TrimEnd('/'); query = shareSasUrl[(q + 1)..]; this.prefix = prefix.Trim('/');
    }

    string Path(string rel) => string.Join('/', (prefix.Length > 0 ? prefix + "/" + rel : rel).Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.EscapeDataString));
    string Url(string path, string? extra = null) => $"{baseUrl}/{path}?{query}{(extra == null ? "" : "&" + extra)}";

    HttpRequestMessage Req(HttpMethod m, string url)
    {
        var r = new HttpRequestMessage(m, url);
        r.Headers.TryAddWithoutValidation("x-ms-version", Version); r.Headers.TryAddWithoutValidation("x-ms-date", DateTime.UtcNow.ToString("R"));
        return r;
    }

    static async Task Ok(HttpResponseMessage r, string what)
    {
        if (r.IsSuccessStatusCode) return;
        var body = ""; try { body = await r.Content.ReadAsStringAsync(); } catch { }
        throw new IOException($"cache mirror: {what}: HTTP {(int)r.StatusCode} {(body.Length > 200 ? body[..200] : body)}");
    }

    public async Task<long?> LengthAsync(string rel, CancellationToken ct)
    {
        using var r = await http.SendAsync(Req(HttpMethod.Head, Url(Path(rel))), ct);
        if (r.StatusCode == HttpStatusCode.NotFound) return null;
        await Ok(r, "length of " + rel);
        return r.Content.Headers.ContentLength;
    }

    public async Task<bool> DownloadAsync(string rel, string localPath, CancellationToken ct)
    {
        using var r = await http.SendAsync(Req(HttpMethod.Get, Url(Path(rel))), HttpCompletionOption.ResponseHeadersRead, ct);
        if (r.StatusCode == HttpStatusCode.NotFound) return false;
        await Ok(r, "read of " + rel);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(localPath))!);
        var tmp = localPath + "." + Guid.NewGuid().ToString("N")[..6] + ".part";
        try
        {
            await using (var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 20, true)) await (await r.Content.ReadAsStreamAsync(ct)).CopyToAsync(fs, ct);
            if (r.Content.Headers.ContentLength is { } want && new FileInfo(tmp).Length != want) throw new IOException($"cache mirror: {rel}: got {new FileInfo(tmp).Length} of {want} bytes");
            File.Move(tmp, localPath, true); return true;
        }
        finally { try { if (File.Exists(tmp)) File.Delete(tmp); } catch { } }
    }

    public async Task SetLengthAsync(string rel, long length, CancellationToken ct)
    {
        var p = Path(rel);
        using (var r0 = Req(HttpMethod.Put, Url(p, "comp=properties")))
        {
            r0.Headers.TryAddWithoutValidation("x-ms-content-length", length.ToString()); r0.Content = new ByteArrayContent([]);
            using var r = await http.SendAsync(r0, ct);
            if (r.StatusCode != HttpStatusCode.NotFound) { await Ok(r, "length of " + rel); return; }
        }
        // not there: its folders, then the file
        var parts = p.Split('/'); var dir = "";
        for (int i = 0; i < parts.Length - 1; i++)
        {
            dir = dir.Length == 0 ? parts[i] : dir + "/" + parts[i];
            using var d0 = Req(HttpMethod.Put, Url(dir, "restype=directory")); d0.Content = new ByteArrayContent([]);
            using var d = await http.SendAsync(d0, ct);
            if (d.StatusCode != HttpStatusCode.Conflict) await Ok(d, "folder " + dir);   // 409: it is there
        }
        using var c0 = Req(HttpMethod.Put, Url(p));
        c0.Headers.TryAddWithoutValidation("x-ms-type", "file"); c0.Headers.TryAddWithoutValidation("x-ms-content-length", length.ToString()); c0.Content = new ByteArrayContent([]);
        using var c = await http.SendAsync(c0, ct);
        await Ok(c, "create " + rel);
    }

    public async Task WriteAsync(string rel, long offset, ReadOnlyMemory<byte> data, CancellationToken ct)
    {
        var p = Path(rel);
        var starts = Enumerable.Range(0, (data.Length + MaxRange - 1) / MaxRange).Select(i => i * MaxRange);
        await System.Threading.Tasks.Parallel.ForEachAsync(starts, new ParallelOptions { MaxDegreeOfParallelism = Parallel, CancellationToken = ct }, async (at, t) =>
        {   // disjoint ranges of one file: safe to write at once
            int n = Math.Min(MaxRange, data.Length - at); long from = offset + at;
            using var r0 = Req(HttpMethod.Put, Url(p, "comp=range"));
            r0.Headers.TryAddWithoutValidation("x-ms-range", $"bytes={from}-{from + n - 1}"); r0.Headers.TryAddWithoutValidation("x-ms-write", "update");
            r0.Content = new ReadOnlyMemoryContent(data.Slice(at, n)); r0.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            using var r = await http.SendAsync(r0, t);
            await Ok(r, $"write of {rel} at {from}");
        });
    }
}
