using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace IndexNode;

/// <summary>
/// 0.10.4 <c>indexnode describe ending --backfill</c>: adds describe-7's "ending" to the describe.json inside existing describe.zip blobs.
/// The model sees only the title, the upload description, the last <see cref="TailSeconds"/> of the text track and the existing synopsis (no images, no re-describe),
/// several dramas per call; a drama the model leaves out is asked again in smaller batches and, failing that, left for the next run.
/// </summary>
public static class EndingBackfill
{
    public const int TailSeconds = 180, Batch = 8, RetryBatch = 3, MaxDescription = 1500, MaxTail = 3000, MaxSynopsis = 1500;

    public sealed record Item(string Id, string Title, string Description, string Tail, string Synopsis);
    public sealed record Report(int Found, int Asked, int Written, int Skipped, int Raced, int Unanswered, int Calls);

    /// <summary>The prompt for one batch: one block per drama, answer keyed by id.</summary>
    public static string Prompt(IEnumerable<Item> items)
    {
        var f = PromptFile.Load("ending"); var sb = new StringBuilder();
        sb.AppendLine(f.Text("intro")); sb.AppendLine(f.Text("rules")); sb.AppendLine(f.Text("answer"));
        foreach (var i in items)
            sb.AppendLine(f.Fill("drama-block", ("id", i.Id), ("title", i.Title), ("description", Cut(i.Description, MaxDescription)), ("synopsis", Cut(i.Synopsis, MaxSynopsis)), ("minutes", (TailSeconds / 60).ToString()),
                ("tail", i.Tail.Length == 0 ? f.Text("no-text") : Cut(i.Tail, MaxTail, fromEnd: true))));
        return sb.ToString();
    }

    static string Cut(string s, int n, bool fromEnd = false) => s.Length <= n ? s : fromEnd ? "…" + s[^n..] : s[..n] + "…";

    /// <summary>The cues that start in the last <paramref name="seconds"/> of the text, as "[m:ss] text" lines.</summary>
    public static string Tail(IReadOnlyList<ShapeCue> cues, double duration, int seconds = TailSeconds)
    {
        if (cues.Count == 0) return "";
        double end = Math.Max(duration, cues.Max(c => c.End)), from = end - seconds;
        return TextTrack.Compress(cues.Where(c => c.End >= from).OrderBy(c => c.Start).ToList(), 20000);
    }

    /// <summary>Asks for the endings of <paramref name="items"/>, several per call. What the model leaves out is asked again in batches of <see cref="RetryBatch"/> and then one at a time (as classify does); a call that fails after the first is logged and its dramas stay unanswered. Answers for ids that were not asked are ignored; a drama whose answer has no usable object is unanswered.</summary>
    public static async Task<(Dictionary<string, JsonObject?> Got, List<Item> Missing, int Calls)> AskAsync(IChat chat, string model, List<Item> items, CancellationToken ct)
    {
        var got = new Dictionary<string, JsonObject?>(StringComparer.Ordinal);
        var remaining = items.ToList(); int last = int.MaxValue, calls = 0; bool first = true;
        foreach (var size in new[] { Batch, RetryBatch, 1 })
        {
            if (remaining.Count == 0) break;
            if (!first && size >= last) continue;
            for (int i = 0; i < remaining.Count; i += size)
            {
                var part = remaining.Skip(i).Take(size).ToList();
                try
                {
                    calls++;
                    var res = await chat.JsonAsync(model, JsonValue.Create(Prompt(part))!, "submit_endings", ct);
                    foreach (var o in ((res["dramas"] as JsonArray) ?? []).OfType<JsonObject>())
                        if (Describe.Txt(o["id"]) is { } id && part.Any(p => p.Id == id) && o["ending"] is JsonObject)
                            got.TryAdd(id, Describe.CleanEnding(o["ending"]));
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception e) { Log.Info($"ending backfill: a call for {part.Count} drama(s) failed ({AiJson.Short(e.Message)})"); }
            }
            remaining = items.Where(e => !got.ContainsKey(e.Id)).ToList(); last = size; first = false;
            if (remaining.Count > 0) Log.Info($"ending backfill: no answer for {remaining.Count} of {items.Count} drama(s) (e.g. {remaining[0].Id}){(size > 1 ? "; asking again in smaller batches" : "")}");
        }
        return (got, remaining, calls);
    }

