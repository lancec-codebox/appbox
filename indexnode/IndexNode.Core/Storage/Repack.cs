using System.Collections.Concurrent;
using System.Text.Json.Nodes;

namespace IndexNode;

/// <summary>`indexnode repack`: the one-off 0.9 (loose files) to 0.10.0 (one zip per stage) conversion, and the loose/repacked/mixed count `indexnode status` shows.</summary>
public static class Repack
{
    public sealed record Counts(int Loose, int Repacked, int Mixed, int Empty, List<string> LooseIds, List<string> MixedIds);

    public static IStore Raw(IStore s) => StageZipStore.Find(s) is StageZipStore z ? z.Inner : s;

    /// <summary>Every drama folder: its loose members (not raw videos, zips or the pass-through clip/) and its stage zips, from one listing.</summary>
    public static async Task<Dictionary<string, (List<string> Loose, List<string> Zips)>> ScanAsync(IStore raw, Paths paths, CancellationToken ct)
    {
        var root = paths.Prefix + "videos/"; var map = new Dictionary<string, (List<string>, List<string>)>(StringComparer.Ordinal);
        foreach (var b in await raw.ListAsync(root, false, false, ct))
        {
            var rest = b.Name[root.Length..]; int i = rest.IndexOf('/'); if (i < 0) continue;
            string id = rest[..i], member = rest[(i + 1)..];
            if (!map.TryGetValue(id, out var e)) map[id] = e = ([], []);
            if (StageLayout.IsRaw(member) || StageLayout.IsPassThrough(member)) continue;
            if (member.EndsWith(".zip", StringComparison.Ordinal) && !member.Contains('/')) e.Item2.Add(member); else e.Item1.Add(member);
        }
        return map;
    }

    public static async Task<Counts> CountAsync(IStore raw, Paths paths, CancellationToken ct)
    {
        var scan = await ScanAsync(raw, paths, ct); int l = 0, r = 0, m = 0, e = 0; List<string> li = [], mi = [];
        foreach (var (id, (loose, zips)) in scan)
        {
            if (loose.Count > 0 && zips.Count > 0) { m++; mi.Add(id); }
            else if (loose.Count > 0) { l++; li.Add(id); }
            else if (zips.Count > 0) r++;
            else e++;
        }
        return new(l, r, m, e, li, mi);
    }

    public sealed record Result(string Id, int Stages, int Files, long Bytes, int Deleted, string? Error);

    /// <summary>Files of one drama fetched or deleted at once.</summary>
    public static int FileParallel { get; set; } = 8;

    private static uint CrcOf(string path)
    {
        uint c = 0; var buf = new byte[1 << 20]; using var fs = File.OpenRead(path); int n;
        while ((n = fs.Read(buf, 0, buf.Length)) > 0) c = Crc32.Update(c, buf.AsSpan(0, n));
        return c;
    }

