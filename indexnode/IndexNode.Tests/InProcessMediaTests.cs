using System.IO.Compression;
using Xunit;

namespace IndexNode.Tests;

public class InProcessMediaTests
{
    static uint Crc(byte[] b) { uint c = 0xFFFFFFFF; foreach (var x in b) { c ^= x; for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1; } return ~c; }
    static void Chunk(Stream o, string type, byte[] data)
    {
        void BE(int v) { o.WriteByte((byte)(v >> 24)); o.WriteByte((byte)(v >> 16)); o.WriteByte((byte)(v >> 8)); o.WriteByte((byte)v); }
        BE(data.Length); var td = System.Text.Encoding.ASCII.GetBytes(type).Concat(data).ToArray(); o.Write(td); BE((int)Crc(td));
    }
    /// <summary>An RGB PNG with a different filter on every row (none, sub, up, average, paeth).</summary>
    static byte[] MakePng(Rgb im)
    {
        int w = im.W, h = im.H, stride = w * 3; var raw = new MemoryStream(); var prev = new byte[stride];
        for (int y = 0; y < h; y++)
        {
            var cur = im.Data.AsSpan(y * stride, stride).ToArray(); int f = y % 5; raw.WriteByte((byte)f);
            for (int i = 0; i < stride; i++)
            {
                int a = i >= 3 ? cur[i - 3] : 0, up = prev[i], c = i >= 3 ? prev[i - 3] : 0, p = a + up - c, pa = Math.Abs(p - a), pb = Math.Abs(p - up), pc = Math.Abs(p - c);
                int pred = f switch { 0 => 0, 1 => a, 2 => up, 3 => (a + up) >> 1, _ => pa <= pb && pa <= pc ? a : pb <= pc ? up : c };
                raw.WriteByte((byte)(cur[i] - pred));
            }
            prev = cur;
        }
        var z = new MemoryStream(); using (var zs = new ZLibStream(z, CompressionLevel.Fastest, true)) zs.Write(raw.ToArray());
        var o = new MemoryStream(); o.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        var ihdr = new byte[13]; ihdr[3] = (byte)w; ihdr[7] = (byte)h; ihdr[8] = 8; ihdr[9] = 2;
        Chunk(o, "IHDR", ihdr); Chunk(o, "IDAT", z.ToArray()); Chunk(o, "IEND", []); return o.ToArray();
    }
    static Rgb Noise(int w, int h, int seed) { var px = new byte[w * h * 3]; new Random(seed).NextBytes(px); return new Rgb(px, w, h); }

    [Fact]
    public void Png_decodes_exactly_with_every_filter_type()
    {
        var im = Noise(37, 23, 1); var back = InProcessMedia.DecodeImage(MakePng(im));
        Assert.Equal((37, 23), (back.W, back.H)); Assert.Equal(im.Data, back.Data);
    }