    sealed record Loaded(string Id, string Blob, string ETag, IReadOnlyDictionary<string, string> Meta, List<(string Name, byte[] Data)> Members, JsonObject Describe);

    /// <summary>Reads a drama's describe.zip whole (one ETag-pinned view: the directory and every member come from that one version). Null when it is gone, unreadable, or already has an "ending" key.</summary>
    static async Task<Loaded?> LoadAsync(IStore raw, string blob, string id, CancellationToken ct)
    {
        var head = await ZipOpen.HeadReadableAsync(raw, blob, ct, mustBeZip: false);
        if (head?.ETag == null || head.Size < ZipOpen.MinZip) return null;
        IZipSource src = new BlobZipSource(raw, blob, head.Size, head.ETag, head.Metadata.GetValueOrDefault("idx_sha256"));
        try
        {
            var entries = (await ZipFormat.ReadDirectoryAsync(src, ct)).Where(e => !e.IsDirectory).ToList();
            var members = new List<(string, byte[])>();
            foreach (var e in entries) members.Add((e.Name, await ZipFormat.ReadMemberAsync(src, e, ct)));
            if (members.FirstOrDefault(m => m.Item1 == "describe.json").Item2 is not { } db || JsonNode.Parse(db) is not JsonObject dj) return null;
            return new Loaded(id, blob, head.ETag, head.Metadata, members, dj);
        }
        catch (Exception e) when (e is StaleZipException or InvalidDataException or JsonException or FileNotFoundException) { Log.Info($"ending backfill {id}: describe.zip unreadable ({e.Message}); skipped"); return null; }
    }

