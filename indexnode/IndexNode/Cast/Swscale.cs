namespace IndexNode;

/// <summary>Planar 8-bit YCbCr (JPEG, BT.601, full range) as the JPEG decoder produced it: chroma planes are CW x CH (subsampled by HSub/VSub shifts).</summary>
public sealed class YuvPlanes
{
    public byte[] Y = [], U = [], V = []; public int W, H, CW, CH, HSub, VSub;
    /// <summary>The region x,y,w,h cut out like ffmpeg's crop filter (x and y are rounded down to the chroma grid).</summary>
    public YuvPlanes Crop(int x, int y, int w, int h)
    {
        x &= ~((1 << HSub) - 1); y &= ~((1 << VSub) - 1); w = Math.Min(w, W - x); h = Math.Min(h, H - y);
        int cx = x >> HSub, cy = y >> VSub, cw = (w + (1 << HSub) - 1) >> HSub, ch = (h + (1 << VSub) - 1) >> VSub;
        byte[] Cut(byte[] s, int sw, int ox, int oy, int ww, int hh) { var o = new byte[ww * hh]; for (int j = 0; j < hh; j++) Buffer.BlockCopy(s, (oy + j) * sw + ox, o, j * ww, ww); return o; }
        return new YuvPlanes { Y = Cut(Y, W, x, y, w, h), U = Cut(U, CW, cx, cy, cw, ch), V = Cut(V, CW, cx, cy, cw, ch), W = w, H = h, CW = cw, CH = ch, HSub = HSub, VSub = VSub };
    }
}

/// <summary>
/// A managed port of libswscale's default scaler (flags=bicubic, ffmpeg n8.0.1) for the two conversions the node uses: YCbCr(JPEG, full range) -> rgb24 and rgb24 -> rgb24, with the
/// same fixed-point horizontal/vertical filters (initFilter: bicubic B=0 C=0.6, filter reduction, border fix, error-diffused normalisation), the same 15-bit intermediate, the same
/// chroma handling (full chroma for 4:4:4 sources or odd widths, else chroma scaled at half width and shared by pixel pairs) and the same YUV->RGB tables (BT.601).
/// ffmpeg's SIMD paths differ from this C reference by at most a rounding step; <c>SwscaleTests</c> measure the difference against a real ffmpeg.
/// </summary>
public static class Swscale
{
    const int Headroom = 512;

    static int Log2(int v) { int r = 0; while (v > 1) { v >>= 1; r++; } return r; }
    static long RoundedDiv(long a, long b) => (a >= 0 ? a + (b >> 1) : a - (b >> 1)) / b;

