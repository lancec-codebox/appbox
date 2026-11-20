namespace IndexNode;

/// <summary>
/// Sequential (baseline / extended, Huffman, 8-bit) YCbCr JPEG decoder that returns the planes as stored, with the same integer IDCT as ffmpeg's mjpeg decoder (libavcodec simple_idct, 8-bit),
/// so decoding plus <see cref="Swscale"/> gives the pixels the ffmpeg path gives. The int8 SSCD model reacts to single-level pixel differences, and libjpeg's IDCT differs from ffmpeg's by one level here and there.
/// Anything else (progressive, arithmetic coding, 12-bit, non-3-component, RGB/Adobe, unusual sampling) returns null and the caller falls back to <see cref="Imaging.DecodeJpeg"/>.
/// </summary>
public static class JpegYuv
{
    static readonly byte[] Zig = [0, 1, 8, 16, 9, 2, 3, 10, 17, 24, 32, 25, 18, 11, 4, 5, 12, 19, 26, 33, 40, 48, 41, 34, 27, 20, 13, 6, 7, 14, 21, 28, 35, 42, 49, 56, 57, 50, 43, 36, 29, 22, 15, 23, 30, 37, 44, 51, 58, 59, 52, 45, 38, 31, 39, 46, 53, 60, 61, 54, 47, 55, 62, 63];

    sealed class Huff
    {
        public readonly int[] MinCode = new int[17], MaxCode = new int[18], ValPtr = new int[17]; public byte[] Vals = [];
        public Huff(byte[] counts, byte[] vals)
        {
            Vals = vals; int code = 0, k = 0;
            for (int l = 1; l <= 16; l++) { ValPtr[l] = k; MinCode[l] = code; code += counts[l - 1]; k += counts[l - 1]; MaxCode[l] = counts[l - 1] == 0 ? -1 : code - 1; code <<= 1; }
            MaxCode[17] = int.MaxValue;
        }
    }

    sealed class Bits(byte[] d, int pos)
    {
        public int Pos = pos; int acc, n; public bool Marker;
        public int Bit()
        {
            if (n == 0)
            {
                if (Pos >= d.Length) { acc = 0; n = 8; return Next(); }
                int b = d[Pos++];
                if (b == 0xFF) { int b2 = Pos < d.Length ? d[Pos] : 0xD9; if (b2 == 0) Pos++; else { Marker = true; Pos--; b = 0; } }
                acc = b; n = 8;
            }
            return Next();
        }
        int Next() { n--; return (acc >> n) & 1; }
        public int Get(int c) { int v = 0; for (int i = 0; i < c; i++) v = (v << 1) | Bit(); return v; }
        public void Reset() { n = 0; acc = 0; Marker = false; }
    }

    static int Decode(Bits b, Huff h)
    {
        int code = 0;
        for (int l = 1; l <= 16; l++)
        {
            code = (code << 1) | b.Bit();
            if (h.MaxCode[l] >= 0 && code <= h.MaxCode[l] && code >= h.MinCode[l]) return h.Vals[h.ValPtr[l] + code - h.MinCode[l]];
        }
        throw new InvalidDataException("jpeg: bad huffman code");
    }
    static int Extend(int v, int t) => t == 0 ? 0 : v < (1 << (t - 1)) ? v - (1 << t) + 1 : v;

    // libavcodec/simple_idct_template.c, BIT_DEPTH 8
    const int W1 = 22725, W2 = 21407, W3 = 19266, W4 = 16383, W5 = 12873, W6 = 8867, W7 = 4520, RowShift = 11, ColShift = 20;

