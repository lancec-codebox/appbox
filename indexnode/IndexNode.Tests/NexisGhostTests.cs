using Azure;
using Nexis.Azure.Utilities;
using Xunit;

namespace IndexNode.Tests;

/// <summary>A blob that behaves like Nexis's: committed bytes, tags, uncommitted blocks, snapshots, and the dehydrate sequence (snapshot; commit empty list with state=transitioning and the snapshot id; stage blocks; ghost) that a test can interleave with a reader.</summary>
public sealed class NexisFakeBlob(string name, byte[] data) : IGhostBlob
{
    public string Name => name;
    public byte[] Committed = data;
    public int ETagN = 1;
    public Dictionary<string, string> Tags = new() { ["ghostd_state"] = "active", ["ghostd_size"] = data.Length.ToString(), ["owner"] = "index" };
    public Dictionary<string, byte[]> Snapshots = new();
    public byte[]? Uncommitted;
    public int Commits;
    public Action? BeforeWrite;      // runs just before a conditional write (the ghost commit): lets a test interleave Nexis

    string ETag => "e" + ETagN;
    public Task<GhostState?> ReadAsync(CancellationToken ct) => Task.FromResult<GhostState?>(new GhostState(Committed.Length, ETag, new Dictionary<string, string>(Tags)));

    public Task<long?> SnapshotLengthAsync(string snapshot, CancellationToken ct) => Task.FromResult<long?>(Snapshots.TryGetValue(snapshot, out var d) ? d.Length : null);

    public Task CommitGhostAsync(GhostState s, CancellationToken ct)
    {
        BeforeWrite?.Invoke(); BeforeWrite = null;
        if (s.ETag != ETag) throw new RequestFailedException(412, "The condition specified using HTTP conditional header(s) is not met.", "ConditionNotMet", null);
        var blocks = Uncommitted ?? [];
        if (blocks.Length.ToString() != Tags.GetValueOrDefault("ghostd_size", "0")) throw new InvalidOperationException($"actual size ({blocks.Length}) != expected size ({Tags.GetValueOrDefault("ghostd_size", "0")})");
        Committed = blocks; Uncommitted = null; Tags["ghostd_state"] = "active"; ETagN++; Commits++; return Task.CompletedTask;
    }

    // ---- Nexis's DehydrateFilesOperation, step by step ----
    public string NexisSnapshot() { var id = "snap" + (Snapshots.Count + 1); Snapshots[id] = Committed; return id; }
    public void NexisCommitEmptyTransitioning(string snap) { Committed = []; Tags["ghostd_state"] = "transitioning"; Tags["ghostd_snapshot"] = snap; Tags["ghostd_size"] = Snapshots[snap].Length.ToString(); ETagN++; }
    public void NexisStageBlocksFromSnapshot(string snap) => Uncommitted = Snapshots[snap];
    public void NexisGhost() { Tags["ghostd_state"] = "ghost"; Tags["ghostd_block_prefix"] = "p"; Tags.Remove("ghostd_snapshot"); ETagN++; }
}

public class NexisGhostTests
{
    static byte[] Data(int n) => Enumerable.Range(0, n).Select(i => (byte)(i % 251)).ToArray();

    [Fact]
    public async Task A_transitioning_blob_is_read_from_its_snapshot_and_the_base_blob_is_not_written()
    {
        var b = new NexisFakeBlob("v", Data(5000)); var snap = b.NexisSnapshot(); b.NexisCommitEmptyTransitioning(snap);
        var tagsBefore = new Dictionary<string, string>(b.Tags); var etag = b.ETagN;
        var r = await NexisAccess.EnsureReadableAsync(b, default);
        Assert.Equal(snap, r.Snapshot);
        Assert.Empty(b.Committed); Assert.Equal(etag, b.ETagN); Assert.Equal(tagsBefore, b.Tags); Assert.Equal(0, b.Commits);   // nothing written: Nexis finishes the ghosting on its own
    }

    [Fact]
    public async Task A_snapshot_with_the_wrong_length_is_refused_naming_blob_and_state()
    {
        var b = new NexisFakeBlob("v/video.mp4", Data(5000)); var snap = b.NexisSnapshot(); b.NexisCommitEmptyTransitioning(snap);
        b.Snapshots[snap] = Data(100);
        var e = await Assert.ThrowsAsync<PermanentFailure>(() => NexisAccess.EnsureReadableAsync(b, default));
        Assert.Contains("v/video.mp4", e.Message); Assert.Contains("transitioning", e.Message); Assert.Contains("100", e.Message);
    }