    /// <summary>libswscale initFilter for the bicubic default. Returns the 14/12-bit coefficients (size per output pixel), the first source index per output pixel and the filter size.</summary>
    internal static (short[] F, int[] Pos, int Size) InitFilter(int xInc, int srcW, int dstW, int one, int srcPos, int dstPos)
    {
        long fone = 1L << (54 - Math.Min(Log2(srcW / dstW), 8));
        var pos = new int[dstW]; int filterSize; long[] filter;
        if (Math.Abs(xInc - 0x10000) < 10 && srcPos == dstPos)
        {
            filterSize = 1; filter = new long[dstW];
            for (int i = 0; i < dstW; i++) { filter[i] = fone; pos[i] = i; }
        }
        else
        {
            const int sizeFactor = 4;
            filterSize = xInc <= 1 << 16 ? 1 + sizeFactor : 1 + (sizeFactor * srcW + dstW - 1) / dstW;
            filterSize = Math.Min(filterSize, srcW - 2); filterSize = Math.Max(filterSize, 1);
            filter = new long[dstW * filterSize];
            long B = (long)(0.0 * (1 << 24)), C = (long)(0.6 * (1 << 24));
            long xDstInSrc = ((dstPos * (long)xInc) >> 7) - ((srcPos * 0x10000L) >> 7);
            for (int i = 0; i < dstW; i++)
            {
                int xx = (int)((xDstInSrc - (filterSize - 2) * (1L << 16)) / (1 << 17)); pos[i] = xx;
                for (int j = 0; j < filterSize; j++)
                {
                    long d = Math.Abs(((long)xx * (1 << 17)) - xDstInSrc) << 13; long coeff;
                    if (xInc > 1 << 16) d = d * dstW / srcW;
                    if (d >= 1L << 31) coeff = 0;
                    else
                    {
                        long dd = (d * d) >> 30, ddd = (dd * d) >> 30;
                        if (d < 1L << 30) coeff = (12 * (1 << 24) - 9 * B - 6 * C) * ddd + (-18 * (1 << 24) + 12 * B + 6 * C) * dd + (6 * (1 << 24) - 2 * B) * (1L << 30);
                        else coeff = (-B - 6 * C) * ddd + (6 * B + 30 * C) * dd + (-12 * B - 48 * C) * d + (8 * B + 24 * C) * (1L << 30);
                    }
                    coeff /= (1L << 54) / fone;
                    filter[i * filterSize + j] = coeff; xx++;
                }
                xDstInSrc += 2L * xInc;
            }
        }
        int filter2Size = filterSize; var filter2 = filter; // no src/dst filter: filter2 is filter and pos is unchanged
        double cutLimit = 0.002 * fone; int minFilterSize = 0;
        for (int i = dstW - 1; i >= 0; i--)
        {
            int min = filter2Size; long cutOff = 0;
            for (int j = 0; j < filter2Size; j++)
            {
                cutOff += Math.Abs(filter2[i * filter2Size]);
                if (cutOff > cutLimit) break;
                if (i < dstW - 1 && pos[i] >= pos[i + 1]) break;
                int k; for (k = 1; k < filter2Size; k++) filter2[i * filter2Size + k - 1] = filter2[i * filter2Size + k];
                filter2[i * filter2Size + k - 1] = 0; pos[i]++;
            }
            cutOff = 0;
            for (int j = filter2Size - 1; j > 0; j--) { cutOff += Math.Abs(filter2[i * filter2Size + j]); if (cutOff > cutLimit) break; min--; }
            if (min > minFilterSize) minFilterSize = min;
        }
        filterSize = minFilterSize; var f = new long[dstW * filterSize];
        for (int i = 0; i < dstW; i++) for (int j = 0; j < filterSize; j++) f[i * filterSize + j] = j >= filter2Size ? 0 : filter2[i * filter2Size + j];
        for (int i = 0; i < dstW; i++)
        {
            if (pos[i] < 0)
            {
                for (int j = 1; j < filterSize; j++) { int left = Math.Max(j + pos[i], 0); f[i * filterSize + left] += f[i * filterSize + j]; f[i * filterSize + j] = 0; }
                pos[i] = 0;
            }
            if (pos[i] + filterSize > srcW)
            {
                int shift = pos[i] + Math.Min(filterSize - srcW, 0); long acc = 0;
                for (int j = filterSize - 1; j >= 0; j--) if (pos[i] + j >= srcW) { acc += f[i * filterSize + j]; f[i * filterSize + j] = 0; }
                for (int j = filterSize - 1; j >= 0; j--) f[i * filterSize + j] = j < shift ? 0 : f[i * filterSize + j - shift];
                pos[i] -= shift; f[i * filterSize + srcW - 1 - pos[i]] += acc;
            }
        }
        var outF = new short[dstW * filterSize];
        for (int i = 0; i < dstW; i++)
        {
            long error = 0, sum = 0;
            for (int j = 0; j < filterSize; j++) sum += f[i * filterSize + j];
            sum = (sum + one / 2) / one; if (sum == 0) sum = 1;
            for (int j = 0; j < filterSize; j++)
            {
                long v = f[i * filterSize + j] + error; int intV = (int)RoundedDiv(v, sum);
                outF[i * filterSize + j] = (short)intV; error = v - (long)intV * sum;
            }
        }
        return (outF, pos, filterSize);
    }

    // chroma/luma sample position in the scaler's grid: ((128 << sub) - 128 + 128) >> sub (the default, pos -513), always 128
    const int LocalPos = 128;

    static short[] HScale8(byte[] src, int stride, int rows, int srcW, int dstW, int xInc)
    {
        var (flt, pos, size) = InitFilter(xInc, srcW, dstW, 1 << 14, LocalPos, LocalPos); var o = new short[rows * dstW];
        Parallel.For(0, rows, y =>
        {
            int so = y * stride;
            for (int i = 0; i < dstW; i++) { int val = 0, p = pos[i]; for (int j = 0; j < size; j++) val += src[so + p + j] * flt[size * i + j]; o[y * dstW + i] = (short)Math.Min(val >> 7, 32767); }
        });
        return o;
    }

    static short[] HScale16(short[] src, int stride, int rows, int srcW, int dstW, int xInc, int sh)
    {
        var (flt, pos, size) = InitFilter(xInc, srcW, dstW, 1 << 14, LocalPos, LocalPos); var o = new short[rows * dstW];
        Parallel.For(0, rows, y =>
        {
            int so = y * stride;
            for (int i = 0; i < dstW; i++) { int val = 0, p = pos[i]; for (int j = 0; j < size; j++) val += (ushort)src[so + p + j] * flt[size * i + j]; o[y * dstW + i] = (short)Math.Min(val >> sh, 32767); }
        });
        return o;
    }

    static int Inc(int src, int dst) => (int)((((long)src << 16) + (dst >> 1)) / dst);
    static byte Clip8(int v) => (byte)(v < 0 ? 0 : v > 255 ? 255 : v);
    static long RoundToInt16(long f) { int r = (int)((f + (1 << 15)) >> 16); return r < -0x7FFF ? -0x8000 : r > 0x7FFF ? 0x7FFF : r; }