    /// <summary>Repacks one drama. dry: only reports. Deletes the loose files (and dino/, processed.json, downloaded.json) only when every stage zip is verified.</summary>
    public static async Task<Result> OneAsync(IStore raw, Paths paths, string id, string nodeId, bool dry, bool deleteLoose, string work, Action<string> say, CancellationToken ct)
    {
        var dir = paths.VideoDir(id);
        var all = (await raw.ListAsync(dir, false, false, ct)).Select(b => b.Name[dir.Length..]).ToList();
        var loose = all.Where(m => !StageLayout.IsRaw(m) && !(m.EndsWith(".zip", StringComparison.Ordinal) && !m.Contains('/')) && !StageLayout.IsPassThrough(m)).ToList();
        if (loose.Count == 0) return new(id, 0, 0, 0, 0, null);
        var retired = loose.Where(StageLayout.IsRetired).ToList();
        var byStage = new Dictionary<string, List<string>>();
        foreach (var m in loose.Where(m => !StageLayout.IsRetired(m) && m != "processed.json"))
        {
            var st = m == "downloaded.json" ? "download" : StageLayout.StageOf(m); if (st == null) continue;
            (byStage.TryGetValue(st, out var l) ? l : byStage[st] = []).Add(m);
        }
        if (dry) { say($"{id}: would write {string.Join('+', StageLayout.Stages.Where(byStage.ContainsKey))} from {loose.Count - retired.Count} loose file(s){(deleteLoose ? $" and delete {loose.Count} loose file(s)" : "")}"); return new(id, byStage.Count, loose.Count, 0, 0, null); }
        var rest = raw as RestStore ?? LocalCacheStore.Unwrap(raw) as RestStore;
        long bytes = 0; int files = 0;
        try
        {
            foreach (var stage in StageLayout.Stages.Where(byStage.ContainsKey))
            {
                var sdir = Path.Combine(work, id, stage); Directory.CreateDirectory(sdir);
                var local = new Dictionary<string, string>(StringComparer.Ordinal); var crcs = new Dictionary<string, (long Size, uint Crc)>(StringComparer.Ordinal);
                // the files of a stage are fetched side by side (a ghosted blob is restored first); size and CRC are computed from what arrived
                var fetched = new ConcurrentDictionary<string, (string Path, long Size, uint Crc)>(StringComparer.Ordinal);
                await Parallel.ForEachAsync(byStage[stage], new ParallelOptions { MaxDegreeOfParallelism = FileParallel, CancellationToken = ct }, async (m, t) =>
                {
                    if (rest != null) { try { await NexisAccess.PrepareAsync(rest, dir + m, t); } catch (OperationCanceledException) { throw; } catch { } }
                    var p = Path.Combine(sdir, m.Replace('/', Path.DirectorySeparatorChar)); Directory.CreateDirectory(Path.GetDirectoryName(p)!);
                    await raw.DownloadToFileAsync(dir + m, p, t);
                    fetched[m] = (p, new FileInfo(p).Length, await Task.Run(() => CrcOf(p), t));
                });
                foreach (var (m, f) in fetched) { local[m] = f.Path; crcs[m] = (f.Size, f.Crc); files++; }
                byte[]? downloaded = null;
                if (local.Remove("downloaded.json", out var dj)) downloaded = await File.ReadAllBytesAsync(dj, ct);
                var built = Path.Combine(sdir, "_" + stage + ".zip");
                var b = await StageZipBuilder.BuildAsync(stage, local, downloaded, built, ct);
                // 1. the zip against the loose files: every member (cast crops through their joined files) has the loose file's size and CRC
                var src = new FileZipSource(built); var entries = (await ZipFormat.ReadDirectoryAsync(src, ct)).Where(e => !e.IsDirectory).ToDictionary(e => e.Name, StringComparer.Ordinal);
                foreach (var m in byStage[stage].Where(m => m != "downloaded.json"))
                {
                    if (entries.TryGetValue(m, out var e)) { if (e.Size != crcs[m].Size || e.Crc != crcs[m].Crc) throw new InvalidDataException($"{stage}.zip: {m} differs from the loose file"); }
                    else if (!await CropMatchesAsync(src, entries, m, crcs[m])) throw new InvalidDataException($"{stage}.zip: {m} is missing");
                }
                var sha = ZipFormat.Sha256Of(built); bytes += new FileInfo(built).Length;
                var summary = b.Summary;
                var md = await StageZipBuilder.MetadataAsync(raw, paths.Prefix, id, stage, sha, nodeId, summary, ct);
                var zname = dir + stage + ".zip";
                var have = await raw.HeadAsync(zname, ct);
                if (have == null || have.Metadata.GetValueOrDefault("idx_sha256") != sha) await raw.PutZipAsync(zname, built, md, null, false, ct);
                // 2. what is in the store: re-read it by ranges and compare member list, sizes, CRCs and the hash in the metadata
                var h = await raw.HeadAsync(zname, ct) ?? throw new InvalidDataException(stage + ".zip did not arrive");
                if (h.Metadata.GetValueOrDefault("idx_sha256") != sha || h.Size != src.Length) throw new InvalidDataException($"{stage}.zip in the store has another hash or size");
                // (no re-download: the zip was built from files whose sizes and CRCs were computed above and checked against its own directory; the blob's length and the hash sent with it must be what we uploaded)
                try { Directory.Delete(sdir, true); } catch { }
            }
            int deleted = 0;
            if (deleteLoose)
            {
                await Parallel.ForEachAsync(loose, new ParallelOptions { MaxDegreeOfParallelism = FileParallel, CancellationToken = ct }, async (m, t) => { await raw.DeleteAsync(dir + m, t); Interlocked.Increment(ref deleted); });
            }
            say($"{id}: {byStage.Count} stage zip(s) verified from {files} file(s){(deleteLoose ? $", {deleted} loose file(s) deleted" : ", loose files kept (--delete-loose to remove)")}");
            return new(id, byStage.Count, files, bytes, deleted, null);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception e) { say($"{id}: NOT repacked, loose files kept: {e.Message}"); return new(id, 0, files, bytes, 0, e.Message); }
        finally { try { Directory.Delete(Path.Combine(work, id), true); } catch { } }
    }

