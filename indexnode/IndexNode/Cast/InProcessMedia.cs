using System.IO.Compression;

namespace IndexNode;

/// <summary>Managed decoders for the query service, so a request never starts ffmpeg/ffprobe: PNG and "any still image" (JPEG through <see cref="Imaging.DecodeJpeg"/>), and 16 kHz mono WAV.</summary>
public static class InProcessMedia
{
    /// <summary>JPEG or PNG (sniffed from the bytes) to rgb24. Throws InvalidDataException for anything else.</summary>
    public static Rgb DecodeImage(byte[] b)
    {
        if (b.Length > 8 && b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47) return DecodePng(b);
        if (b.Length > 3 && b[0] == 0xFF && b[1] == 0xD8) { try { return Imaging.DecodeJpeg(b); } catch (Exception e) when (e is not InvalidDataException) { throw new InvalidDataException("jpeg: " + e.Message); } }
        throw new InvalidDataException(IsWebp(b) ? "a WebP image is converted first (WebpToPngAsync)" : "not a JPEG, PNG or WebP image");
    }

    /// <summary>"RIFF" size "WEBP": what phones and browsers often save a screenshot as (0.13.46).</summary>
    public static bool IsWebp(byte[] b) => b.Length > 12 && b[0] == 'R' && b[1] == 'I' && b[2] == 'F' && b[3] == 'F' && b[8] == 'W' && b[9] == 'E' && b[10] == 'B' && b[11] == 'P';

