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
        void Drain()
        {
            while (!vad.IsEmpty())
            {
                var seg = vad.Front();
                vad.Pop();
                var st = rec.CreateStream();
                st.AcceptWaveform(SampleRate, seg.Samples);
                rec.Decode(st);
                var r = st.Result;
                double start = offset + (double)seg.Start / SampleRate, end = start + (double)seg.Samples.Length / SampleRate;
                var text = r.Text.Trim();
                if (text.Length == 0) continue;
                var chars = new List<AsrChar>();
                if (r.Tokens != null && r.Timestamps != null)
                    for (int i = 0; i < Math.Min(r.Tokens.Length, r.Timestamps.Length); i++) chars.Add(new AsrChar(Math.Round(start + r.Timestamps[i], 3), r.Tokens[i]));
                segs.Add(new AsrSegment(Math.Round(start, 3), Math.Round(end, 3), text, LangCode(NativeLang(st)), chars));
            }
        }
        const int window = 512; var chunk = new float[window];
        for (int pos = 0; pos < audio.Length; pos += window)
        {
            ct.ThrowIfCancellationRequested();
            int n = Math.Min(window, audio.Length - pos);
            if (n < window) { Array.Clear(chunk); Array.Copy(audio, pos, chunk, 0, n); } else Array.Copy(audio, pos, chunk, 0, window);
            vad.AcceptWaveform(chunk);
            if (pos % (window * 1000) == 0) { Drain(); progress($"{(offset + (double)pos / SampleRate) / 60:F0} min"); }
        }
        vad.Flush();
        Drain();
        return segs;
    }
}
