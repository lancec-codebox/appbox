using System.Collections.Concurrent;
using Xunit;

namespace IndexNode.Tests;

/// <summary>An in-memory <see cref="ICacheMirror"/> (Azure Files semantics: a file has a set length; writes land inside it).</summary>
public sealed class FakeMirror : ICacheMirror
{
    public readonly ConcurrentDictionary<string, byte[]> Files = new(StringComparer.Ordinal);
    public int Downloads, Writes;
    public Task<long?> LengthAsync(string rel, CancellationToken ct) => Task.FromResult(Files.TryGetValue(rel, out var b) ? (long?)b.LongLength : null);
    public async Task<bool> DownloadAsync(string rel, string localPath, CancellationToken ct)
    {
        if (!Files.TryGetValue(rel, out var b)) return false;
        Interlocked.Increment(ref Downloads); Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(localPath))!); await File.WriteAllBytesAsync(localPath, b, ct); return true;
    }
    public Task SetLengthAsync(string rel, long length, CancellationToken ct)
    {
        Files.AddOrUpdate(rel, _ => new byte[length], (_, b) => { var n = new byte[length]; Array.Copy(b, n, Math.Min(b.LongLength, length)); return n; });
        return Task.CompletedTask;
    }
    public Task WriteAsync(string rel, long offset, ReadOnlyMemory<byte> data, CancellationToken ct)
    {
        var b = Files[rel]; if (offset + data.Length > b.LongLength) throw new IOException($"write past the end of {rel}");
        data.Span.CopyTo(b.AsSpan((int)offset)); Interlocked.Increment(ref Writes); return Task.CompletedTask;
    }
}

/// <summary>0.13.91: the frame codes copy and the frames cache mirrored to a share, so a restart restores them instead of reading the store.</summary>
public class CacheMirrorTests
{
    static readonly Paths P = new("index/");
    static string Tmp() { var d = Path.Combine(Path.GetTempPath(), "cm-" + Guid.NewGuid().ToString("N")[..8]); Directory.CreateDirectory(d); return d; }

    [Fact]
    public async Task A_restart_restores_the_codes_copy_from_the_mirror_and_reads_nothing_from_the_store()
    {
        var (f, idx, _) = await FrameCodesTests.SeedFor(5);
        Assert.Equal(5, await FrameCodes.FillAsync(f, P, idx, 60, 0, _ => { }, default));
        var m = new FakeMirror(); string a = Tmp(), b = Tmp();
        try
        {
            var first = new FrameCodesCache(a) { RangeBytes = 4096, Streams = 2, Mirror = m };
            Assert.Equal(0, (await first.ViewAsync(f, P, idx, default)).Missing);
            var log = (await f.GetAppendInfoAsync(FrameCodes.Segment(P, idx.MeanVersion, 0), default))!.Length;
            var local = File.ReadAllBytes(Directory.GetFiles(a, "log-0000*.bin", SearchOption.AllDirectories).Single());
            Assert.Equal(log, local.LongLength);
            Assert.Equal(local, m.Files["frame-codes/m" + idx.MeanVersion + "/log-0000.bin"]);   // the mirror is the local copy, byte for byte

            var restarted = new FrameCodesCache(b) { RangeBytes = 4096, Streams = 2, Mirror = m };   // a new container: empty disk, same mirror
            var v = await restarted.ViewAsync(f, P, idx, default);
            Assert.Equal(0, v.Missing); Assert.Equal(300, v.Frames);
            Assert.Equal(1, restarted.MirrorRestores); Assert.Equal(0, restarted.Reads);   // no ranged read of the log
        }
        finally { foreach (var d in new[] { a, b }) try { Directory.Delete(d, true); } catch { } }
    }

    [Fact]
    public async Task A_mirror_that_is_not_the_local_copy_is_rewritten_from_it()
    {
        var (f, idx, _) = await FrameCodesTests.SeedFor(3);
        await FrameCodes.FillAsync(f, P, idx, 60, 0, _ => { }, default);
        var m = new FakeMirror(); var rel = "frame-codes/m" + idx.MeanVersion + "/log-0000.bin"; m.Files[rel] = [1, 2, 3, 4, 5, 6, 7];   // something else is there
        var dir = Tmp();
        try
        {
            var c = new FrameCodesCache(dir) { RangeBytes = 4096, Streams = 2, Mirror = m };
            Assert.Equal(0, (await c.ViewAsync(f, P, idx, default)).Missing);   // the restored junk is dropped by the view's own check, the log is read from the store
            var local = File.ReadAllBytes(Directory.GetFiles(dir, "log-0000*.bin", SearchOption.AllDirectories).OrderBy(File.GetLastWriteTimeUtc).Last());
            Assert.Equal(local, m.Files[rel]);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public async Task A_frames_file_from_the_store_is_uploaded_and_a_new_cache_takes_it_from_the_mirror()
    {
        var s = new FakeStore(); var data = Enumerable.Range(0, 5000).Select(i => (byte)i).ToArray();
        await s.PutAsync("index/search/frames/abc.bin", data, null, null, true, default);
        var m = new FakeMirror(); string a = Tmp(), b = Tmp();
        try
        {
            var c1 = new FramesCache(a, 1 << 20) { Mirror = m };
            await c1.EnsureAsync(s, "index/search/frames/abc.bin", "sha1234567890", data.Length, default);
            for (int i = 0; i < 100 && c1.MirrorUploads == 0; i++) await Task.Delay(20);   // the upload runs in the background
            Assert.Equal(1, c1.Downloads); Assert.Equal(1, c1.MirrorUploads);
            var c2 = new FramesCache(b, 1 << 20) { Mirror = m };
            var p = await c2.EnsureAsync(s, "index/search/frames/abc.bin", "sha1234567890", data.Length, default);
            Assert.Equal(data, File.ReadAllBytes(p));
            Assert.Equal(0, c2.Downloads); Assert.Equal(1, c2.MirrorHits);   // from the share, not the store
        }
        finally { foreach (var d in new[] { a, b }) try { Directory.Delete(d, true); } catch { } }
    }
}
