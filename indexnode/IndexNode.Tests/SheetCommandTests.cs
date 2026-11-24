using Xunit;

namespace IndexNode.Tests;

/// <summary>0.13.47: `indexnode sheet`: the drawing, the size cap, the look-alike pick and the stored-frames source.</summary>
public class SheetCommandTests
{
    static readonly Paths P = new("index/");
    static Rgb Solid(int w, int h, byte r, byte g, byte b) { var d = new byte[w * h * 3]; for (int i = 0; i < w * h; i++) { d[i * 3] = r; d[i * 3 + 1] = g; d[i * 3 + 2] = b; } return new Rgb(d, w, h); }
    static Rgb Noise(int w, int h, int seed) { var d = new byte[w * h * 3]; new Random(seed).NextBytes(d); return new Rgb(d, w, h); }
    /// <summary>A picture with structure: a bright square whose place depends on <paramref name="k"/>.</summary>
    static Rgb Square(int w, int h, int k)
    {
        var im = Solid(w, h, 40, 40, 40); int x0 = 10 + k * 23 % (w - 40), y0 = 12 + k * 37 % (h - 40);
        for (int y = y0; y < y0 + 28; y++) for (int x = x0; x < x0 + 28; x++) { int p = (y * w + x) * 3; im.Data[p] = 230; im.Data[p + 1] = 200; im.Data[p + 2] = 60; }
        return im;
    }

    [Fact]
    public void Clock_spread_and_the_matched_times_of_a_decision()
    {
        Assert.Equal("0:00:00", SheetCommand.Clock(0)); Assert.Equal("0:46:11", SheetCommand.Clock(2771)); Assert.Equal("2:07:05", SheetCommand.Clock(7624.6));
        Assert.Equal([75.0, 225.0], SheetCommand.Spread(0, 300, 2)); Assert.Equal([2730.3], SheetCommand.Spread(2580.3, 2880.3, 1)); Assert.Empty(SheetCommand.Spread(0, 10, 0));
        Assert.Equal([2771.0, 6407.0], SheetCommand.MatchTimes("member DAK8i46znUw matches both segments (shares 0.97/0.87, times 2771/6407 s, in order)")!);
        Assert.Equal([1459.0, 3408.5], SheetCommand.MatchTimes("member X matches both segments (shares 0.93/0.97, times 1459/3408.5 s, in order); duration 4862 s within ...")!);
        Assert.Null(SheetCommand.MatchTimes("new (no sample frame matched any indexed upload)")); Assert.Null(SheetCommand.MatchTimes(null));
    }

    [Fact]
    public void The_grid_has_a_cell_per_frame_a_dark_cell_for_a_missing_one_and_the_time_in_the_corner()
    {
        var a = new SheetCommand.Shot(10, 10, Solid(360, 640, 200, 30, 30)); var wide = new SheetCommand.Shot(20, 21, Solid(640, 360, 30, 200, 30));
        var im = SheetCommand.Compose([[a, null, a], [wide, wide]], 120);
        Assert.Equal(3 * 120 + 4 * 2, im.W);                                   // three columns, 2 px gaps
        Assert.Equal(213 + 68 + 3 * 2, im.H);                                  // a row is as tall as its tallest frame: 640*120/360 = 213, 360*120/640 = 68 (rounded)
        int At(int x, int y, int ch) => im.Data[(y * im.W + x) * 3 + ch];
        Assert.Equal(200, At(2 + 60, 2 + 30, 0));                              // first cell: the red frame
        Assert.Equal(24, At(2 + 122 + 60, 2 + 30, 0));                         // the missing frame: background
        Assert.Equal(200, At(2 + 60, 2 + 213 + 2 + 30, 1));                    // second row: the green frame
        // the time label: a black box with white digits at the bottom-left of the frame
        int ly = 2 + 213 - 9; bool white = false, black = false;
        for (int y = ly; y < ly + 9; y++) for (int x = 2; x < 2 + 40; x++) { white |= At(x, y, 0) == 255 && At(x, y, 1) == 255; black |= At(x, y, 0) == 0 && At(x, y, 1) == 0; }
        Assert.True(white && black);
        // a frame narrower than the cell is not blown up
        var small = SheetCommand.Compose([[new SheetCommand.Shot(0, 0, Solid(36, 64, 9, 9, 200))]], 120);
        Assert.Equal(64 + 4, small.H);
    }