    [Fact]
    public async Task A_transitioning_blob_whose_snapshot_is_gone_is_treated_as_a_ghost_and_its_blocks_committed()
    {
        var b = new NexisFakeBlob("v", Data(3000)); var snap = b.NexisSnapshot(); b.NexisCommitEmptyTransitioning(snap); b.NexisStageBlocksFromSnapshot(snap);
        b.Snapshots.Clear();
        var r = await NexisAccess.EnsureReadableAsync(b, default);
        Assert.Null(r.Snapshot); Assert.True(r.Repaired);
        Assert.Equal(Data(3000), b.Committed); Assert.Equal(1, b.Commits);
    }

    [Fact]
    public async Task A_plain_ghost_is_hydrated_and_an_active_blob_is_left_alone()
    {
        var b = new NexisFakeBlob("v", Data(4000)); var snap = b.NexisSnapshot(); b.NexisCommitEmptyTransitioning(snap); b.NexisStageBlocksFromSnapshot(snap); b.NexisGhost();
        Assert.True((await NexisAccess.EnsureReadableAsync(b, default)).Repaired); Assert.Equal(Data(4000), b.Committed);
        var again = await NexisAccess.EnsureReadableAsync(b, default); Assert.False(again.Repaired); Assert.Null(again.Snapshot); Assert.Equal(1, b.Commits);
    }

    [Fact]
    public async Task Nexis_finishing_between_our_read_and_our_ghost_commit_is_a_lost_race_and_we_look_again()
    {
        var b = new NexisFakeBlob("v", Data(2000)); var snap = b.NexisSnapshot(); b.NexisCommitEmptyTransitioning(snap); b.NexisStageBlocksFromSnapshot(snap); b.NexisGhost();
        b.BeforeWrite = () => { b.Committed = Data(2000); b.Tags["ghostd_state"] = "active"; b.ETagN++; };   // another node hydrated it first
        var r = await NexisAccess.EnsureReadableAsync(b, default);                      // our conditional commit lost the ETag race; the re-read saw an active blob
        Assert.Null(r.Snapshot); Assert.Equal(Data(2000), b.Committed); Assert.Equal(0, b.Commits);
    }

    [Fact]
    public async Task A_reader_walking_through_Nexis_sequence_never_gets_an_unreadable_blob()
    {
        var b = new NexisFakeBlob("v", Data(2500)); var snap = b.NexisSnapshot();
        var r0 = await NexisAccess.EnsureReadableAsync(b, default); Assert.Null(r0.Snapshot);          // before: active, readable as is
        b.NexisCommitEmptyTransitioning(snap);
        var r1 = await NexisAccess.EnsureReadableAsync(b, default); Assert.Equal(snap, r1.Snapshot);    // empty list committed: read the snapshot
        b.NexisStageBlocksFromSnapshot(snap);
        var r2 = await NexisAccess.EnsureReadableAsync(b, default); Assert.Equal(snap, r2.Snapshot);    // blocks staged, still transitioning
        b.NexisGhost();
        var r3 = await NexisAccess.EnsureReadableAsync(b, default); Assert.Null(r3.Snapshot); Assert.True(r3.Repaired); Assert.Equal(Data(2500), b.Committed);   // ghost: hydrated
    }

    [Fact]
    public async Task A_blob_with_the_wrong_committed_length_or_no_way_back_fails_without_a_retry_naming_blob_and_state()
    {
        var wrong = new NexisFakeBlob("v/video.mp4", Data(100)); wrong.Committed = Data(40);   // active, but 40 of 100 bytes
        var e = await Assert.ThrowsAsync<PermanentFailure>(() => NexisAccess.EnsureReadableAsync(wrong, default));
        Assert.Contains("v/video.mp4", e.Message); Assert.Contains("active", e.Message); Assert.Contains("40", e.Message);

        var empty = new NexisFakeBlob("v/x.mp4", []); empty.Tags["ghostd_state"] = "active";   // 0 bytes, nothing recorded
        var e2 = await Assert.ThrowsAsync<PermanentFailure>(() => NexisAccess.EnsureReadableAsync(empty, default));
        Assert.Contains("v/x.mp4", e2.Message);

        var short_ = new NexisFakeBlob("v/y.mp4", Data(500)); var snap = short_.NexisSnapshot(); short_.NexisCommitEmptyTransitioning(snap); short_.NexisStageBlocksFromSnapshot(snap); short_.NexisGhost();
        short_.Uncommitted = Data(10);   // blocks do not add up to ghostd_size
        var e3 = await Assert.ThrowsAsync<PermanentFailure>(() => NexisAccess.EnsureReadableAsync(short_, default));
        Assert.Contains("ghost", e3.Message); Assert.Contains("v/y.mp4", e3.Message);
    }
}