    static void IdctRow(short[] r, int o)
    {
        if (r[o + 1] == 0 && r[o + 2] == 0 && r[o + 3] == 0 && r[o + 4] == 0 && r[o + 5] == 0 && r[o + 6] == 0 && r[o + 7] == 0)
        {
            short t = unchecked((short)(r[o] << 3)); for (int i = 0; i < 8; i++) r[o + i] = t; return;
        }
        unchecked
        {
            int a0 = W4 * r[o] + (1 << (RowShift - 1)), a1 = a0, a2 = a0, a3 = a0;
            a0 += W2 * r[o + 2]; a1 += W6 * r[o + 2]; a2 -= W6 * r[o + 2]; a3 -= W2 * r[o + 2];
            int b0 = W1 * r[o + 1] + W3 * r[o + 3], b1 = W3 * r[o + 1] - W7 * r[o + 3], b2 = W5 * r[o + 1] - W1 * r[o + 3], b3 = W7 * r[o + 1] - W5 * r[o + 3];
            a0 += W4 * r[o + 4] + W6 * r[o + 6]; a1 += -W4 * r[o + 4] - W2 * r[o + 6]; a2 += -W4 * r[o + 4] + W2 * r[o + 6]; a3 += W4 * r[o + 4] - W6 * r[o + 6];
            b0 += W5 * r[o + 5] + W7 * r[o + 7]; b1 += -W1 * r[o + 5] - W5 * r[o + 7]; b2 += W7 * r[o + 5] + W3 * r[o + 7]; b3 += W3 * r[o + 5] - W1 * r[o + 7];
            r[o] = (short)((a0 + b0) >> RowShift); r[o + 7] = (short)((a0 - b0) >> RowShift); r[o + 1] = (short)((a1 + b1) >> RowShift); r[o + 6] = (short)((a1 - b1) >> RowShift);
            r[o + 2] = (short)((a2 + b2) >> RowShift); r[o + 5] = (short)((a2 - b2) >> RowShift); r[o + 3] = (short)((a3 + b3) >> RowShift); r[o + 4] = (short)((a3 - b3) >> RowShift);
        }
    }

    static byte Clip(int v) => (byte)(v < 0 ? 0 : v > 255 ? 255 : v);

    static void IdctPut(short[] blk, byte[] dst, int off, int stride)
    {
        for (int i = 0; i < 8; i++) IdctRow(blk, i * 8);
        unchecked
        {
            for (int c = 0; c < 8; c++)
            {
                int a0 = W4 * (blk[c] + ((1 << (ColShift - 1)) / W4)), a1 = a0, a2 = a0, a3 = a0;
                a0 += W2 * blk[8 * 2 + c]; a1 += W6 * blk[8 * 2 + c]; a2 += -W6 * blk[8 * 2 + c]; a3 += -W2 * blk[8 * 2 + c];
                int b0 = W1 * blk[8 + c] + W3 * blk[24 + c], b1 = W3 * blk[8 + c] - W7 * blk[24 + c], b2 = W5 * blk[8 + c] - W1 * blk[24 + c], b3 = W7 * blk[8 + c] - W5 * blk[24 + c];
                a0 += W4 * blk[32 + c]; a1 += -W4 * blk[32 + c]; a2 += -W4 * blk[32 + c]; a3 += W4 * blk[32 + c];
                b0 += W5 * blk[40 + c]; b1 += -W1 * blk[40 + c]; b2 += W7 * blk[40 + c]; b3 += W3 * blk[40 + c];
                a0 += W6 * blk[48 + c]; a1 += -W2 * blk[48 + c]; a2 += W2 * blk[48 + c]; a3 += -W6 * blk[48 + c];
                b0 += W7 * blk[56 + c]; b1 += -W5 * blk[56 + c]; b2 += W3 * blk[56 + c]; b3 += -W1 * blk[56 + c];
                int p = off + c;
                dst[p] = Clip((a0 + b0) >> ColShift); dst[p + stride] = Clip((a1 + b1) >> ColShift); dst[p + 2 * stride] = Clip((a2 + b2) >> ColShift); dst[p + 3 * stride] = Clip((a3 + b3) >> ColShift);
                dst[p + 4 * stride] = Clip((a3 - b3) >> ColShift); dst[p + 5 * stride] = Clip((a2 - b2) >> ColShift); dst[p + 6 * stride] = Clip((a1 - b1) >> ColShift); dst[p + 7 * stride] = Clip((a0 - b0) >> ColShift);
            }
        }
    }

