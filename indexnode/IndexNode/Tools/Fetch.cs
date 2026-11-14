using System.Security.Cryptography;

namespace IndexNode;

public sealed class ChecksumMismatch(string message) : Exception(message);

/// <summary>Downloads a file and refuses it unless its sha256 is the pinned one.</summary>
public static class Fetch
{
    public static async Task<string> Sha256Async(string path, CancellationToken ct = default)
    {
        await using var fs = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(fs, ct)).ToLowerInvariant();
    }

    /// <summary>Streams the url into <paramref name="dest"/> (through a .part file) and checks the sha256; a bad file is deleted.</summary>
    public static async Task VerifiedAsync(HttpClient http, string url, string dest, string sha256, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(dest))!);
        var tmp = dest + ".part";
        try
        {
            using (var r = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                if (!r.IsSuccessStatusCode) throw new InvalidOperationException($"download failed: HTTP {(int)r.StatusCode} for {Redact.Sas(url)}");
                await using var fs = File.Create(tmp);
                await r.Content.CopyToAsync(fs, ct);
            }
            var got = await Sha256Async(tmp, ct);
            if (!string.Equals(got, sha256, StringComparison.OrdinalIgnoreCase))
                throw new ChecksumMismatch($"sha256 of {Path.GetFileName(dest)} is {got}, expected {sha256}");
            File.Move(tmp, dest, true);
        }
        finally { try { if (File.Exists(tmp)) File.Delete(tmp); } catch { } }
    }
}
