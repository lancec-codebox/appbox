namespace IndexNode.Hosting;

/// <summary>Builds the real store URL for a plugin request: the path must lie under the container URL; the SAS query is appended here and nowhere else.</summary>
public static class StoreGate
{
    /// <param name="storeUrl">https://acct.blob.core.windows.net/index (no credential)</param>
    /// <param name="sas">the SAS query without a leading "?"</param>
    /// <param name="pathAndQuery">relative to the account: "/index/index/jobs/x.json?comp=metadata" (what RestStore builds from the container URL)</param>
    public static Uri Build(string storeUrl, string sas, string pathAndQuery)
    {
        if (string.IsNullOrEmpty(storeUrl) || string.IsNullOrEmpty(sas)) throw new InvalidOperationException("the node is not set up (no store)");
        var store = new Uri(storeUrl);
        if (!pathAndQuery.StartsWith('/') || pathAndQuery.Contains("..") || pathAndQuery.Contains("//") || pathAndQuery.Contains('@') || pathAndQuery.Contains('\\'))
            throw new UnauthorizedAccessException("refused store path");
        var uri = new Uri(store.GetLeftPart(UriPartial.Authority) + pathAndQuery);
        var container = store.AbsolutePath.TrimEnd('/');
        if (uri.AbsolutePath != container && !uri.AbsolutePath.StartsWith(container + "/", StringComparison.Ordinal))
            throw new UnauthorizedAccessException("the request is outside the store container");
        var q = uri.Query.TrimStart('?');
        if (q.Contains("sig=", StringComparison.OrdinalIgnoreCase)) throw new UnauthorizedAccessException("the plugin must not send a signature");
        return new Uri(uri.GetLeftPart(UriPartial.Path) + "?" + (q.Length > 0 ? q + "&" : "") + sas.TrimStart('?'));
    }
}
