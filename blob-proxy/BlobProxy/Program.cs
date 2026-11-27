using System.Diagnostics;
using System.Net;
using BlobProxy;
using Yarp.ReverseProxy.Forwarder;

// blob-proxy: a pass-through proxy for Azure Blob Storage that counts what goes through it. It holds no credentials and caches nothing:
// callers send their own SAS, and "https://<proxy>/<account>-<container>/<blob>?<sas>" is forwarded unchanged (headers, ranges, conditions,
// body streamed both ways) to "https://<account>.blob.core.windows.net/<container>/<blob>?<sas>". Only accounts in ALLOWED_ACCOUNTS are forwarded.
// Query strings (the SAS) are never logged or kept.
var builder = WebApplication.CreateSlimBuilder(args);
builder.Logging.ClearProviders(); builder.Logging.AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = "HH:mm:ss "; });
builder.Logging.SetMinimumLevel(LogLevel.Warning);   // YARP's own request logs would carry the URL with its SAS
builder.Logging.AddFilter("BlobProxy", LogLevel.Information);
var port = Environment.GetEnvironmentVariable("PORT") is { Length: > 0 } p ? int.Parse(p) : 8080;
builder.WebHost.ConfigureKestrel(k =>
{
    if (Environment.GetEnvironmentVariable("BIND") is { Length: > 0 } bind) k.Listen(IPAddress.Parse(bind), port);   // BIND=127.0.0.1 for a local run
    else k.ListenAnyIP(port);
    k.Limits.MaxRequestBodySize = null;   // block uploads up to the service's own limits
    k.Limits.MinRequestBodyDataRate = null; k.Limits.MinResponseDataRate = null;   // slow runners on a VPN must not be cut off by Kestrel
    k.AddServerHeader = false;
});
builder.Services.AddHttpForwarder();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<Stats>();

var app = builder.Build();
var log = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("BlobProxy");
var allow = Target.Allowlist(Environment.GetEnvironmentVariable("ALLOWED_ACCOUNTS"));
var suffix = Environment.GetEnvironmentVariable("BLOB_SUFFIX") is { Length: > 0 } s ? s.Trim().Trim('.') : "blob.core.windows.net";
var stats = app.Services.GetRequiredService<Stats>();
var version = typeof(Target).Assembly.GetName().Version?.ToString(3) ?? "?";
log.LogInformation("blob-proxy {Version} on :{Port}, forwarding to *.{Suffix} for {Count} account(s): {Accounts}", version, port, suffix, allow.Count, string.Join(",", allow));
if (allow.Count == 0) log.LogWarning("ALLOWED_ACCOUNTS is empty: every storage request is refused");

var http = new HttpMessageInvoker(new SocketsHttpHandler
{
    UseProxy = false, AllowAutoRedirect = false, AutomaticDecompression = DecompressionMethods.None, UseCookies = false,
    EnableMultipleHttp2Connections = true, PooledConnectionLifetime = TimeSpan.FromMinutes(5), ConnectTimeout = TimeSpan.FromSeconds(15),
    ActivityHeadersPropagator = null,
});
var config = new ForwarderRequestConfig { ActivityTimeout = TimeSpan.FromMinutes(10) };
var forwarder = app.Services.GetRequiredService<IHttpForwarder>();

app.MapGet("/healthz", () => Results.Text("ok"));
app.MapGet("/v1/stats", () => Results.Json(stats.Snapshot()));
app.MapGet("/", () => Results.Content(Page.Html(version), "text/html; charset=utf-8"));

app.Map("/{**rest}", async (HttpContext ctx) =>
{
    var target = Target.Parse(ctx.Request.Path.Value);
    if (target == null) { ctx.Response.StatusCode = 404; await ctx.Response.WriteAsync("not a storage path: /<account>-<container>/<blob>"); return; }
    if (!allow.Contains(target.Account)) { ctx.Response.StatusCode = 403; await ctx.Response.WriteAsync("account not allowed by this proxy"); return; }

    var client = ctx.Request.Headers["x-indexnode-node"].FirstOrDefault() is { Length: > 0 and <= 64 } n ? n : "ip:" + ctx.Connection.RemoteIpAddress;
    var op = Stats.Op(ctx.Request.Method, ctx.Request.Query["comp"].FirstOrDefault(), ctx.Request.Headers.ContainsKey("Range") || ctx.Request.Headers.ContainsKey("x-ms-range"));
    var key = new Stats.Key(client, op, target.Account + "-" + target.Container, Stats.Folder(target.BlobPath));

    // bytes are taken from the Content-Length headers (owner: "only ... through content-length headers"): bodies stream through untouched
    long bytesIn = ctx.Request.ContentLength ?? 0;
    ctx.Request.Path = target.BlobPath;   // YARP builds prefix + path + query
    var sw = Stopwatch.StartNew();
    var error = await forwarder.SendAsync(ctx, target.Prefix(suffix), http, config, HttpTransformer.Default);
    sw.Stop();
    int status = error == ForwarderError.None ? ctx.Response.StatusCode : 0;
    long bytesOut = ctx.Request.Method == "HEAD" ? 0 : ctx.Response.ContentLength ?? 0;
    stats.Record(key, bytesIn, bytesOut, sw.ElapsedMilliseconds, status);
    if (error != ForwarderError.None)
        log.LogWarning("{Method} {Store}{Path}: {Error} after {Ms} ms ({Client})", ctx.Request.Method, key.Store, target.BlobPath, error, sw.ElapsedMilliseconds, client);
});

_ = Task.Run(async () =>
{
    using var tick = new PeriodicTimer(TimeSpan.FromMinutes(1));
    while (await tick.WaitForNextTickAsync()) if (stats.MinuteLine() is { } line) log.LogInformation("{Line}", line);
});

app.Run();

public partial class Program;