    /// <summary>Writes the new describe.zip: the same members in the same order with describe.json replaced, the metadata kept (new idx_sha256 and idx_at, the other facts as they were), If-Match on the ETag the zip was read at. False when the zip changed meanwhile (nothing written).</summary>
    public static async Task<bool> WriteAsync(IStore raw, string blob, string etag, IReadOnlyDictionary<string, string> oldMeta, List<(string Name, byte[] Data)> members, JsonObject describe, string workDir, string nodeId, CancellationToken ct)
    {
        Directory.CreateDirectory(workDir);
        var zip = Path.Combine(workDir, Guid.NewGuid().ToString("N")[..8] + ".zip");
        try
        {
            var newDescribe = JsonSerializer.SerializeToUtf8Bytes(describe, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
            var list = members.Select(m => m.Name == "describe.json" ? new ZipFormat.Member(m.Name, null, newDescribe) : new ZipFormat.Member(m.Name, null, m.Data)).ToList();
            await Task.Run(() => ZipFormat.Write(zip, list), ct);
            var small = new Dictionary<string, byte[]> { ["describe.json"] = newDescribe };
            var md = new Dictionary<string, string>(oldMeta);   // keeps idx_inputs: the inputs did not change
            var fresh = StageLayout.Metadata(ZipFormat.Sha256Of(zip), oldMeta.GetValueOrDefault("idx_inputs", ""), nodeId, StageLayout.SummaryOf("describe", small));
            foreach (var kv in fresh) if (kv.Key != "idx_inputs") md[kv.Key] = kv.Value;
            return await raw.PutZipIfMatchAsync(blob, zip, md, null, etag, ct);
        }
        finally { try { File.Delete(zip); } catch { } }
    }

    /// <summary>The run. <paramref name="limit"/> (--dry-run N) caps how many dramas are looked at; with <paramref name="dry"/> nothing is sent to the model and nothing is written, the prompt of the first batch is returned in <paramref name="dryPrompt"/>.</summary>
    public static async Task<(Report Report, string? DryPrompt)> RunAsync(IStore store, Paths paths, IChat? chat, string? model, string nodeId, string workDir, int? limit, bool dry, CancellationToken ct)
    {
        store = StageZipStore.Find(store) is StageZipStore ? store : StageZipStore.Wrap(store, Path.Combine(workDir, "cache"));   // reads of info.json, the text track ... go through the zips
        var raw = ((StageZipStore)StageZipStore.Find(store)!).Inner;
        int found = 0, skipped = 0, raced = 0, written = 0, calls = 0;
        var todo = new List<Loaded>(); var items = new List<Item>();
        var names = (await raw.ListAsync(paths.Prefix + "videos/", false, false, ct)).Where(b => b.Name.EndsWith("/describe.zip", StringComparison.Ordinal)).Select(b => b.Name).Order(StringComparer.Ordinal).ToList();
        foreach (var blob in names)
        {
            if (limit is { } n && items.Count >= n) break;
            var id = blob[(paths.Prefix + "videos/").Length..^"/describe.zip".Length];
            if (id.Contains('/')) continue;
            found++;
            var l = await LoadAsync(raw, blob, id, ct);
            if (l == null || l.Describe.ContainsKey("ending")) { skipped++; continue; }
            var (title, desc, dur, cues) = await TextOfAsync(store, paths, id, ct);
            todo.Add(l); items.Add(new Item(id, title.Length > 0 ? title : Describe.Txt(l.Describe["fullTitle"]) ?? "", desc, Tail(cues, dur), Describe.Txt(l.Describe["synopsis"]) ?? ""));
        }
        string? dryPrompt = items.Count > 0 ? Prompt(items.Take(Batch)) : null;
        if (dry || chat == null || model == null) return (new Report(found, items.Count, 0, skipped, 0, items.Count, 0), dryPrompt);
        var (got, missing, n2) = await AskAsync(chat, model, items, ct); calls = n2;
        foreach (var l in todo)
        {
            if (!got.TryGetValue(l.Id, out var ending)) continue;
            l.Describe["ending"] = ending ?? new JsonObject { ["kind"] = null, ["signs"] = new JsonArray(), ["nextPart"] = null, ["nextPartHint"] = null };
            l.Describe["endingBy"] = new JsonObject { ["promptVersion"] = Describe.PromptVersion, ["model"] = model, ["at"] = Protocol.NowUtc(), ["backfill"] = true };
            if (await WriteAsync(raw, l.Blob, l.ETag, l.Meta, l.Members, l.Describe, workDir, nodeId, ct)) written++;
            else { raced++; Log.Info($"ending backfill {l.Id}: describe.zip changed while we asked; left as it is (run again)"); }
        }
        return (new Report(found, items.Count, written, skipped, raced, missing.Count, calls), dryPrompt);
    }

    /// <summary>Title, description, duration and the merged text track of a drama, read through the zip-aware store (info.json, description.txt, ocr and asr cues).</summary>
    static async Task<(string Title, string Desc, double Duration, List<ShapeCue> Cues)> TextOfAsync(IStore store, Paths paths, string id, CancellationToken ct)
    {
        var src = new StoreJob(store, paths, id);
        async Task<JsonObject?> J(string f) { try { return await src.ReadAsync(f, ct) is { } b ? JsonNode.Parse(b) as JsonObject : null; } catch (Exception e) when (e is JsonException) { return null; } }
        var info = await J("info.json"); var dl = await J("downloaded.json");
        double dur = dl?["durationSeconds"]?.GetValue<double>() ?? info?["duration"]?.GetValue<double>() ?? 0;
        var ocr = DescribeJob.TextCues(await src.ReadAsync("ocr/subs.zh.vtt", ct)); var asr = DescribeJob.TextCues(await src.ReadAsync("asr/" + Transcribe.ModelName + ".zh.vtt", ct));
        var desc = await src.ReadAsync("description.txt", ct) is { } db ? Encoding.UTF8.GetString(db) : info?["description"]?.GetValue<string>() ?? "";
        return (info?["title"]?.GetValue<string>() ?? "", desc, dur, TextTrack.Merge(ocr, asr));
    }
}