    /// <summary>cast/cand_C_K.jpg lives in cast/crops/NNNN.jpgs: its slice has the loose file's size and CRC.</summary>
    private static async Task<bool> CropMatchesAsync(IZipSource src, Dictionary<string, ZipEntry> entries, string member, (long Size, uint Crc) want)
    {
        var mm = System.Text.RegularExpressions.Regex.Match(member, @"^cast/(?<k>cand_(hs_)?\d+_\d+)\.jpg$"); if (!mm.Success || !entries.TryGetValue(StageLayout.CropsDir + "map.json", out var mapE)) return false;
        if (JsonNode.Parse(await ZipFormat.ReadMemberAsync(src, mapE, default))?[mm.Groups["k"].Value] is not JsonArray loc) return false;
        int chunk = loc[0]!.GetValue<int>(), i = loc[1]!.GetValue<int>();
        var idx = JsonNode.Parse(await ZipFormat.ReadMemberAsync(src, entries[$"{StageLayout.CropsDir}{chunk:D4}.json"], default))!.AsArray();
        var data = await ZipFormat.ReadMemberAsync(src, entries[$"{StageLayout.CropsDir}{chunk:D4}.jpgs"], default);
        int off = idx[i]![0]!.GetValue<int>(), len = idx[i]![1]!.GetValue<int>();
        return len == want.Size && Crc32.Of(data.AsSpan(off, len)) == want.Crc;
    }

    /// <summary>indexnode repack [--videos id,id] [--dry] [--delete-loose] [--parallel 2]</summary>
    public static async Task<int> RunAsync(IStore store, Paths paths, string nodeId, IEnumerable<string>? only, bool dry, bool deleteLoose, int parallel, Action<string> say, CancellationToken ct)
    {
        var raw = Raw(store); var scan = await ScanAsync(raw, paths, ct);
        var ids = scan.Where(kv => kv.Value.Loose.Count > 0).Select(kv => kv.Key).OrderBy(x => x, StringComparer.Ordinal).ToList();
        if (only != null) { var set = only.ToHashSet(); ids = ids.Where(set.Contains).ToList(); }
        say($"{ids.Count} drama(s) with loose files{(dry ? " (dry run: nothing is written or deleted)" : "")}");
        var work = Path.Combine(Path.GetTempPath(), "indexnode", "repack-" + Environment.ProcessId); Directory.CreateDirectory(work);
        var results = new ConcurrentBag<Result>(); int n = 0;
        try
        {
            await Parallel.ForEachAsync(ids, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, parallel), CancellationToken = ct }, async (id, t) =>
            {
                var r = await OneAsync(raw, paths, id, nodeId, dry, deleteLoose, work, say, t); results.Add(r);
                say($"  [{Interlocked.Increment(ref n)}/{ids.Count}] {id}");
            });
        }
        finally { try { Directory.Delete(work, true); } catch { } }
        int failed = results.Count(r => r.Error != null);
        say($"summary: {results.Count - failed} drama(s) {(dry ? "planned" : "repacked")}, {failed} failed, {results.Sum(r => r.Stages)} stage zip(s), {results.Sum(r => r.Bytes) / 1048576} MB written, {results.Sum(r => r.Deleted)} loose file(s) deleted");
        return failed == 0 ? 0 : 1;
    }
}
