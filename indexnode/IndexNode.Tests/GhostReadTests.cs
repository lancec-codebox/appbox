using System.Net;
using Azure;
using Xunit;

namespace IndexNode.Tests;

/// <summary>Every read of a RestStore hydrates a Nexis ghost first: the blob comes back with its content, not as 0 bytes.</summary>
public class GhostReadTests
{
    const string Sas = "https://acct.blob.core.windows.net/c?sv=2024-01-01&sp=racwdl&sig=SECRETSIG";

    /// <summary>A store whose HTTP side serves what the fake blob holds (0 bytes while it is a ghost) and whose hydrate side is the same fake.</summary>
    sealed class Rig
    {
        public NexisFakeBlob Blob; public List<string> Requests = []; public RestStore Store;
        public Rig(byte[] content, bool ghost = true, Action<NexisFakeBlob>? setup = null)
        {
            Blob = new NexisFakeBlob("index/videos/j/ocr/lines.json", content);
            if (ghost) { var snap = Blob.NexisSnapshot(); Blob.NexisCommitEmptyTransitioning(snap); Blob.NexisStageBlocksFromSnapshot(snap); Blob.NexisGhost(); }
            setup?.Invoke(Blob);
            Store = new RestStore(new HttpClient(new H(this)), Sas) { RetryDelay = _ => TimeSpan.Zero, GhostBlobFor = _ => Blob };
        }
        sealed class H(Rig r) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
            {
                r.Requests.Add(req.Method + " " + req.RequestUri!.AbsolutePath + (req.Headers.Range != null ? " " + req.Headers.Range : ""));
                var data = r.Blob.Committed;
                if (req.Headers.Range is { } rg) { var f = (int)rg.Ranges.First().From!; var t = (int)rg.Ranges.First().To!; var part = data.Skip(f).Take(t - f + 1).ToArray(); return Task.FromResult(new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = new ByteArrayContent(part) }); }
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(data), Headers = { ETag = new System.Net.Http.Headers.EntityTagHeaderValue("\"e" + r.Blob.ETagN + "\"") } });
            }
        }
    }

    static readonly byte[] Json = System.Text.Encoding.UTF8.GetBytes("""{"band":{"width":360},"zh":[]}""");

    [Fact]
    public async Task A_ghost_is_hydrated_then_read_and_the_caller_never_sees_0_bytes()
    {
        var rig = new Rig(Json);
        Assert.Empty(rig.Blob.Committed);
        var got = await rig.Store.GetAsync("index/videos/j/ocr/lines.json", default);
        Assert.Equal(Json, got!.Data); Assert.Equal(1, rig.Blob.Commits); Assert.Equal("active", rig.Blob.Tags["ghostd_state"]);
    }

    [Fact]
    public async Task An_active_blob_is_read_without_any_hydrate()
    {
        var rig = new Rig(Json, ghost: false);
        Assert.Equal(Json, (await rig.Store.GetAsync("index/videos/j/ocr/lines.json", default))!.Data); Assert.Equal(0, rig.Blob.Commits);
    }

    [Fact]
    public async Task A_blob_that_is_really_empty_and_not_a_ghost_comes_back_empty()
    {
        var rig = new Rig([], ghost: false);
        Assert.Empty((await rig.Store.GetAsync("index/videos/j/ocr/lines.json", default))!.Data); Assert.Equal(0, rig.Blob.Commits);
    }

    [Fact]
    public async Task A_ghost_whose_blocks_do_not_add_up_to_its_size_is_refused_and_nothing_is_committed()
    {
        var rig = new Rig(Json, setup: b => b.Uncommitted = Json.Take(10).ToArray());
        var e = await Assert.ThrowsAsync<PermanentFailure>(() => rig.Store.GetAsync("index/videos/j/ocr/lines.json", default));
        Assert.Contains("index/videos/j/ocr/lines.json", e.Message); Assert.Contains("expected", e.Message); Assert.Equal(0, rig.Blob.Commits); Assert.Empty(rig.Blob.Committed);
        Assert.DoesNotContain("SECRETSIG", e.Message);
    }

    [Fact]
    public async Task A_412_means_another_reader_hydrated_first_and_the_read_goes_on_with_the_hydrated_blob()
    {
        var rig = new Rig(Json);
        var data = Json; rig.Blob.BeforeWrite = () => { rig.Blob.Committed = data; rig.Blob.Uncommitted = null; rig.Blob.Tags["ghostd_state"] = "active"; rig.Blob.ETagN++; };   // someone else commits between our look and our write
        var got = await rig.Store.GetAsync("index/videos/j/ocr/lines.json", default);
        Assert.Equal(Json, got!.Data); Assert.Equal(0, rig.Blob.Commits);
    }

    [Fact]
    public async Task A_range_read_of_a_ghost_hydrates_it_first()
    {
        var rig = new Rig(Json);
        var part = await rig.Store.GetRangeAsync("index/videos/j/ocr/lines.json", 2, 5, default);
        Assert.Equal(Json.Skip(2).Take(5).ToArray(), part); Assert.Equal(1, rig.Blob.Commits);
    }

    [Fact]
    public async Task A_conditional_read_and_a_file_download_hydrate_too()
    {
        var rig = new Rig(Json);
        var c = await rig.Store.GetIfChangedAsync("index/videos/j/ocr/lines.json", null, default);
        Assert.Equal(Json, c.Data);
        var rig2 = new Rig(Json); var path = Path.Combine(Path.GetTempPath(), "ghost-" + Guid.NewGuid().ToString("N"));
        await rig2.Store.DownloadToFileAsync("index/videos/j/ocr/lines.json", path, default);
        try { Assert.Equal(Json, File.ReadAllBytes(path)); } finally { File.Delete(path); }
    }

    [Fact]
    public void Diverse_with_fewer_usable_faces_than_candidates_returns_them_without_a_null()
    {
        // 14 faces: 3 solo, 11 not; the quality filter leaves them all, the tiers overlap, and the pool runs dry before 12 are picked
        var faces = Enumerable.Range(0, 14).Select(i => new FaceRec { T = i * 10, Quality = 0.5 + i * 0.01, Other = i < 3 ? 0 : 1, Emb = [1f, i * 0.1f, 0f, 0f] }).ToList();
        var pick = CastLogic.Diverse(faces, 20);
        Assert.Equal(14, pick.Count); Assert.DoesNotContain(null, pick);
        var few = CastLogic.Diverse(faces.Take(2).ToList(), 12); Assert.Equal(2, few.Count);
    }

    [Fact]
    public void An_empty_or_short_faces_f16_is_a_clear_error_not_a_NullReferenceException()
    {
        var doc = System.Text.Json.Nodes.JsonNode.Parse("""{"dim":4,"faces":[{"box":[0,0,1,1],"t":1.0,"score":0.9,"blur":1,"quality":1,"cluster":1}]}""")!.AsObject();
        var e = Assert.Throws<InvalidOperationException>(() => CastCandidates.LoadFaces(doc, [], null, 360, "j/cast/faces.f16"));
        Assert.Contains("j/cast/faces.f16", e.Message); Assert.Contains("0 bytes", e.Message);
        Assert.Throws<InvalidOperationException>(() => CastCandidates.LoadFaces(doc, null, null, 360));
        var s = Assert.Throws<InvalidOperationException>(() => CastCandidates.LoadFaces(doc, new byte[5], null, 360, "j/cast/faces.f16")); Assert.Contains("too short", s.Message);
        Assert.Single(CastCandidates.LoadFaces(doc, new byte[8], null, 360));
    }

    [Fact]
    public void An_empty_stored_json_names_the_blob_not_a_bare_JsonReaderException()
    {
        var e = Assert.Throws<StoredJson.EmptyBlobException>(() => IndexNode.SubtitleBand.Parse([], "j/ocr/lines.json"));
        Assert.Contains("j/ocr/lines.json", e.Message); Assert.Contains("0 bytes", e.Message); Assert.Contains("ghost", e.Message);
        Assert.Throws<StoredJson.EmptyBlobException>(() => StoredJson.Object([], "j/cast/faces.json"));
        Assert.Throws<StoredJson.EmptyBlobException>(() => StoredJson.Doc([], "j/frames/manifest.json"));
        Assert.Throws<StoredJson.EmptyBlobException>(() => StoredJson.Parse(System.Text.Encoding.UTF8.GetBytes("  \n"), "j/x.json"));
        var bad = Assert.Throws<InvalidOperationException>(() => StoredJson.Parse(System.Text.Encoding.UTF8.GetBytes("{oops"), "j/x.json")); Assert.Contains("j/x.json", bad.Message); Assert.Contains("not valid JSON", bad.Message);
        Assert.Throws<InvalidOperationException>(() => StoredJson.Parse(null, "j/x.json"));
        Assert.Null(StoredJson.Parse(System.Text.Encoding.UTF8.GetBytes("null"), "j/x.json"));
    }
}