    /// <summary>The planes of a JPEG as ffmpeg's mjpeg decoder would hand them to swscale, or null when this decoder does not take the file.</summary>
    public static YuvPlanes? Decode(byte[] d)
    {
        try { return DecodeCore(d); } catch (Exception e) when (e is InvalidDataException or IndexOutOfRangeException or ArgumentException) { return null; }
    }

    static YuvPlanes? DecodeCore(byte[] d)
    {
        if (d.Length < 4 || d[0] != 0xFF || d[1] != 0xD8) return null;
        var q = new int[4][]; var dcT = new Huff?[4]; var acT = new Huff?[4]; int restart = 0, w = 0, h = 0, p = 2; int[] cid = [], ch = [], cv = [], cq = [];
        while (p + 4 <= d.Length)
        {
            if (d[p] != 0xFF) { p++; continue; }
            int m = d[p + 1]; if (m == 0xFF) { p++; continue; }
            if (m == 0xD8 || (m >= 0xD0 && m <= 0xD7) || m == 0x01 || m == 0) { p += 2; continue; }
            int len = (d[p + 2] << 8) | d[p + 3]; int s = p + 4, e = p + 2 + len; if (e > d.Length) return null;
            if (m == 0xEE && len >= 14 && d[s] == 'A' && d[s + 1] == 'd') return null;   // Adobe: the colour transform is not ours to guess
            else if (m == 0xDB)
            {
                while (s < e) { int pq = d[s] >> 4, tq = d[s] & 15; s++; if (tq > 3) return null; var t = new int[64]; for (int i = 0; i < 64; i++) { t[Zig[i]] = pq == 0 ? d[s] : (d[s] << 8) | d[s + 1]; s += pq == 0 ? 1 : 2; } q[tq] = t; }
            }
            else if (m == 0xC4)
            {
                while (s < e)
                {
                    int tc = d[s] >> 4, th = d[s] & 15; s++; if (th > 3) return null; var counts = d.AsSpan(s, 16).ToArray(); s += 16; int n = counts.Sum(x => x); var vals = d.AsSpan(s, n).ToArray(); s += n;
                    if (tc == 0) dcT[th] = new Huff(counts, vals); else acT[th] = new Huff(counts, vals);
                }
            }
            else if (m == 0xDD) restart = (d[s] << 8) | d[s + 1];
            else if (m == 0xC0 || m == 0xC1)
            {
                if (d[s] != 8) return null; h = (d[s + 1] << 8) | d[s + 2]; w = (d[s + 3] << 8) | d[s + 4]; int nc = d[s + 5]; if (nc != 3 || w == 0 || h == 0) return null;
                cid = new int[3]; ch = new int[3]; cv = new int[3]; cq = new int[3];
                for (int i = 0; i < 3; i++) { cid[i] = d[s + 6 + 3 * i]; ch[i] = d[s + 7 + 3 * i] >> 4; cv[i] = d[s + 7 + 3 * i] & 15; cq[i] = d[s + 8 + 3 * i]; }
            }
            else if (m is 0xC2 or 0xC3 or (>= 0xC5 and <= 0xCF and not 0xC8 and not 0xC4)) return null;   // progressive, lossless, arithmetic, hierarchical
            else if (m == 0xDA) { return Scan(d, e, w, h, cid, ch, cv, cq, q, dcT, acT, restart, s); }
            p = e;
        }
        return null;
    }

