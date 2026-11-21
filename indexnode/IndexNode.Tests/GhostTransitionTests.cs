namespace IndexNode.Tests;

/// <summary>0.13.4: a ghost transition (dehydrate or hydrate) changes the ETag, never the bytes, so a reader holding the old ETag must not fail.</summary>
public class GhostTransitionTests
{
    const string Blob = "index/videos/g1/centers.zip";
    static readonly byte[] Bytes = Enumerable.Range(0, 200).Select(i => (byte)i).ToArray();

    static async Task<(FakeStore S, BlobPin Pin)> Setup(string sha = "abc")
    {
        var s = new FakeStore(); await s.PutAsync(Blob, Bytes, null, null, false, default);
        await s.SetMetadataAsync(Blob, new Dictionary<string, string> { ["idx_sha256"] = sha }, null, default);
        var h = (await s.HeadAsync(Blob, default))!;
        return (s, new BlobPin(Blob, h.ETag, h.Size, sha));
    }

    [Fact]
    public async Task A_dehydrated_blob_is_read_with_the_same_bytes_after_the_ETag_changed()
    {
        var (s, pin) = await Setup();
        Assert.Equal(Bytes[10..30], (await GhostSafeRead.RangeAsync(s, pin, 10, 20, default)).Data);
        s.Dehydrate(Blob);
        var r = await GhostSafeRead.RangeAsync(s, pin, 50, 40, default);
        Assert.False(r.Mismatch); Assert.Equal(Bytes[50..90], r.Data);   // 0.13.8: the store, not the pin, follows the new ETag
        Assert.Equal(Bytes[0..5], (await GhostSafeRead.RangeAsync(s, pin, 0, 5, default)).Data);   // the pin follows the new ETag
    }

    [Fact]
    public async Task A_hydrate_after_the_ETag_was_taken_is_read_with_the_same_bytes()
    {
        var (s, pin) = await Setup(); s.Dehydrate(Blob);
        var h = (await s.HeadAsync(Blob, default))!; var ghostPin = new BlobPin(Blob, h.ETag, Bytes.Length, "abc");   // a reader that opened it while it was a ghost
        await s.GetAsync(Blob, default);   // hydrated by someone else
        var r = await GhostSafeRead.RangeAsync(s, ghostPin, 100, 10, default);
        Assert.False(r.Mismatch); Assert.Equal(Bytes[100..110], r.Data);
    }

    [Fact]
    public async Task A_real_delete_still_fails()
    {
        var (s, pin) = await Setup(); s.Dehydrate(Blob); await s.DeleteAsync(Blob, null, default);
        var r = await GhostSafeRead.RangeAsync(s, pin, 0, 10, default);
        Assert.Null(r.Data); Assert.False(r.Mismatch);   // the caller says "gone"
    }

    [Fact]
    public async Task Changed_content_still_fails()
    {
        var (s, pin) = await Setup("abc");
        var other = Bytes.Reverse().ToArray();   // same length, different bytes and a different idx_sha256
        await s.PutAsync(Blob, other, null, null, false, default);
        await s.SetMetadataAsync(Blob, new Dictionary<string, string> { ["idx_sha256"] = "def" }, null, default);
        Assert.True((await GhostSafeRead.RangeAsync(s, pin, 0, 10, default)).Mismatch);
        // and a different length is changed content too
        var (s2, pin2) = await Setup(); await s2.PutAsync(Blob, Bytes[..150], null, null, false, default);
        Assert.True((await GhostSafeRead.RangeAsync(s2, pin2, 0, 10, default)).Mismatch);
    }

    [Fact]
    public async Task CentersZip_ReadAsync_survives_a_dehydrate()
    {
        var (s, _) = await Setup(); var z = new CentersZip { Store = s, Blob = Blob, ETag = (await s.HeadAsync(Blob, default))!.ETag, Sha = "abc", ZipSize = Bytes.Length, DataStart = 0, DataLength = Bytes.Length };
        s.Dehydrate(Blob);
        Assert.Equal(Bytes[20..40], await z.ReadAsync(20, 20, default));
    }
}