    /// <summary>YCbCr (JPEG: BT.601, full range) to rgb24 at dw x dh.</summary>
    public static Rgb ScaleYuv(YuvPlanes p, int dw, int dh)
    {
        bool full = dw % 2 == 1 || (p.HSub == 0 && p.VSub == 0);
        int cdw = full ? dw : (dw + 1) >> 1;
        var yr = HScale8(p.Y, p.W, p.H, p.W, dw, Inc(p.W, dw));
        var ur = HScale8(p.U, p.CW, p.CH, p.CW, cdw, Inc(p.CW, cdw)); var vr = HScale8(p.V, p.CW, p.CH, p.CW, cdw, Inc(p.CW, cdw));
        return Finish(yr, ur, vr, p.W, p.H, p.CW, p.CH, dw, dh, cdw, true);
    }

    /// <summary>rgb24 to rgb24 at dw x dh: swscale converts to YCbCr (BT.601 limited), scales, and converts back.</summary>
    public static Rgb ScaleRgb(Rgb im, int dw, int dh)
    {
        int sw = im.W, sh = im.H; bool full = true; // rgb sources have no chroma subsampling, so swscale forces full chroma
        int cdw = dw; bool halfIn = sw % 2 == 0 && dw <= (sw >> 1);
        int ry = (int)(0.299 * 219 / 255 * 32768 + 0.5), gy = (int)(0.587 * 219 / 255 * 32768 + 0.5), by = (int)(0.114 * 219 / 255 * 32768 + 0.5);
        int ru = -(int)(0.169 * 224 / 255 * 32768 + 0.5), gu = -(int)(0.331 * 224 / 255 * 32768 + 0.5), bu = (int)(0.500 * 224 / 255 * 32768 + 0.5);
        int rv = (int)(0.500 * 224 / 255 * 32768 + 0.5), gv = -(int)(0.419 * 224 / 255 * 32768 + 0.5), bv = -(int)(0.081 * 224 / 255 * 32768 + 0.5);
        var Y = new short[sw * sh]; int cw = halfIn ? sw >> 1 : sw; var U = new short[cw * sh]; var V = new short[cw * sh]; var d = im.Data;
        Parallel.For(0, sh, y =>
        {
            for (int x = 0; x < sw; x++) { int o = (y * sw + x) * 3; Y[y * sw + x] = (short)((ry * d[o] + gy * d[o + 1] + by * d[o + 2] + (32 << 14) + (1 << 8)) >> 9); }
            for (int x = 0; x < cw; x++)
            {
                if (halfIn)
                {
                    int o = (y * sw + 2 * x) * 3; int r = d[o] + d[o + 3], g = d[o + 1] + d[o + 4], b = d[o + 2] + d[o + 5];
                    U[y * cw + x] = (short)((ru * r + gu * g + bu * b + (256 << 15) + (1 << 9)) >> 10); V[y * cw + x] = (short)((rv * r + gv * g + bv * b + (256 << 15) + (1 << 9)) >> 10);
                }
                else
                {
                    int o = (y * sw + x) * 3; int r = d[o], g = d[o + 1], b = d[o + 2];
                    U[y * cw + x] = (short)((ru * r + gu * g + bu * b + (256 << 14) + (1 << 8)) >> 9); V[y * cw + x] = (short)((rv * r + gv * g + bv * b + (256 << 14) + (1 << 8)) >> 9);
                }
            }
        });
        var yr = HScale16(Y, sw, sh, sw, dw, Inc(sw, dw), 13); var ur = HScale16(U, cw, sh, cw, cdw, Inc(cw, cdw), 13); var vr = HScale16(V, cw, sh, cw, cdw, Inc(cw, cdw), 13);
        return Finish(yr, ur, vr, sw, sh, cw, sh, dw, dh, cdw, false);
    }