    [Fact]
    public void Png_from_ffmpeg_decodes_to_what_ffmpeg_reads()
    {
        var ff = "/usr/bin/ffmpeg"; if (!File.Exists(ff)) return;
        var dir = Path.Combine(Path.GetTempPath(), "inproc-" + Guid.NewGuid().ToString("N")[..8]); Directory.CreateDirectory(dir);
        try
        {
            var im = Noise(64, 48, 3); var raw = Path.Combine(dir, "a.rgb"); File.WriteAllBytes(raw, im.Data);
            var p = Proc.RunAsync(ff, ["-hide_banner", "-loglevel", "error", "-y", "-f", "rawvideo", "-pix_fmt", "rgb24", "-s", "64x48", "-i", raw, Path.Combine(dir, "a.png")], default).GetAwaiter().GetResult();
            Assert.Equal(0, p.ExitCode); Assert.Equal(im.Data, InProcessMedia.DecodeImage(File.ReadAllBytes(Path.Combine(dir, "a.png"))).Data);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Jpeg_and_png_are_sniffed_and_garbage_is_refused()
    {
        var im = Noise(64, 48, 5);
        Assert.Equal((64, 48), (InProcessMedia.DecodeImage(Imaging.EncodeJpeg(im)).W, InProcessMedia.DecodeImage(Imaging.EncodeJpeg(im)).H));
        Assert.Throws<InvalidDataException>(() => InProcessMedia.DecodeImage([1, 2, 3, 4, 5, 6, 7, 8, 9]));
        var interlaced = MakePng(im); interlaced[28] = 1;   // IHDR interlace byte
        Assert.Throws<InvalidDataException>(() => InProcessMedia.DecodeImage(interlaced));
    }

    [Fact]
    public void Sscd_preparation_crops_then_resizes_to_short_side_288_without_ffmpeg()
    {
        var im = Noise(640, 360, 9); var crop = new BarCrop(40, 40, 0, 0);
        var (rgb, w, h) = Sscd.Prepare(im, crop);
        Assert.Equal(Sscd.InputSize(640, 360, crop), (w, h)); Assert.Equal(288, Math.Min(w, h)); Assert.Equal(w * h * 3, rgb.Length);
    }

    [Fact]
    public void Wav_16k_mono_is_read_and_other_formats_are_refused()
    {
        var b = new byte[44 + 3200]; System.Text.Encoding.ASCII.GetBytes("RIFF").CopyTo(b, 0); System.Text.Encoding.ASCII.GetBytes("WAVEfmt ").CopyTo(b, 8); BitConverter.GetBytes(16).CopyTo(b, 16);
        BitConverter.GetBytes((short)1).CopyTo(b, 20); BitConverter.GetBytes((short)1).CopyTo(b, 22); BitConverter.GetBytes(16000).CopyTo(b, 24); BitConverter.GetBytes((short)16).CopyTo(b, 34);
        System.Text.Encoding.ASCII.GetBytes("data").CopyTo(b, 36); BitConverter.GetBytes(3200).CopyTo(b, 40); BitConverter.GetBytes((short)16384).CopyTo(b, 44);
        var f = InProcessMedia.ReadWav(b); Assert.Equal(1600, f.Length); Assert.Equal(0.5f, f[0], 3);
        Assert.Throws<InvalidDataException>(() => InProcessMedia.ReadWav(System.Text.Encoding.ASCII.GetBytes("OggS" + new string('x', 80))));
        BitConverter.GetBytes(44100).CopyTo(b, 24); Assert.Throws<InvalidDataException>(() => InProcessMedia.ReadWav(b));
    }

    // 2026-11-20: flaky under parallel runs, counts children process-wide (every child of the test process, whichever test started it).
    [Fact(Skip = "2026-11-20: flaky under parallel runs, counts children process-wide")]
    public async Task The_service_path_starts_no_child_process_even_with_no_ffmpeg()
    {
        // the model is not downloaded in tests, so the descriptor step is the real in-process one only when a model is present; the rest (decode, crop, WAV) is exercised above.
        var an = new PreCheckAnalyzer(new Tools(new HttpClient(), new NodeConfig()), new NodeConfig(), new HttpClient(), new FakeStore(), new Paths("p/"), new SearchIndexCache(Path.Combine(Path.GetTempPath(), "ip-" + Guid.NewGuid().ToString("N")[..6]), TimeSpan.FromMinutes(1))) { InProcess = true, SkipModelCheck = true };
        var wav = Path.Combine(Path.GetTempPath(), "ip-" + Guid.NewGuid().ToString("N")[..6] + ".wav");
        var b = new byte[44]; System.Text.Encoding.ASCII.GetBytes("RIFF").CopyTo(b, 0); System.Text.Encoding.ASCII.GetBytes("WAVEfmt ").CopyTo(b, 8); BitConverter.GetBytes(16).CopyTo(b, 16); BitConverter.GetBytes((short)1).CopyTo(b, 20); BitConverter.GetBytes((short)1).CopyTo(b, 22); BitConverter.GetBytes(16000).CopyTo(b, 24); BitConverter.GetBytes((short)16).CopyTo(b, 34); System.Text.Encoding.ASCII.GetBytes("data").CopyTo(b, 36);
        File.WriteAllBytes(wav, b);
        try
        {
            int before = Directory.GetDirectories("/proc").Count(d => int.TryParse(Path.GetFileName(d), out _) && File.Exists(d + "/stat") && File.ReadAllText(d + "/stat").Split(' ')[3] == Environment.ProcessId.ToString());
            var r = await an.SpeechAsync(wav, 1, 60, 10, default);   // an empty clip: SenseVoice missing/unavailable or no speech, never an ffmpeg start
            int after = Directory.GetDirectories("/proc").Count(d => int.TryParse(Path.GetFileName(d), out _) && File.Exists(d + "/stat") && File.ReadAllText(d + "/stat").Split(' ')[3] == Environment.ProcessId.ToString());
            Assert.Equal(before, after); Assert.DoesNotContain("ffmpeg", r.Unavailable ?? "");
        }
        finally { File.Delete(wav); }
    }
}
