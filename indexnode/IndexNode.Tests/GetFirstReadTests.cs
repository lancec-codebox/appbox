using System.Net;
using System.Text;
using Xunit;

namespace IndexNode.Tests;

/// <summary>0.13.32: the GET-first whole read and the read-path split on a simulated Nexis (op counts are HTTP requests; the hydrate commit is the fake's, not an HTTP op).</summary>
public class GetFirstReadTests
{
    const string Sas = "https://acct.blob.core.windows.net/c?sv=2024-01-01&sp=racwdl&sig=SECRETSIG";
    const string Name = "index/videos/j/cast.zip";
    static byte[] Data(int n) => Enumerable.Range(0, n).Select(i => (byte)(i % 251)).ToArray();

    sealed class Rig
    {
        public NexisFakeBlob Blob; public List<string> Requests = []; public RestStore Store;
        public static readonly DateTimeOffset ServerNow = new(2031, 5, 6, 7, 8, 9, TimeSpan.Zero); public string LastPutBody = ""; public int Puts;
        public Action? AfterGet, BeforeTags, AfterTagsGet;
        public Rig(int n = 3000, string state = "active")
        {
            Blob = new NexisFakeBlob(Name, Data(n));
            if (state is "transitioning" or "ghost") { var s = Blob.NexisSnapshot(); Blob.NexisCommitEmptyTransitioning(s); if (state == "ghost") { Blob.NexisStageBlocksFromSnapshot(s); Blob.NexisGhost(); } }
            Store = new RestStore(new HttpClient(new H(this)), Sas) { RetryDelay = _ => TimeSpan.Zero, GhostBlobFor = _ => Blob };
        }
        sealed class H(Rig r) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
            {
                var q = req.RequestUri!.Query; var b = r.Blob;
                if (q.Contains("comp=tags"))
                {
                    if (req.Method == HttpMethod.Put)
                    {
                        r.Requests.Add("PUT tags"); r.LastPutBody = req.Content!.ReadAsStringAsync().Result;
                        var cond = req.Headers.TryGetValues("x-ms-if-tags", out var c) ? c.First() : null;
                        if (cond != null && !(b.Tags.TryGetValue("ghostd_state", out var cs) && cond.Contains("'" + cs + "'"))) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.PreconditionFailed));
                        b.Tags["ghostd_last_access"] = System.Xml.Linq.XDocument.Parse(r.LastPutBody).Descendants("Tag").First(x => x.Element("Key")!.Value == "ghostd_last_access").Element("Value")!.Value; r.Puts++; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
                    }
                    r.Requests.Add("GET tags"); r.BeforeTags?.Invoke(); r.BeforeTags = null;
                    var xml = "<?xml version=\"1.0\"?><Tags><TagSet>" + string.Concat(b.Tags.Select(t => $"<Tag><Key>{t.Key}</Key><Value>{t.Value}</Value></Tag>")) + "</TagSet></Tags>";
                    var tr = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(xml, Encoding.UTF8, "application/xml") }; tr.Headers.Date = ServerNow; r.AfterTagsGet?.Invoke(); return Task.FromResult(tr);
                }
                byte[] data = b.Committed; bool snap = q.Contains("snapshot=");
                if (snap) { var id = Uri.UnescapeDataString(q[(q.IndexOf("snapshot=") + 9)..].Split('&')[0]); if (!b.Snapshots.TryGetValue(id, out var d)) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)); data = d; }
                r.Requests.Add(req.Method + (req.Headers.Range != null ? " range" : "") + (snap ? " snapshot" : ""));
                var etag = new System.Net.Http.Headers.EntityTagHeaderValue("\"e" + b.ETagN + "\"");
                if (req.Headers.TryGetValues("If-Match", out var im) && im.First() != etag.Tag) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.PreconditionFailed));
                HttpResponseMessage resp;
                if (req.Method == HttpMethod.Head) { resp = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([]), Headers = { ETag = etag } }; resp.Content.Headers.ContentLength = data.Length; resp.Headers.TryAddWithoutValidation("x-ms-tag-count", b.Tags.Count.ToString()); }
                else if (req.Headers.Range is { } rg) { int f = (int)rg.Ranges.First().From!, t = (int)rg.Ranges.First().To!; if (f >= data.Length) resp = new HttpResponseMessage(HttpStatusCode.RequestedRangeNotSatisfiable); else { resp = new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = new ByteArrayContent(data.Skip(f).Take(t - f + 1).ToArray()), Headers = { ETag = etag } }; resp.Content.Headers.ContentRange = new System.Net.Http.Headers.ContentRangeHeaderValue(f, Math.Min(t, data.Length - 1), data.Length); } }
                else resp = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(data), Headers = { ETag = etag } };
                if (req.Method == HttpMethod.Get && req.Headers.Range == null) { var a = r.AfterGet; r.AfterGet = null; a?.Invoke(); }
                return Task.FromResult(resp);
            }
        }
    }

    static string Tmp() => Path.Combine(Path.GetTempPath(), "gf-" + Guid.NewGuid().ToString("N")[..8]);

    [Fact]
    public async Task An_active_read_is_tags_touch_GET_three_ops_and_a_repeat_within_5_minutes_is_the_GET_alone()
    {
        var rig = new Rig(); var p = Tmp();
        var props = await rig.Store.DownloadToFileWithPropsAsync(Name, p, default);
        Assert.Equal(Data(3000), File.ReadAllBytes(p)); Assert.Equal(3000, props!.Size); Assert.NotNull(props.ETag);   // size and ETag come from the GET: no HEAD
        Assert.Equal(["GET tags", "PUT tags", "GET"], rig.Requests); Assert.Equal(Rig.ServerNow.UtcDateTime.ToString("o"), rig.Blob.Tags["ghostd_last_access"]);
        rig.Requests.Clear(); await rig.Store.DownloadToFileWithPropsAsync(Name, p, default);
        Assert.Equal(["GET"], rig.Requests);
    }

    [Fact]
    public async Task A_ghost_is_hydrated_then_touched_and_read()
    {
        var rig = new Rig(state: "ghost"); var p = Tmp();
        var props = await rig.Store.DownloadToFileWithPropsAsync(Name, p, default);
        Assert.Equal(Data(3000), File.ReadAllBytes(p)); Assert.Equal(3000, props!.Size);
        Assert.Equal(1, rig.Blob.Commits); Assert.Equal(["GET tags", "PUT tags", "GET"], rig.Requests);   // tags, touch under ghost, hydrate (the fake's commit), one GET
    }

    [Fact]
    public async Task A_transitioning_blob_with_a_snapshot_is_read_from_the_snapshot_and_nothing_is_written()
    {
        var rig = new Rig(state: "transitioning"); var p = Tmp(); var etag = rig.Blob.ETagN;
        await rig.Store.DownloadToFileWithPropsAsync(Name, p, default);
        Assert.Equal(Data(3000), File.ReadAllBytes(p));
        Assert.Equal(["GET tags", "PUT tags", "GET snapshot"], rig.Requests); Assert.Equal(0, rig.Blob.Commits); Assert.Equal(etag, rig.Blob.ETagN);
    }

    [Fact]
    public async Task An_untagged_blob_gets_exactly_ghostd_last_access_added_and_a_tagged_one_keeps_every_tag()
    {
        var rig = new Rig(); rig.Blob.Tags.Clear(); var p = Tmp();
        await rig.Store.DownloadToFileWithPropsAsync(Name, p, default);
        Assert.Equal(["ghostd_last_access"], rig.Blob.Tags.Keys.ToArray()); Assert.Equal(Rig.ServerNow.UtcDateTime.ToString("o"), rig.Blob.Tags["ghostd_last_access"]);
        Assert.Equal(["GET tags", "PUT tags", "GET"], rig.Requests);
        var tagged = new Rig(); tagged.Blob.Tags["idx_kind"] = "keep"; tagged.Blob.Tags["ghostd_last_access"] = "old"; var before = new Dictionary<string, string>(tagged.Blob.Tags);
        await tagged.Store.DownloadToFileWithPropsAsync(Name, Tmp(), default);
        foreach (var kv in before.Where(k => k.Key != "ghostd_last_access")) Assert.Equal(kv.Value, tagged.Blob.Tags[kv.Key]);
        Assert.Equal(before.Count, tagged.Blob.Tags.Count); Assert.Equal(Rig.ServerNow.UtcDateTime.ToString("o"), tagged.Blob.Tags["ghostd_last_access"]);
    }

    [Fact]
    public async Task Nexis_tagging_the_blob_between_the_tags_read_and_the_touch_makes_the_conditional_touch_fail_and_retry()
    {
        var rig = new Rig(); var p = Tmp(); int seen = 0; rig.Blob.Tags["ghostd_state"] = "active";
        rig.AfterTagsGet = () => { if (++seen == 1) { var s = rig.Blob.NexisSnapshot(); rig.Blob.NexisCommitEmptyTransitioning(s); } };
        await rig.Store.DownloadToFileWithPropsAsync(Name, p, default);
        Assert.Equal(Data(3000), File.ReadAllBytes(p)); Assert.Equal(2, rig.Requests.Count(r => r == "GET tags")); Assert.Equal(2, rig.Requests.Count(r => r == "PUT tags"));
    }

    [Fact]
    public async Task A_touch_that_loses_to_a_transition_between_the_tags_read_and_the_touch_takes_the_snapshot_branch()
    {
        var rig = new Rig(); var p = Tmp(); string? snap = null;
        rig.BeforeTags = () => { };   // first tags read sees active; Nexis then starts the dehydrate before our conditional touch lands
        var inner = rig.Store; var tagsSeen = 0;
        rig.AfterTagsGet = () => { if (++tagsSeen == 1) { snap = rig.Blob.NexisSnapshot(); rig.Blob.NexisCommitEmptyTransitioning(snap); } };
        await inner.DownloadToFileWithPropsAsync(Name, p, default);
        Assert.Equal(Data(3000), File.ReadAllBytes(p));
        Assert.Equal(["GET tags", "PUT tags", "GET tags", "PUT tags", "GET snapshot"], rig.Requests);   // 412 on the first touch: again from the tags, now transitioning: touched under that state, read from the snapshot
    }

    [Fact]
    public async Task The_touch_moves_last_access_so_a_ghosting_round_that_saw_the_old_value_is_blocked()
    {
        var rig = new Rig(); rig.Blob.Tags["ghostd_last_access"] = "old"; var p = Tmp();
        await rig.Store.DownloadToFileWithPropsAsync(Name, p, default);
        Assert.NotEqual("old", rig.Blob.Tags["ghostd_last_access"]);   // Nexis's ghosting commit is conditional on the last-access it saw: it fails for this round
    }

    [Fact]
    public async Task Ghosting_that_lands_after_a_full_data_GET_changes_nothing_for_the_reader()
    {
        var rig = new Rig(); var p = Tmp();
        rig.AfterGet = () => { var s = rig.Blob.NexisSnapshot(); rig.Blob.NexisCommitEmptyTransitioning(s); rig.Blob.NexisStageBlocksFromSnapshot(s); rig.Blob.NexisGhost(); };
        await rig.Store.DownloadToFileWithPropsAsync(Name, p, default);
        Assert.Equal(Data(3000), File.ReadAllBytes(p)); Assert.Equal(["GET tags", "PUT tags", "GET"], rig.Requests);
    }

    [Fact]
    public async Task The_base_emptied_between_the_GET_and_the_tags_read_is_read_correctly()
    {
        var rig = new Rig(); var p = Tmp();
        await rig.Store.DownloadToFileWithPropsAsync(Name, p, default);   // touched: the next read within 5 minutes is the GET alone
        var s0 = rig.Blob.NexisSnapshot(); rig.Blob.NexisCommitEmptyTransitioning(s0);   // transitioning, base empty
        rig.Requests.Clear();
        rig.BeforeTags = () => { var s = rig.Blob.Tags["ghostd_snapshot"]; rig.Blob.NexisStageBlocksFromSnapshot(s); rig.Blob.NexisGhost(); rig.Blob.Snapshots.Remove(s); };   // by the tags read Nexis has finished: ghost
        File.Delete(p); await rig.Store.DownloadToFileWithPropsAsync(Name, p, default);
        Assert.Equal(Data(3000), File.ReadAllBytes(p)); Assert.Equal(1, rig.Blob.Commits); Assert.Equal("GET", rig.Requests[0]); Assert.Equal("GET tags", rig.Requests[1]);
    }

    [Fact]
    public async Task A_transition_that_lands_between_two_range_reads_is_a_412_and_a_restart_reads_cleanly()
    {
        var rig = new Rig(); var first = await rig.Store.GetRangeAsync(Name, 0, 100, null, default);
        var etag = (await rig.Store.HeadAsync(Name, default))!.ETag;
        var s = rig.Blob.NexisSnapshot(); rig.Blob.NexisCommitEmptyTransitioning(s); rig.Blob.NexisStageBlocksFromSnapshot(s); rig.Blob.NexisGhost();   // ghosted: new ETag, base emptied
        var second = await rig.Store.GetRangeAsync(Name, 100, 100, etag, default);
        Assert.True(second.Mismatch || second.Data != null);   // never a torn or empty answer: either a restart signal or the same content
        var restart = await rig.Store.GetRangeAsync(Name, 100, 100, null, default);
        Assert.Equal(Data(3000).Skip(100).Take(100).ToArray(), restart.Data);
        Assert.Equal(Data(3000).Take(100).ToArray(), first.Data);
    }

    [Fact]
    public async Task A_conditional_touch_that_loses_to_a_transition_fails_and_the_state_branch_is_taken()
    {
        var rig = new Rig();
        Assert.True(await NexisAccess.TouchAsync(rig.Store, Name, default)); Assert.True(rig.Blob.Tags.ContainsKey("ghostd_last_access"));   // the refresh of a long-held reader: conditional too
        var s = rig.Blob.NexisSnapshot(); rig.Blob.NexisCommitEmptyTransitioning(s); rig.Blob.Tags.Remove("ghostd_last_access");
        Assert.False(await NexisAccess.TouchAsync(rig.Store, Name, default)); Assert.DoesNotContain("ghostd_last_access", rig.Blob.Tags.Keys);   // not written over the ghoster's tags
        var read = await NexisAccess.PrepareAsync(rig.Store, Name, default);                // the failed touch goes on to the transitioning branch: the snapshot
        Assert.Equal(s, read.Snapshot);
    }
}