    /// <summary>0.13.46: a WebP image to a PNG through ffmpeg, a child process, with the input format forced to WebP (nothing else is probed); an animated WebP gives its first frame, alpha is dropped.
    /// The PNG then takes the in-process path (<see cref="DecodeImage"/>), so a WebP screenshot is embedded exactly like a PNG of the same pixels.
    /// No managed WebP decoder is bundled: the free ImageSharp line (3.1) has open advisories and the patched lines need a licence key at build time.</summary>
    public static async Task<byte[]> WebpToPngAsync(string ffmpeg, byte[] webp, CancellationToken ct)
    {
        var dir = Path.Combine(Path.GetTempPath(), "indexnode-webp-" + Guid.NewGuid().ToString("N")[..8]); Directory.CreateDirectory(dir);
        try
        {
            string src = Path.Combine(dir, "in.webp"), dst = Path.Combine(dir, "out.png");
            await File.WriteAllBytesAsync(src, webp, ct);
            var r = await Proc.RunAsync(ffmpeg, ["-hide_banner", "-loglevel", "error", "-nostdin", "-y", "-f", "webp_pipe", "-i", src, "-frames:v", "1", "-pix_fmt", "rgb24", "-c:v", "png", dst], ct);
            if (r.ExitCode != 0 || !File.Exists(dst)) throw new InvalidDataException("webp: the image could not be decoded");   // ffmpeg's own words (addresses, temp paths) are not for a caller
            return await File.ReadAllBytesAsync(dst, ct);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    /// <summary>PNG: all colour types, 1/2/4/8/16 bit, non-interlaced (alpha is dropped over black-free: the colour channels are used as they are).</summary>
    public static Rgb DecodePng(byte[] b)
    {
        int p = 8, w = 0, h = 0, depth = 0, ct = 0, interlace = 0; byte[]? pal = null; var idat = new MemoryStream();
        while (p + 8 <= b.Length)
        {
            int len = (b[p] << 24) | (b[p + 1] << 16) | (b[p + 2] << 8) | b[p + 3]; var type = System.Text.Encoding.ASCII.GetString(b, p + 4, 4); int d = p + 8;
            if (len < 0 || d + len > b.Length) throw new InvalidDataException("png: truncated");
            if (type == "IHDR") { w = (b[d] << 24) | (b[d + 1] << 16) | (b[d + 2] << 8) | b[d + 3]; h = (b[d + 4] << 24) | (b[d + 5] << 16) | (b[d + 6] << 8) | b[d + 7]; depth = b[d + 8]; ct = b[d + 9]; interlace = b[d + 12]; }
            else if (type == "PLTE") pal = b.AsSpan(d, len).ToArray();
            else if (type == "IDAT") idat.Write(b, d, len);
            else if (type == "IEND") break;
            p = d + len + 4;
        }
        if (w <= 0 || h <= 0 || (long)w * h > 100_000_000) throw new InvalidDataException("png: bad size");
        if (interlace != 0) throw new InvalidDataException("png: interlaced images are not supported");
        int ch = ct switch { 0 => 1, 2 => 3, 3 => 1, 4 => 2, 6 => 4, _ => throw new InvalidDataException("png: colour type " + ct) };
        if (depth is not (1 or 2 or 4 or 8 or 16)) throw new InvalidDataException("png: bit depth " + depth);
        int bpp = Math.Max(1, ch * depth / 8), stride = (w * ch * depth + 7) / 8;
        var raw = new byte[(stride + 1) * h]; idat.Position = 0;
        using (var z = new ZLibStream(idat, CompressionMode.Decompress)) { int o = 0; while (o < raw.Length) { int k = z.Read(raw, o, raw.Length - o); if (k == 0) throw new InvalidDataException("png: short data"); o += k; } }
        var cur = new byte[stride]; var prev = new byte[stride]; var d3 = new byte[w * h * 3];
        for (int y = 0; y < h; y++)
        {
            int f = raw[y * (stride + 1)]; var line = raw.AsSpan(y * (stride + 1) + 1, stride);
            for (int i = 0; i < stride; i++)
            {
                int a = i >= bpp ? cur[i - bpp] : 0, up = prev[i], c = i >= bpp ? prev[i - bpp] : 0, x = line[i];
                cur[i] = (byte)(f switch
                {
                    0 => x, 1 => x + a, 2 => x + up, 3 => x + ((a + up) >> 1),
                    4 => x + Paeth(a, up, c), _ => throw new InvalidDataException("png: filter " + f),
                });
            }
            for (int x = 0; x < w; x++)
            {
                int o = (y * w + x) * 3; int S(int idx) // sample idx (channel-major in the row) scaled to 8 bit
                {
                    if (depth == 8) return cur[idx];
                    if (depth == 16) return cur[idx * 2];
                    int bit = idx * depth; int v = (cur[bit >> 3] >> (8 - depth - (bit & 7))) & ((1 << depth) - 1);
                    return ct == 3 ? v : v * 255 / ((1 << depth) - 1);
                }
                switch (ct)
                {
                    case 0: case 4: d3[o] = d3[o + 1] = d3[o + 2] = (byte)S(x * ch); break;
                    case 2: case 6: d3[o] = (byte)S(x * ch); d3[o + 1] = (byte)S(x * ch + 1); d3[o + 2] = (byte)S(x * ch + 2); break;
                    default: { int i3 = S(x); if (pal == null || i3 * 3 + 2 >= pal.Length) throw new InvalidDataException("png: bad palette"); d3[o] = pal[i3 * 3]; d3[o + 1] = pal[i3 * 3 + 1]; d3[o + 2] = pal[i3 * 3 + 2]; break; }
                }
            }
            (cur, prev) = (prev, cur);
        }
        return new Rgb(d3, w, h);
    }
    static int Paeth(int a, int b, int c) { int p = a + b - c, pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c); return pa <= pb && pa <= pc ? a : pb <= pc ? b : c; }

    /// <summary>16 kHz mono WAV (16-bit PCM or float32) to float samples. A stereo file is averaged to mono; another sample rate is refused (the client resamples).</summary>
    public static float[] ReadWav(byte[] b)
    {
        if (b.Length < 44 || b[0] != 'R' || b[1] != 'I' || b[2] != 'F' || b[3] != 'F' || b[8] != 'W') throw new InvalidDataException("audio must be a 16 kHz mono 16-bit WAV (RIFF); Opus/Ogg is not decoded by the service");
        int fmt = 0, chans = 0, rate = 0, bits = 0, p = 12;
        while (p + 8 <= b.Length)
        {
            var id = System.Text.Encoding.ASCII.GetString(b, p, 4); int len = BitConverter.ToInt32(b, p + 4); int d = p + 8;
            if (id == "fmt ") { fmt = BitConverter.ToUInt16(b, d); chans = BitConverter.ToUInt16(b, d + 2); rate = BitConverter.ToInt32(b, d + 4); bits = BitConverter.ToUInt16(b, d + 14); if (fmt == 0xFFFE && len >= 26) fmt = BitConverter.ToUInt16(b, d + 24); }
            else if (id == "data")
            {
                if (rate != 16000) throw new InvalidDataException($"audio must be 16 kHz (got {rate} Hz)");
                if (chans < 1) throw new InvalidDataException("wav: no fmt chunk");
                int n = Math.Min(len < 0 ? int.MaxValue : len, b.Length - d); int bytes = bits / 8, frames = n / (bytes * chans); var f = new float[frames];
                for (int i = 0; i < frames; i++)
                {
                    double s = 0;
                    for (int c = 0; c < chans; c++)
                    {
                        int o = d + (i * chans + c) * bytes;
                        s += fmt == 3 && bits == 32 ? BitConverter.ToSingle(b, o) : fmt == 1 && bits == 16 ? BitConverter.ToInt16(b, o) / 32768.0 : throw new InvalidDataException($"wav: format {fmt}/{bits} bit is not supported (16-bit PCM or float32)");
                    }
                    f[i] = (float)(s / chans);
                }
                return f;
            }
            p = d + len + (len & 1);
        }
        throw new InvalidDataException("wav: no data chunk");
    }
}
