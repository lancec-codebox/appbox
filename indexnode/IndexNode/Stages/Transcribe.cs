using System.Diagnostics;
using System.Globalization;
using System.Text;
using SherpaOnnx;

namespace IndexNode;

public sealed record AsrChar(double T, string Text);
public sealed record AsrSegment(double Start, double End, string Text, string Language, IReadOnlyList<AsrChar> Chars);

/// <summary>The transcribe step: 16 kHz mono audio, Silero VAD, SenseVoice-Small int8, through the sherpa-onnx .NET package.</summary>
public static class Transcribe
{
    // The managed wrapper does not expose SenseVoice's language; the C API gives the whole result as JSON (with "lang").
    [System.Runtime.InteropServices.DllImport("sherpa-onnx-c-api", CallingConvention = System.Runtime.InteropServices.CallingConvention.Cdecl)]
    private static extern IntPtr SherpaOnnxGetOfflineStreamResultAsJson(IntPtr stream);
    [System.Runtime.InteropServices.DllImport("sherpa-onnx-c-api", CallingConvention = System.Runtime.InteropServices.CallingConvention.Cdecl)]
    private static extern void SherpaOnnxDestroyOfflineStreamResultJson(IntPtr json);

    /// <summary>The "lang" of the stream's result ("&lt;|zh|&gt;"), or "" when the native call is unavailable.</summary>
    private static string NativeLang(OfflineStream st)
    {
        try
        {
            var ptr = SherpaOnnxGetOfflineStreamResultAsJson(st.Handle);
            if (ptr == IntPtr.Zero) return "";
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(System.Runtime.InteropServices.Marshal.PtrToStringUTF8(ptr) ?? "{}");
                return doc.RootElement.TryGetProperty("lang", out var l) ? l.GetString() ?? "" : "";
            }
            finally { SherpaOnnxDestroyOfflineStreamResultJson(ptr); }
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException) { return ""; }
    }

    public const string ModelName = "sensevoice";
    public const int SampleRate = 16000;
    /// <summary>A video is dubbed when more than this share of its speech time is in a language other than Chinese.</summary>
    public const double DubbedShare = 0.5;

    /// <summary>"&lt;|zh|&gt;" -> "zh"; Cantonese ("yue") counts as Chinese for the dubbing rule, see <see cref="IsChinese"/>.</summary>
    public static string LangCode(string? raw)
    {
        var s = (raw ?? "").Trim().Trim('<', '>', '|').ToLowerInvariant();
        return s.Length == 0 || s == "nospeech" ? "und" : s;
    }

    public static bool IsChinese(string lang) => lang is "zh" or "yue";

    /// <summary>
    /// The dubbing rule: the majority language by speech time is <c>speechLanguage</c> (Cantonese is reported as "zh" there, since it is
    /// the same written drama); the video is <c>dubbed</c> when more than half of the speech time is not Chinese. Segments with no
    /// detected language are left out of both counts.
    /// </summary>
    public static (string SpeechLanguage, bool Dubbed) Dubbing(IEnumerable<AsrSegment> segments)
    {
        var secs = new Dictionary<string, double>();
        foreach (var s in segments)
        {
            var l = s.Language; if (l == "und" || l.Length == 0) continue;
            if (l == "yue") l = "zh";
            secs[l] = secs.GetValueOrDefault(l) + Math.Max(0, s.End - s.Start);
        }
        double total = secs.Values.Sum();
        if (total <= 0) return ("und", false);
        var majority = secs.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal).First().Key;
        double nonZh = total - secs.GetValueOrDefault("zh");
        return (majority, nonZh / total > DubbedShare);
    }

    /// <summary>The transcript as shaped cues (<see cref="CueShaper"/>): per-character times from the recogniser tokens where they line up with the text.</summary>
    public static List<ShapeCue> Shape(IEnumerable<AsrSegment> segs, IReadOnlyCollection<string>? glossary = null) =>
        CueShaper.Shape(segs.Select(s => { var t = s.Text.Trim(); return new ShapeCue(s.Start, s.End, t, s.Chars.Count > 0 ? CueShaper.AlignTimes(t, s.Chars.Select(c => (c.T, c.Text)).ToList()) : null); }), glossary);

    public static string Vtt(IEnumerable<AsrSegment> segs) => CueShaper.ToVtt(Shape(segs));

    public static string RawVtt(IEnumerable<AsrSegment> segs)
    {
        var sb = new StringBuilder("WEBVTT\n\n");
        foreach (var s in segs.Where(s => s.Text.Trim().Length > 0))
            sb.Append(OcrCues.Timestamp(s.Start)).Append(" --> ").Append(OcrCues.Timestamp(Math.Max(s.End, s.Start + 0.05))).Append('\n').Append(s.Text.Trim()).Append("\n\n");
        return sb.ToString();
    }

    public static byte[] ToJson(IReadOnlyList<AsrSegment> segs, string speechLanguage, bool dubbed, double audioSeconds, double wallSeconds) =>
        System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new
        {
            v = 1, model = ModelName, modelFile = Models.AsrModel.File, vad = "silero", language = speechLanguage, dubbed, shape = CueShaper.Stats(Shape(segs)).ToJson(),
            audioSeconds = Math.Round(audioSeconds, 1), seconds = Math.Round(wallSeconds, 1),
            segments = segs.Select(s => new
            {
                start = s.Start, end = s.End, text = s.Text, language = s.Language,
                words = s.Chars.Select(c => new object[] { c.T, c.Text }),
            }),
        });

    /// <summary>16 kHz mono float samples of [start, start+duration) of the file, through ffmpeg (nothing is kept on disk).</summary>
    public static async Task<float[]> ReadAudioAsync(Tools tools, string file, double start, double duration, CancellationToken ct)
    {
        var ffmpeg = await tools.FfmpegAsync(ct);
        var psi = new ProcessStartInfo(ffmpeg) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        var args = new List<string> { "-hide_banner", "-loglevel", "error", "-nostdin", "-threads", "1" };
        if (start > 0) { args.Add("-ss"); args.Add(start.ToString("F3", CultureInfo.InvariantCulture)); }
        if (duration > 0) { args.Add("-t"); args.Add(duration.ToString("F3", CultureInfo.InvariantCulture)); }
        args.AddRange(["-i", file, "-vn", "-ac", "1", "-ar", "16000", "-f", "f32le", "-"]);
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        using var reg = ct.Register(() => Proc.Kill(p));
        var err = p.StandardError.ReadToEndAsync();
        using var ms = new MemoryStream();
        await p.StandardOutput.BaseStream.CopyToAsync(ms, ct);
        await p.WaitForExitAsync(CancellationToken.None);
        ct.ThrowIfCancellationRequested();
        if (p.ExitCode != 0) throw new InvalidOperationException("ffmpeg audio extraction failed: " + (await err).Trim());
        var bytes = ms.GetBuffer(); int n = (int)ms.Length / 4;
        var f = new float[n]; Buffer.BlockCopy(bytes, 0, f, 0, n * 4);
        return f;
    }

    private static readonly Dictionary<string, Lazy<OfflineRecognizer>> recognisers = new();
    /// <summary>The SenseVoice recogniser, loaded once per process (per model file and thread count) and used by every worker at once: sherpa-onnx's OfflineRecognizer.Decode takes a stream of its own per call and the ONNX Runtime session under it allows concurrent runs, so no lock. Never disposed by the caller.</summary>
    public static OfflineRecognizer SharedRecognizer(string modelPath, string tokensPath, int threads)
    {
        var key = $"{Path.GetFullPath(modelPath)}|{Math.Max(1, threads)}";
        Lazy<OfflineRecognizer> l;
        lock (recognisers) { if (!recognisers.TryGetValue(key, out l!)) recognisers[key] = l = new Lazy<OfflineRecognizer>(() => NewRecognizer(modelPath, tokensPath, threads), LazyThreadSafetyMode.ExecutionAndPublication); }
        return l.Value;
    }

    /// <summary>Disposes the shared recognisers (a batch does this after the transcribe stage).</summary>
    public static int UnloadRecognizers()
    {
        List<OfflineRecognizer> rs = new();
        lock (recognisers) { foreach (var l in recognisers.Values) if (l.IsValueCreated) rs.Add(l.Value); recognisers.Clear(); }
        foreach (var r in rs) try { r.Dispose(); } catch { }
        return rs.Count;
    }

    static OfflineRecognizer NewRecognizer(string modelPath, string tokensPath, int threads)
    {
        var rc = new OfflineRecognizerConfig();
        rc.ModelConfig.SenseVoice.Model = modelPath;
        rc.ModelConfig.SenseVoice.Language = "auto";
        rc.ModelConfig.SenseVoice.UseInverseTextNormalization = 1;
        rc.ModelConfig.Tokens = tokensPath;
        rc.ModelConfig.NumThreads = Math.Max(1, threads);
        rc.ModelConfig.Provider = "cpu";
        rc.ModelConfig.Debug = 0;
        return new OfflineRecognizer(rc);
    }

    static VoiceActivityDetector NewVad(string vadPath)
    {
        var vc = new VadModelConfig();
        vc.SileroVad.Model = vadPath;
        vc.SileroVad.Threshold = 0.5f;
        vc.SileroVad.MinSilenceDuration = 0.5f;
        vc.SileroVad.MinSpeechDuration = 0.25f;
        vc.SileroVad.MaxSpeechDuration = 20f;
        vc.SampleRate = SampleRate;
        vc.NumThreads = 1;
        return new VoiceActivityDetector(vc, 60);
    }

    /// <summary>The segment's result as an AsrSegment (null when it read nothing): character times are the segment start plus the token times.</summary>
    static AsrSegment? ToSegment(OfflineStream st, double start, double end)
    {
        var r = st.Result; var text = r.Text.Trim();
        if (text.Length == 0) return null;
        var chars = new List<AsrChar>();
        if (r.Tokens != null && r.Timestamps != null)
            for (int i = 0; i < Math.Min(r.Tokens.Length, r.Timestamps.Length); i++) chars.Add(new AsrChar(Math.Round(start + r.Timestamps[i], 3), r.Tokens[i]));
        return new AsrSegment(Math.Round(start, 3), Math.Round(end, 3), text, LangCode(NativeLang(st)), chars);
    }

    /// <summary>
    /// The parallel transcribe: voice detection runs on <paramref name="workers"/> pieces of the audio at once (each piece read 25 s past its end, longer
    /// than the longest segment, so a sentence crossing a cut is finished by the piece it started in; the next piece drops what it overlaps), then
    /// <paramref name="workers"/> recognisers of <paramref name="threadsPer"/> threads take the segments from one queue, <paramref name="batch"/> at a time.
    /// Same models and settings as <see cref="Run"/>.
    /// </summary>
    /// <summary>Voice detection in parallel: each piece's detector starts this long before the piece (grid-aligned), and pieces are at least PieceSeconds long.
    /// On the 10-min window (1-min pieces): no warm-up 18.6% CER against the burned-in subtitles, 60 s 17.7%, one sequential pass 17.1%.</summary>
    public static int WarmUpSeconds = 60, PieceSeconds = 600;
    public static bool SharedModel = true; // one loaded model serves every worker (same text as separate copies, no 5 s / 3 GB of loading)

    /// <summary>The audio of the whole file read by <paramref name="parts"/> ffmpeg processes at once (each seeks to its part), joined.</summary>
    public static async Task<float[]> ReadAudioParallelAsync(Tools tools, string file, double duration, int parts, CancellationToken ct)
    {
        parts = Math.Max(1, Math.Min(parts, (int)(duration / 60)));
        if (parts == 1) return await ReadAudioAsync(tools, file, 0, 0, ct);
        var cuts = Enumerable.Range(0, parts + 1).Select(k => Math.Round(duration * k / parts)).ToArray();
        var got = await Task.WhenAll(Enumerable.Range(0, parts).Select(k => ReadAudioAsync(tools, file, cuts[k], k == parts - 1 ? 0 : cuts[k + 1] - cuts[k], ct)));
        // each part is cut to exactly its sample count (the last keeps what it has), so the parts join without a gap or an overlap
        var lens = Enumerable.Range(0, parts).Select(k => k == parts - 1 ? got[k].Length : Math.Min(got[k].Length, (int)((cuts[k + 1] - cuts[k]) * SampleRate))).ToArray();
        var all = new float[lens.Sum()]; int o = 0;
        for (int k = 0; k < parts; k++) { Array.Copy(got[k], 0, all, o, lens[k]); o += lens[k]; }
        return all;
    }

    public static List<AsrSegment> RunParallel(string modelPath, string tokensPath, string vadPath, float[] audio, int workers, int threadsPer, int batch, Action<string> progress, CancellationToken ct, double offset = 0)
    {
        const int window = 512; int overlap = 25 * SampleRate, warm = WarmUpSeconds * SampleRate; var wall = Stopwatch.StartNew();
        // pieces of at least PieceSeconds: the detector carries memory across the track, so each cut changes the segmentation for a while after it
        int pieces = Math.Max(1, Math.Min(workers, audio.Length / (PieceSeconds * SampleRate)));
        var bounds = Enumerable.Range(0, pieces + 1).Select(k => k == pieces ? audio.Length : (int)((long)audio.Length * k / pieces / window * window)).ToArray();
        // the recognisers load while voice detection runs (12 copies of the 234 MB model took ~5.5 s)
        // SharedModel: one loaded recogniser, called from every worker at once (ONNX Runtime sessions allow concurrent runs; each stream is its own)
        int nrec = SharedModel ? 1 : Math.Max(1, workers); var recs = new OfflineRecognizer[nrec]; long loadTicks = Stopwatch.GetTimestamp();
        var loading = Task.Run(() => Parallel.For(0, nrec, i => recs[i] = SharedModel ? SharedRecognizer(modelPath, tokensPath, threadsPer) : NewRecognizer(modelPath, tokensPath, threadsPer)), ct);
        var found = new List<(int Start, float[] Samples)>[pieces];
        Parallel.For(0, pieces, new ParallelOptions { MaxDegreeOfParallelism = pieces, CancellationToken = ct }, k =>
        {
            using var vad = NewVad(vadPath); var list = found[k] = []; var chunk = new float[window];
            // the detector starts WarmUpSeconds before the piece (its memory settles; what it finds there is dropped) and reads 25 s past the piece's end
            int s0 = Math.Max(0, bounds[k] - warm) / window * window, s1 = k == pieces - 1 ? audio.Length : Math.Min(audio.Length, bounds[k + 1] + overlap);
            void Drain() { while (!vad.IsEmpty()) { var seg = vad.Front(); vad.Pop(); int st = s0 + (int)seg.Start; if (st >= bounds[k] && (k == pieces - 1 || st < bounds[k + 1])) list.Add((st, seg.Samples)); } }
            for (int pos = s0; pos < s1; pos += window)
            {
                ct.ThrowIfCancellationRequested();
                int n = Math.Min(window, s1 - pos);
                if (n < window) { Array.Clear(chunk); Array.Copy(audio, pos, chunk, 0, n); } else Array.Copy(audio, pos, chunk, 0, window);
                vad.AcceptWaveform(chunk); Drain();
            }
            vad.Flush(); Drain();
        });
        var segs = new List<(int Start, float[] Samples)>();
        foreach (var piece in found)
            foreach (var s in piece.OrderBy(x => x.Start))
                if (segs.Count == 0 || s.Start >= segs[^1].Start + segs[^1].Samples.Length) segs.Add(s); // a piece's first segment may be the tail of the previous piece's last one
        double vadSecs = wall.Elapsed.TotalSeconds, speech = segs.Sum(s => s.Samples.Length) / (double)SampleRate;
        progress($"voice detection: {segs.Count} segments, {speech:F0}s of speech in {vadSecs:F1}s ({pieces} pieces)");

        loading.Wait(ct); double loadSecs = (Stopwatch.GetTimestamp() - loadTicks) / (double)Stopwatch.Frequency, waitLoad = wall.Elapsed.TotalSeconds - vadSecs;
        var res = new AsrSegment?[segs.Count]; int next = -1, done = 0; batch = Math.Max(1, batch); long decodeSum = 0;
        Parallel.For(0, Math.Max(1, workers), new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, workers), CancellationToken = ct }, w =>
        {
            var rec = recs[SharedModel ? 0 : w];
            while (true)
            {
                var idx = new List<int>(); int i;
                while (idx.Count < batch && (i = Interlocked.Increment(ref next)) < segs.Count) idx.Add(i);
                if (idx.Count == 0) break;
                ct.ThrowIfCancellationRequested();
                var streams = idx.Select(j => { var st = rec.CreateStream(); st.AcceptWaveform(SampleRate, segs[j].Samples); return st; }).ToList();
                long d0 = Stopwatch.GetTimestamp(); if (streams.Count == 1) rec.Decode(streams[0]); else rec.Decode(streams); Interlocked.Add(ref decodeSum, Stopwatch.GetTimestamp() - d0);
                for (int q = 0; q < idx.Count; q++)
                {
                    var (s0, sm) = segs[idx[q]]; double start = offset + (double)s0 / SampleRate;
                    res[idx[q]] = ToSegment(streams[q], start, start + (double)sm.Length / SampleRate);
                    streams[q].Dispose();
                }
                int n = Interlocked.Add(ref done, idx.Count);
                if (n / 200 != (n - idx.Count) / 200 || n == segs.Count) progress($"recognition: {n}/{segs.Count} segments");
            }
        });
        if (!SharedModel) foreach (var rr in recs) rr.Dispose(); // the shared one lives as long as the process
        Log.Info($"transcribe: {(SharedModel ? "one shared model, " : "")}voice detection {vadSecs:F1}s ({pieces} pieces), recognition {wall.Elapsed.TotalSeconds - vadSecs:F1}s ({segs.Count} segments, {speech:F0}s of speech; {workers} recognisers x {threadsPer} thread(s), batch {batch}), wall {wall.Elapsed.TotalSeconds:F1}s; model load {loadSecs:F1}s (alongside voice detection; {waitLoad:F1}s waited after it), decode time summed over recognisers {decodeSum / (double)Stopwatch.Frequency:F1}s");
        return res.Where(r => r != null).Select(r => r!).ToList();
    }

    /// <summary>Segments by Silero VAD, each recognised by SenseVoice; the character times are the segment start plus the token times.</summary>
    public static List<AsrSegment> Run(string modelPath, string tokensPath, string vadPath, float[] audio, int threads, Action<string> progress, CancellationToken ct, double offset = 0)
    {
        var rc = new OfflineRecognizerConfig();
        rc.ModelConfig.SenseVoice.Model = modelPath;
        rc.ModelConfig.SenseVoice.Language = "auto";
        rc.ModelConfig.SenseVoice.UseInverseTextNormalization = 1;
        rc.ModelConfig.Tokens = tokensPath;
        rc.ModelConfig.NumThreads = Math.Max(1, threads);
        rc.ModelConfig.Provider = "cpu";
        rc.ModelConfig.Debug = 0;
        using var rec = new OfflineRecognizer(rc);

        var vc = new VadModelConfig();
        vc.SileroVad.Model = vadPath;
        vc.SileroVad.Threshold = 0.5f;
        vc.SileroVad.MinSilenceDuration = 0.5f;
        vc.SileroVad.MinSpeechDuration = 0.25f;
        vc.SileroVad.MaxSpeechDuration = 20f;
        vc.SampleRate = SampleRate;
        vc.NumThreads = 1;
        using var vad = new VoiceActivityDetector(vc, 60);

        var segs = new List<AsrSegment>();
        long tVad = 0, tRec = 0, tPost = 0; double speech = 0; var wall = Stopwatch.StartNew();
        string Times() => $"voice detection {tVad / (double)Stopwatch.Frequency:F1}s, recognition {tRec / (double)Stopwatch.Frequency:F1}s ({segs.Count} segments, {speech:F0}s of speech), results {tPost / (double)Stopwatch.Frequency:F1}s, wall {wall.Elapsed.TotalSeconds:F1}s";
        void Drain()
        {
            while (!vad.IsEmpty())
            {
                var seg = vad.Front();
                vad.Pop();
                long r0 = Stopwatch.GetTimestamp();
                var st = rec.CreateStream();
                st.AcceptWaveform(SampleRate, seg.Samples);
                rec.Decode(st);
                long r1 = Stopwatch.GetTimestamp(); tRec += r1 - r0; speech += seg.Samples.Length / (double)SampleRate;
                var r = st.Result;
                double start = offset + (double)seg.Start / SampleRate, end = start + (double)seg.Samples.Length / SampleRate;
                var text = r.Text.Trim();
                if (text.Length == 0) continue;
                var chars = new List<AsrChar>();
                if (r.Tokens != null && r.Timestamps != null)
                    for (int i = 0; i < Math.Min(r.Tokens.Length, r.Timestamps.Length); i++) chars.Add(new AsrChar(Math.Round(start + r.Timestamps[i], 3), r.Tokens[i]));
                segs.Add(new AsrSegment(Math.Round(start, 3), Math.Round(end, 3), text, LangCode(NativeLang(st)), chars));
                tPost += Stopwatch.GetTimestamp() - r1;
            }
        }
        const int window = 512; var chunk = new float[window];
        for (int pos = 0; pos < audio.Length; pos += window)
        {
            ct.ThrowIfCancellationRequested();
            int n = Math.Min(window, audio.Length - pos);
            if (n < window) { Array.Clear(chunk); Array.Copy(audio, pos, chunk, 0, n); } else Array.Copy(audio, pos, chunk, 0, window);
            long v0 = Stopwatch.GetTimestamp(); vad.AcceptWaveform(chunk); tVad += Stopwatch.GetTimestamp() - v0;
            if (pos % (window * 1000) == 0) { Drain(); progress($"{(offset + (double)pos / SampleRate) / 60:F1} min of audio; {Times()}"); }
        }
        long f0 = Stopwatch.GetTimestamp(); vad.Flush(); tVad += Stopwatch.GetTimestamp() - f0;
        Drain();
        Log.Info($"transcribe: {Times()}");
        return segs;
    }
}
