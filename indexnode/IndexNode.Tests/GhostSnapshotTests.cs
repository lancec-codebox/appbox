using System.IO.Compression;
using System.Net;
using Xunit;

namespace IndexNode.Tests;

/// <summary>0.13.5: a vanished Nexis snapshot is not a missing blob, and a ghost zip is hydrated before it is opened.</summary>
public class GhostSnapshotTests
{
    const string Sas = "https://acct.blob.core.windows.net/c?sv=2024-01-01&sp=racwdl&sig=SECRETSIG";
    const string Name = "index/videos/EG1TeijGKBo/centers.zip";

    static byte[] Zip()
    {
        var ms = new MemoryStream();
        using (var za = new ZipArchive(ms, ZipArchiveMode.Create, true)) { using var w = new StreamWriter(za.CreateEntry("downloaded.json").Open()); w.Write("{\"durationSeconds\":42}"); }
        return ms.ToArray();
    }

    sealed class Rig
    {
        public NexisFakeBlob Blob; public RestStore Store; public List<string> Requests = []; bool deleteOnFirstRead = true;
        public Rig(byte[] content)
        {
            Blob = new NexisFakeBlob(Name, content);
            var snap = Blob.NexisSnapshot(); Blob.NexisCommitEmptyTransitioning(snap);   // Nexis is mid-dehydrate: the base blob is empty, the content is in the snapshot
            Store = new RestStore(new HttpClient(new H(this)), Sas) { RetryDelay = _ => TimeSpan.Zero, GhostBlobFor = _ => Blob };
            Store.ReadSnapshots[Name] = snap;   // what NexisAccess.PrepareAsync did when the reader opened the blob mid-transition
        }
        sealed class H(Rig r) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
            {
                var q = req.RequestUri!.Query; r.Requests.Add(req.Method + " " + (q.Contains("snapshot=") ? "snapshot" : "base"));
                byte[] data;
                if (q.Contains("snapshot="))
                {
                    var id = Uri.UnescapeDataString(q[(q.IndexOf("snapshot=") + 9)..].Split('&')[0]);
                    if (!r.Blob.Snapshots.TryGetValue(id, out var d)) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
                    if (r.deleteOnFirstRead)
                    {   // the transition finishes (the blocks are staged, the blob is a ghost) and Nexis deletes its ephemeral snapshot: this read finds it gone
                        r.deleteOnFirstRead = false; r.Blob.NexisStageBlocksFromSnapshot(id); r.Blob.NexisGhost(); r.Blob.Snapshots.Remove(id);
                        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
                    }
                    data = d;
                }
                else data = r.Blob.Committed;
                var etag = new System.Net.Http.Headers.EntityTagHeaderValue("\"e" + r.Blob.ETagN + "\"");
                if (req.Headers.Range is { } rg) { int f = (int)rg.Ranges.First().From!, t = (int)rg.Ranges.First().To!; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = new ByteArrayContent(data.Skip(f).Take(t - f + 1).ToArray()), Headers = { ETag = etag } }); }
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(data), Headers = { ETag = etag } });
            }
        }
    }

    [Fact]
    public async Task A_snapshot_that_disappears_while_the_transition_finishes_is_not_a_missing_blob()
    {
        var zip = Zip(); var rig = new Rig(zip);
        var got = await rig.Store.GetRangeAsync(Name, 0, 30, null, default);   // goes to the snapshot, finds it gone, waits for the ghost, hydrates, reads the base blob
        Assert.True(got.Data != null, string.Join(",", rig.Requests) + " mismatch=" + got.Mismatch + " commits=" + rig.Blob.Commits + " state=" + rig.Blob.Tags["ghostd_state"]);
        Assert.False(got.Mismatch); Assert.Equal(zip[..30], got.Data);
        Assert.Contains("GET snapshot", rig.Requests); Assert.Equal(1, rig.Blob.Commits); Assert.Equal("active", rig.Blob.Tags["ghostd_state"]);
        Assert.Equal(zip, (await rig.Store.GetAsync(Name, default))!.Data);   // and the next whole read is the hydrated blob
    }

    static async Task<FakeStore> Ghosted(string name, byte[] zip, bool tags)
    {
        var s = new FakeStore(); await s.PutAsync(name, zip, null, null, false, default);
        await s.SetMetadataAsync(name, new Dictionary<string, string> { ["idx_sha256"] = "abc" }, null, default);
        s.Dehydrate(name);
        if (tags) await s.SetTagsAsync(name, new Dictionary<string, string> { ["ghostd_state"] = "ghost", ["ghostd_size"] = zip.Length.ToString() }, null, default);
        return s;
    }

    [Fact]
    public async Task A_ghost_zip_with_a_recorded_size_is_hydrated_and_opened()
    {
        var zip = Zip(); var s = await Ghosted(Name, zip, true);
        Assert.Equal(0, (await s.HeadAsync(Name, default))!.Size);
        var h = (await ZipOpen.HeadReadableAsync(s, Name, default))!;
        Assert.Equal(zip.Length, h.Size);
        var es = await ZipFormat.ReadDirectoryAsync(new BlobZipSource(s, Name, h.Size, h.ETag), default);
        Assert.Contains(es, e => e.Name == "downloaded.json");
        // the backfill's reader goes through it too
        var paths = new Paths("index/"); var dz = paths.Video("EG1TeijGKBo", "download.zip"); var s2 = await Ghosted(dz, zip, true);
        Assert.Equal(42, (await VideosBackfill.ZipDurationAsync(s2, paths, "EG1TeijGKBo", default)).Duration);
    }

    [Fact]
    public async Task A_zero_byte_blob_with_no_ghost_tags_is_still_not_a_zip()
    {
        var s = new FakeStore(); await s.PutAsync(Name, [], null, null, false, default);
        var e = await Assert.ThrowsAsync<InvalidDataException>(() => ZipOpen.HeadReadableAsync(s, Name, default));
        Assert.Contains("not a zip", e.Message);
        Assert.Null(await ZipOpen.HeadReadableAsync(s, "index/videos/none/centers.zip", default));   // a missing blob is null, not an error
    }
}
