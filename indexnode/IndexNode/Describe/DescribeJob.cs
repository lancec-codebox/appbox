using System.Text.Json;
using System.Text.Json.Nodes;

namespace IndexNode;

/// <summary>Gathers a job's inputs (text track, description, thumbnail, cast sheets, credits) and runs or dries the describe step.</summary>
public static class DescribeJob
{
    /// <summary>Writes the files the vision call needs to <paramref name="work"/> and builds the input.</summary>
    public static async Task<DescribeInput> GatherAsync(IJobSource src, string work, CancellationToken ct)
    {
        Directory.CreateDirectory(work);
        async Task<JsonNode?> J(string f) => await src.ReadAsync(f, ct) is { } b ? JsonNode.Parse(b) : null;
        var info = await J("info.json") as JsonObject; var downloaded = await J("downloaded.json") as JsonObject;
        double dur = downloaded?["durationSeconds"]?.GetValue<double>() ?? info?["duration"]?.GetValue<double>() ?? 0;
        var ocr = TextCues(await src.ReadAsync("ocr/subs.zh.vtt", ct)); var asr = TextCues(await src.ReadAsync("asr/" + Transcribe.ModelName + ".zh.vtt", ct));
        var cues = TextTrack.Merge(ocr, asr);
        var desc = await src.ReadAsync("description.txt", ct) is { } db ? System.Text.Encoding.UTF8.GetString(db) : info?["description"]?.GetValue<string>() ?? "";
        var credits = new List<string>();
        if (await J("cast/credits.json") is JsonObject cj) foreach (var l in (cj["lines"] as JsonArray) ?? []) if (l?["text"]?.GetValue<string>() is { Length: > 0 } t) credits.Add(t);
        var clusters = new List<MainCluster>(); var sheets = new Dictionary<int, string>();
        if (await J("cast/clusters.json") is JsonObject cl)
            foreach (var c in ((cl["clusters"] as JsonArray) ?? []).OfType<JsonObject>().Where(c => c["main"]?.GetValue<bool>() == true).OrderByDescending(c => c["screenSeconds"]!.GetValue<double>()))
            {
                int id = c["id"]!.GetValue<int>();
                clusters.Add(new MainCluster(id, c["screenSeconds"]!.GetValue<double>(), c["first"]!.GetValue<double>(), c["masked"]?.GetValue<bool>() ?? false));
                if (await src.ReadAsync($"cast/sheet_{id}.jpg", ct) is { } sb) { var f = Path.Combine(work, $"sheet_{id}.jpg"); File.WriteAllBytes(f, sb); sheets[id] = f; }
            }
        string? thumb = null; if (await src.ReadAsync("thumb.jpg", ct) is { } tb) { thumb = Path.Combine(work, "thumb.jpg"); File.WriteAllBytes(thumb, tb); }
        string? ps = null; int pn = 0;
        if (await src.ReadAsync("cast/poster_sheet.jpg", ct) is { } pb) { ps = Path.Combine(work, "poster_sheet.jpg"); File.WriteAllBytes(ps, pb); pn = (await J("cast/poster_cands.json") as JsonArray)?.Count(x => x?["shortlist"]?.GetValue<bool>() ?? true) ?? 0; }
        return new DescribeInput
        {
            JobId = src.JobId, Title = info?["title"]?.GetValue<string>() ?? "", Description = desc, Duration = dur, TextSource = ocr.Count > 0 ? "burned-in OCR" : "transcript",
            Cues = cues, Credits = credits, Clusters = clusters, Thumb = thumb, Sheets = sheets, PosterSheet = ps, PosterCount = pn,
        };
    }

    static List<ShapeCue> TextCues(byte[]? b) => b == null ? [] : CueShaper.ParseVtt(System.Text.Encoding.UTF8.GetString(b));

    /// <summary>The step: describe.json bytes. Without an AI endpoint the step is skipped (null), it does not fail.</summary>
    public static async Task<byte[]?> RunAsync(IChat? chat, AiConfig? ai, DescribeInput d, CancellationToken ct)
    {
        if (chat == null || ai is not { Configured: true }) return null;
        var res = await Describe.RunAsync(chat, ai.TextModel!, ai.VisionModel ?? ai.TextModel!, d, ct);
        return JsonSerializer.SerializeToUtf8Bytes(res, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
    }

    /// <summary>Dry mode: the request that would be sent, written as JSON; nothing is sent.</summary>
    public static string Dry(DescribeInput d, AiConfig? ai)
    {
        var text = ai?.TextModel ?? "(text model)"; var vision = ai?.VisionModel ?? ai?.TextModel ?? "(vision model)";
        var main = Describe.BuildMain(d, vision);
        var stub = new JsonObject { ["teaser"] = new JsonObject { ["en"] = "<teaser from the first call>" }, ["cast"] = new JsonArray(), ["relations"] = new JsonArray() };
        return Describe.DryJson([main, Describe.BuildCheck(stub, Describe.Cutoff(d.Duration), text)]);
    }
}