    [Fact]
    public void The_sheet_fits_the_size_cap_by_quality_first_and_then_by_narrower_cells()
    {
        var rows = new List<IReadOnlyList<SheetCommand.Shot?>> { Enumerable.Range(0, 4).Select(i => (SheetCommand.Shot?)new SheetCommand.Shot(i, i, Noise(360, 640, i))).ToList(), Enumerable.Range(4, 4).Select(i => (SheetCommand.Shot?)new SheetCommand.Shot(i, i, Noise(360, 640, i))).ToList() };
        var big = SheetCommand.Fit(rows, 240, 10_000_000);
        Assert.Equal((80, 240), (big.Quality, big.CellW));                    // nothing to give up
        var capped = SheetCommand.Fit(rows, 240, 100 * 1024);                 // noise is the worst case for JPEG
        Assert.True(capped.Jpeg.Length <= 100 * 1024, $"{capped.Jpeg.Length} bytes at quality {capped.Quality}, cells {capped.CellW}");
        Assert.True(capped.Quality < 80 || capped.CellW < 240);
        Assert.Equal((capped.CellW * 4 + 10), Imaging.DecodeJpeg(capped.Jpeg).W);
        var none = SheetCommand.Fit(rows, 240, 100);                           // an impossible cap: the smallest it reached, not a failure
        Assert.True(none.Jpeg.Length > 100 && none.CellW < 240);
    }

    [Fact]
    public void The_look_alike_frame_is_the_same_picture_even_when_cropped_and_rescaled()
    {
        var a = Square(360, 640, 3);
        Assert.True(SheetCommand.Similarity(a, a) > 0.999);
        var smaller = Thumbs.BoxResize(a, 180, 320);                            // the same picture at another size
        Assert.True(SheetCommand.Similarity(a, smaller) > 0.95);
        Assert.True(SheetCommand.Similarity(a, Square(360, 640, 9)) < SheetCommand.Similarity(a, smaller) - 0.3);   // another shot scores clearly lower
        Assert.Equal(0, SheetCommand.Similarity(Solid(64, 64, 5, 5, 5), a), 6);  // a flat frame is like nothing
    }

    static async Task<FakeStore> StoredFrames(string id)
    {
        // two 600 s chunks of stored JPEG frames, one every 100 s: frame k is a square at place k
        var s = new FakeStore(); var chunks = new List<(string, double, int, long)>();
        for (int c = 0; c < 2; c++)
        {
            var ms = new MemoryStream(); var frames = new List<ChunkFrame>();
            for (int k = 0; k < 6; k++)
            {
                var jpg = Imaging.EncodeJpeg(Square(180, 320, c * 6 + k), 90);
                frames.Add(new ChunkFrame(k, c * 600 + k * 100, k, [], null, c == 0 ? ms.Position : -1, c == 0 ? jpg.Length : 0));   // the second chunk has no byte ranges (an older index): the stream is split at the JPEG markers
                ms.Write(jpg);
            }
            var n = FrameChunks.Name(c);
            await s.PutAsync(P.Video(id, $"frames/{n}.json"), FrameChunks.JpegIndexJson(n, c * 600, 180, 320, 90, frames), null, null, false, default);
            await s.PutAsync(P.Video(id, $"frames/{n}.mjpeg"), ms.ToArray(), null, null, false, default, "application/octet-stream");
            chunks.Add((n, c * 600, 6, ms.Length));
        }
        await s.PutAsync(P.Video(id, "frames/manifest.json"), FrameChunks.ManifestJson("frames", 180, 320, 0, 12, chunks, container: "mjpeg", codec: "jpeg"), null, null, false, default);
        return s;
    }

    [Fact]
    public async Task Stored_frames_give_the_nearest_frame_and_the_frames_around_a_time()
    {
        var s = await StoredFrames("vid00000001");
        await using var src = (await SheetCommand.FramesSource.OpenAsync(s, P, "vid00000001", null, default))!;
        Assert.Equal(1100, src.Duration);
        var at = (await src.AtAsync(240, default))!;
        Assert.Equal((240.0, 200.0), (at.Wanted, at.T)); Assert.True(SheetCommand.Similarity(at.Im, Square(180, 320, 2)) > 0.95);
        var late = (await src.AtAsync(880, default))!;                         // in the second chunk, whose index has no byte ranges
        Assert.Equal(900, late.T); Assert.True(SheetCommand.Similarity(late.Im, Square(180, 320, 9)) > 0.95);
        Assert.Equal(1100, (await src.AtAsync(5000, default))!.T);              // past the end: the last frame
        var near = await src.NearAsync(600, 150, default);                      // across the chunk border
        Assert.Equal([500.0, 600.0, 700.0], near.Select(x => x.T).ToArray());
        // the look-alike pick: the frame at 700 s is found from a copy of it at another size, though 600 s is the recorded time
        var want = Thumbs.BoxResize(Square(180, 320, 7), 90, 160);
        Assert.Equal(700, near.OrderByDescending(x => SheetCommand.Similarity(want, x.Im)).First().T);
        Assert.Null(await SheetCommand.FramesSource.OpenAsync(s, P, "nothere0000", null, default));
    }
}