    static Rgb Finish(short[] yr, short[] ur, short[] vr, int sw, int sh, int cw, int chh, int dw, int dh, int cdw, bool fullRange)
    {
        bool fullChroma = cdw == dw;
        var (vyF, vyP, vyS) = InitFilter(Inc(sh, dh), sh, dh, 1 << 12, LocalPos, LocalPos);
        var (vcF, vcP, vcS) = InitFilter(Inc(chh, dh), chh, dh, 1 << 12, LocalPos, LocalPos);
        var o = new byte[dw * dh * 3];
        // colour tables (ff_yuv2rgb_c_init_tables, BT.601 / default coefficients)
        long crv = 104597, cbu = 132201, cgu = -25675, cgv = -53279, cy = 1 << 16, oy = 0;
        if (!fullRange) { cy = cy * 255 / 219; oy = 16 << 16; } else { crv = crv * 224 / 255; cbu = cbu * 224 / 255; cgu = cgu * 224 / 255; cgv = cgv * 224 / 255; }
        long yCoeff = RoundToInt16(cy * (1 << 13)), yOff = RoundToInt16(oy * (1 << 9)), v2r = RoundToInt16(crv * (1 << 13)), v2g = RoundToInt16(cgv * (1 << 13)), u2g = RoundToInt16(cgu * (1 << 13)), u2b = RoundToInt16(cbu * (1 << 13));
        long tcrv = ((crv << 16) + 0x8000) / Math.Max(cy, 1), tcbu = ((cbu << 16) + 0x8000) / Math.Max(cy, 1), tcgu = ((cgu << 16) + 0x8000) / Math.Max(cy, 1), tcgv = ((cgv << 16) + 0x8000) / Math.Max(cy, 1);
        int yoffs = (fullRange ? 384 : 326) + Headroom, plane = 1024 + 2 * Headroom; var yt = new byte[plane]; long yb = -(384L << 16) - Headroom * cy - oy;
        for (int i = 0; i < plane; i++) { yt[i] = Clip8((int)Math.Clamp((yb + 0x8000) >> 16, -1000, 1000)); yb += cy; }
        int T(int idx) => yt[Math.Clamp(idx, 0, plane - 1)];
        Parallel.For(0, dh, y =>
        {
            int pyBase = vyP[y], pcBase = vcP[y]; var lum = new int[dw]; var cu = new int[cdw]; var cvv = new int[cdw];
            if (fullChroma)
            {   // yuv2rgb_full_X_c: 10-bit intermediates, coefficients applied per pixel
                for (int x = 0; x < dw; x++)
                {
                    int Y = 1 << 9, U = (1 << 9) - (128 << 19), V = (1 << 9) - (128 << 19);
                    for (int j = 0; j < vyS; j++) Y += yr[(pyBase + j) * dw + x] * vyF[y * vyS + j];
                    for (int j = 0; j < vcS; j++) { U += ur[(pcBase + j) * cdw + x] * vcF[y * vcS + j]; V += vr[(pcBase + j) * cdw + x] * vcF[y * vcS + j]; }
                    Y >>= 10; U >>= 10; V >>= 10;
                    Y -= (int)yOff; Y *= (int)yCoeff; Y += 1 << 21;
                    int R = Y + V * (int)v2r, G = Y + V * (int)v2g + U * (int)u2g, B = Y + U * (int)u2b;
                    if (((R | G | B) & 0xC0000000) != 0) { R = Math.Clamp(R, 0, (1 << 30) - 1); G = Math.Clamp(G, 0, (1 << 30) - 1); B = Math.Clamp(B, 0, (1 << 30) - 1); }
                    int q = (y * dw + x) * 3; o[q] = (byte)(R >> 22); o[q + 1] = (byte)(G >> 22); o[q + 2] = (byte)(B >> 22);
                }
            }
            else
            {   // yuv2rgb_X_c_template: 8-bit Y per pixel, U/V per pixel pair, table lookups
                for (int i = 0; i < ((dw + 1) >> 1); i++)
                {
                    int Y1 = 1 << 18, Y2 = 1 << 18, U = 1 << 18, V = 1 << 18;
                    for (int j = 0; j < vyS; j++) { int f = vyF[y * vyS + j]; Y1 += yr[(pyBase + j) * dw + i * 2] * f; Y2 += yr[(pyBase + j) * dw + i * 2 + 1] * f; }
                    for (int j = 0; j < vcS; j++) { int f = vcF[y * vcS + j]; U += ur[(pcBase + j) * cdw + i] * f; V += vr[(pcBase + j) * cdw + i] * f; }
                    Y1 >>= 19; Y2 >>= 19; U >>= 19; V >>= 19;
                    int cV = Clip8(V), cU = Clip8(U);
                    int rOff = yoffs - (int)(tcrv >> 9) + (int)((cV * tcrv) >> 16), bOff = yoffs - (int)(tcbu >> 9) + (int)((cU * tcbu) >> 16);
                    int gOff = yoffs - (int)(tcgu >> 9) + (int)((cU * tcgu) >> 16) - (int)(tcgv >> 9) + (int)((cV * tcgv) >> 16);
                    int q = (y * dw + i * 2) * 3;
                    o[q] = (byte)T(rOff + Y1); o[q + 1] = (byte)T(gOff + Y1); o[q + 2] = (byte)T(bOff + Y1);
                    if (i * 2 + 1 < dw) { o[q + 3] = (byte)T(rOff + Y2); o[q + 4] = (byte)T(gOff + Y2); o[q + 5] = (byte)T(bOff + Y2); }
                }
            }
        });
        return new Rgb(o, dw, dh);
    }
}
