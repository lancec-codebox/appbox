using System.Net;
using System.Text.RegularExpressions;
using IndexNode.Hosting;

namespace IndexNode.Plugin;

/// <summary>
/// An HttpMessageHandler that sends the store client's requests through IHost.StoreAsync. The store client (RestStore) is given a URL with a placeholder
/// signature; this handler removes it, and the host adds the real SAS natively. The plugin never holds the SAS.
/// </summary>
public sealed partial class HostStoreHandler(IHost host) : HttpMessageHandler
{
    public const string Placeholder = "sig=HOSTPLACEHOLDER";
    public const int MaxBody = 512 << 20;

    [GeneratedRegex(@"[?&]sig=HOSTPLACEHOLDER")] private static partial Regex PlaceholderRx();

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var pq = PlaceholderRx().Replace(request.RequestUri!.PathAndQuery, "");
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var h in request.Headers) headers[h.Key] = string.Join(",", h.Value);
        byte[]? body = null;
        if (request.Content != null)
        {
            body = await request.Content.ReadAsByteArrayAsync(ct);
            foreach (var h in request.Content.Headers) headers[h.Key] = string.Join(",", h.Value);
        }
        var r = await host.StoreAsync(request.Method.Method, pq, headers, body, MaxBody, ct);
        var resp = new HttpResponseMessage((HttpStatusCode)r.Status) { RequestMessage = request, Content = new ByteArrayContent(r.Body) };
        if (!string.IsNullOrEmpty(r.ContentType)) resp.Content.Headers.TryAddWithoutValidation("Content-Type", r.ContentType);
        foreach (var line in r.Headers.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var i = line.IndexOf(':'); if (i <= 0) continue;
            var name = line[..i].Trim(); var val = line[(i + 1)..].Trim();
            if (name.Equals("Content-Type", StringComparison.OrdinalIgnoreCase) || name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)) continue;
            if (!resp.Headers.TryAddWithoutValidation(name, val)) resp.Content.Headers.TryAddWithoutValidation(name, val);
        }
        return resp;
    }
}
