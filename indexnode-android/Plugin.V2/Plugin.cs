using System.Text;
using System.Text.Json;
using Spike.Contract;
namespace PluginV2;

/// <summary>v2: behaves visibly differently and drives Medusa's local API on 127.0.0.1:38917.</summary>
public sealed class Plugin : IPlugin
{
    const string Base = "http://127.0.0.1:38917";
    // Short public video: "Me at the zoo" (19 s), first YouTube upload.
    const string DefaultVideo = "https://www.youtube.com/watch?v=jNQXAC9IVRw";

    public async Task<string> RunAsync(IHost host, string args)
    {
        var video = string.IsNullOrWhiteSpace(args) ? DefaultVideo : args.Trim();
        var steps = new List<string>();
        void Step(string s) { steps.Add(s); host.Log(s); host.SetStatus(s); }
        // IndexNode core type compiled in: proves its protocol code runs inside the loaded DLL.
        Step($"v2 running on {System.Runtime.InteropServices.RuntimeInformation.OSArchitecture}/{System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}");

        var st = await host.GetAsync(Base + "/v1/status");
        Step($"GET /v1/status -> {st.Status} {Trunc(st.Text)}");
        if (st.Status != 200) return Result(false, steps, "Medusa not reachable (is it installed and the helper service running?)");

        var body = JsonSerializer.Serialize(new Dictionary<string, object> { ["url"] = video, ["quality"] = "360p", ["want"] = new[] { "video" } });
        var job = await host.SendAsync("POST", Base + "/v1/jobs", body, new Dictionary<string, string> { ["Content-Type"] = "application/json" });
        Step($"POST /v1/jobs -> {job.Status} {Trunc(job.Text)}");
        if (job.Status != 200) return Result(false, steps, "job rejected");
        var id = JsonDocument.Parse(job.Text).RootElement.GetProperty("id").GetString()!;

        string state = "";
        for (int i = 0; i < 120; i++)
        {
            await Task.Delay(2000);
            var js = await host.GetAsync($"{Base}/v1/jobs/{id}");
            var root = JsonDocument.Parse(js.Text).RootElement;
            state = root.GetProperty("state").GetString() ?? "";
            Step($"GET /v1/jobs/{id} -> {js.Status} state={state}" + (root.TryGetProperty("progress", out var p) ? " progress=" + p : ""));
            if (state is not ("queued" or "running" or "fetching" or "pending" or "downloading")) break;
        }
        var res = await host.GetAsync($"{Base}/v1/jobs/{id}/result");
        Step($"GET /v1/jobs/{id}/result -> {res.Status} {Trunc(res.Text)}");
        if (res.Status != 200) return Result(false, steps, "job did not finish: " + state);

        var rr = JsonDocument.Parse(res.Text).RootElement;
        var mediaId = rr.TryGetProperty("mediaId", out var mi) ? mi.GetString()! : rr.GetProperty("media").GetProperty("mediaId").GetString()!;
        var media = await host.GetAsync($"{Base}/v1/media/{mediaId}");
        var mr = JsonDocument.Parse(media.Text).RootElement;
        var keys = string.Join(",", mr.EnumerateObject().Select(o => o.Name));
        Step($"GET /v1/media/{mediaId} -> {media.Status} keys=[{keys}] title={Prop(mr, "title")} durationSec={Prop(mr, "durationSec")} height={Prop(mr, "height")} extractor={Prop(mr, "extractor")}");

        var vid = await host.GetAsync($"{Base}/v1/media/{mediaId}/video",
            new Dictionary<string, string> { ["Range"] = "bytes=0-1048575" }, maxBytes: 1 << 20);
        Step($"GET /v1/media/{mediaId}/video Range 0-1048575 -> {vid.Status} {vid.ContentType} got {vid.Body.Length} bytes; {Hdr(vid, "Content-Range")}");
        return Result(vid.Status is 200 or 206, steps, null);
    }

    static string Prop(JsonElement e, string n) => e.TryGetProperty(n, out var v) ? v.ToString() : "(absent)";
    static string Hdr(HostResponse r, string name) =>
        r.Headers.Split('\n').FirstOrDefault(l => l.StartsWith(name + ":", StringComparison.OrdinalIgnoreCase))?.Trim() ?? name + ": (none)";
    static string Trunc(string s) => s.Length > 300 ? s[..300] + "..." : s;

    static string Result(bool ok, List<string> steps, string? error)
    {
        var sb = new StringBuilder("{\"plugin\":\"v2\",\"ok\":" + (ok ? "true" : "false"));
        if (error != null) sb.Append(",\"error\":" + JsonSerializer.Serialize(error));
        sb.Append(",\"steps\":" + JsonSerializer.Serialize(steps) + "}");
        return sb.ToString();
    }
}