    static YuvPlanes? Scan(byte[] d, int dataStart, int w, int h, int[] cid, int[] ch, int[] cv, int[] cq, int[][] q, Huff?[] dcT, Huff?[] acT, int restart, int sosBody)
    {
        if (w == 0 || cid.Length != 3) return null;
        if (cid[0] == 'R' && cid[1] == 'G' && cid[2] == 'B') return null;
        int hy = ch[0], vy = cv[0], hc = ch[1], vc = cv[1];
        if (ch[2] != hc || cv[2] != vc || hy < 1 || hy > 2 || vy < 1 || vy > 2 || hc < 1 || hc > hy || vc < 1 || vc > vy || hy % hc != 0 || vy % vc != 0) return null;
        if (d[sosBody] != 3) return null;
        var dcSel = new int[3]; var acSel = new int[3]; var compOf = new int[3];
        for (int i = 0; i < 3; i++) { int id = d[sosBody + 1 + 2 * i]; compOf[i] = Array.IndexOf(cid, id); if (compOf[i] < 0) return null; dcSel[compOf[i]] = d[sosBody + 2 + 2 * i] >> 4; acSel[compOf[i]] = d[sosBody + 2 + 2 * i] & 15; }
        if (d[sosBody + 7] != 0 || d[sosBody + 8] != 63) return null;
        int mcuW = 8 * hy, mcuH = 8 * vy, mx = (w + mcuW - 1) / mcuW, my = (h + mcuH - 1) / mcuH;
        int[] pw = [mx * hy * 8, mx * hc * 8, mx * hc * 8], ph = [my * vy * 8, my * vc * 8, my * vc * 8];
        var planes = new byte[3][]; for (int i = 0; i < 3; i++) planes[i] = new byte[pw[i] * ph[i]];
        var bits = new Bits(d, dataStart); var pred = new int[3]; var blk = new short[64]; int[] cnt = [0, 0, 0];
        for (int c = 0; c < 3; c++) { if (q[cq[c]] == null || dcT[dcSel[c]] == null || acT[acSel[c]] == null) return null; pred[c] = 1024; }
        int mcus = 0;
        for (int yy = 0; yy < my; yy++)
            for (int xx = 0; xx < mx; xx++)
            {
                if (restart > 0 && mcus > 0 && mcus % restart == 0)
                {
                    bits.Reset(); int pp = bits.Pos; while (pp + 1 < d.Length && !(d[pp] == 0xFF && d[pp + 1] >= 0xD0 && d[pp + 1] <= 0xD7)) pp++;
                    if (pp + 1 >= d.Length) return null; bits.Pos = pp + 2; for (int c = 0; c < 3; c++) pred[c] = 1024;
                }
                mcus++;
                for (int c = 0; c < 3; c++)
                    for (int by = 0; by < cv[c]; by++)
                        for (int bx = 0; bx < ch[c]; bx++)
                        {
                            Array.Clear(blk); var qt = q[cq[c]]!;
                            int t = Decode(bits, dcT[dcSel[c]]!); int diff = Extend(bits.Get(t), t) ; if (t == 0) diff = 0;
                            pred[c] = unchecked(pred[c] + diff * qt[0]); blk[0] = unchecked((short)pred[c]);
                            for (int k = 1; k < 64;)
                            {
                                int rs = Decode(bits, acT[acSel[c]]!); int r = rs >> 4, sz = rs & 15;
                                if (sz == 0) { if (r == 15) { k += 16; continue; } break; }
                                k += r; if (k > 63) throw new InvalidDataException("jpeg: coefficient index");
                                int v = Extend(bits.Get(sz), sz); blk[Zig[k]] = unchecked((short)(v * qt[Zig[k]])); k++;
                            }
                            int ox = (xx * ch[c] + bx) * 8, oy = (yy * cv[c] + by) * 8; IdctPut(blk, planes[c], oy * pw[c] + ox, pw[c]);
                        }
            }
        int hs = hy / hc - 1, vs = vy / vc - 1, cw = (w + (1 << hs) - 1) >> hs, chh = (h + (1 << vs) - 1) >> vs;
        byte[] Cut(byte[] s, int sw, int ww, int hh) { var o = new byte[ww * hh]; for (int j = 0; j < hh; j++) Buffer.BlockCopy(s, j * sw, o, j * ww, ww); return o; }
        return new YuvPlanes { Y = Cut(planes[0], pw[0], w, h), U = Cut(planes[1], pw[1], cw, chh), V = Cut(planes[2], pw[2], cw, chh), W = w, H = h, CW = cw, CH = chh, HSub = hs, VSub = vs };
    }
}
