using System.Text.RegularExpressions;

namespace BlobProxy;

/// <summary>
/// The storage request a proxy path stands for. The first path segment is "&lt;account&gt;-&lt;container&gt;": Azure account names are 3 to 24
/// lowercase letters and digits (never a hyphen), so the first hyphen always ends the account; the container is the rest (it may hold hyphens).
/// "/acct-media/index/x.json" -> https://acct.blob.core.windows.net/media + "/index/x.json". A first segment without a hyphen is the proxy's own
/// (health, stats, the page).
/// </summary>
public sealed record Target(string Account, string Container, string BlobPath)
{
    static readonly Regex AccountName = new("^[a-z0-9]{3,24}$", RegexOptions.CultureInvariant);
    static readonly Regex ContainerName = new("^(\\$root|\\$web|\\$logs|[a-z0-9](?!.*--)[a-z0-9-]{1,61}[a-z0-9])$", RegexOptions.CultureInvariant);

    /// <summary>The destination prefix YARP appends <see cref="BlobPath"/> and the query to.</summary>
    public string Prefix(string suffix) => $"https://{Account}.{suffix}/{Container}";

    /// <summary>The target of a request path, or null when the first segment is not "&lt;account&gt;-&lt;container&gt;" with valid names.</summary>
    public static Target? Parse(string? path)
    {
        if (string.IsNullOrEmpty(path) || path[0] != '/') return null;
        int slash = path.IndexOf('/', 1);
        var first = slash < 0 ? path[1..] : path[1..slash];
        int dash = first.IndexOf('-');
        if (dash <= 0 || dash == first.Length - 1) return null;
        var account = first[..dash]; var container = first[(dash + 1)..];
        if (!AccountName.IsMatch(account) || !ContainerName.IsMatch(container)) return null;
        return new Target(account, container, slash < 0 ? "" : path[slash..]);
    }

    /// <summary>The allowlist from ALLOWED_ACCOUNTS (comma or space separated account names). Empty = nothing is forwarded.</summary>
    public static HashSet<string> Allowlist(string? env) =>
        new((env ?? "").Split([',', ' ', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(x => x.ToLowerInvariant()), StringComparer.Ordinal);
}
