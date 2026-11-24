using System.Net;
using IndexNode.Hosting;

namespace IndexNode.Plugin;

/// <summary>
/// An HttpMessageHandler over IHost.HttpAsync, so IndexNode.Core's clients of the check server (the inline check of a sample, the download service's hand-over)
/// run on the phone unchanged. The host adds no credential: the caller's own Authorization header goes through as it is.
/// </summary>
public sealed class HostHttpHandler(IHost host) : HttpMessageHandler
{
    public const int MaxBody = 8 << 20;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var h in request.Headers) headers[h.Key] = string.Join(",", h.Value);
        byte[]? body = null;
        if (request.Content != null)
        {
            body = await request.Content.ReadAsByteArrayAsync(ct);
            foreach (var h in request.Content.Headers) headers[h.Key] = string.Join(",", h.Value);
        }
        var r = await host.HttpAsync(request.Method.Method, request.RequestUri!.AbsoluteUri, headers, body, MaxBody, ct);
        var resp = new HttpResponseMessage((HttpStatusCode)r.Status) { RequestMessage = request, Content = new ByteArrayContent(r.Body) };
        if (!string.IsNullOrEmpty(r.ContentType)) resp.Content.Headers.TryAddWithoutValidation("Content-Type", r.ContentType);
        return resp;
    }
}
