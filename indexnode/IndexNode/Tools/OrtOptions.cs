using Microsoft.ML.OnnxRuntime;

namespace IndexNode;

/// <summary>The one way IndexNode builds ONNX Runtime session options. Spinning is off: with more ORT threads than cores, spin-waiting halves the throughput (OCR measured 13 fps with 16x2 threads against 27 fps with 1 thread each or spinning off). The sum of the intra-op threads of all sessions running at once must stay within the node's thread budget: pass each session its share, not the whole budget.
/// With <see cref="Device"/> = "cuda" every session runs on the CUDA execution provider and the build fails hard instead of falling back to the CPU: appending the provider throws when the CUDA libraries do not load, and session.disable_cpu_ep_fallback makes session creation throw when a graph node would run on the CPU (only in strict mode, INDEXNODE_CUDA_STRICT=1; by default the CUDA provider must load and be registered, and small shape ops may stay on the CPU).</summary>
public static class OrtOptions
{
    /// <summary>"cpu" (default) or "cuda"; set once at start by `run --gpu` / `--device` / INDEXNODE_DEVICE.</summary>
    public static string Device { get; set; } = "cpu";
    public static int DeviceId { get; set; }
    public static bool Cuda => Device == "cuda";
    /// <summary>Strict mode (INDEXNODE_CUDA_STRICT=1): refuse any graph node on the CPU. Default: the CUDA provider must load and be registered, small shape ops may stay on the CPU.</summary>
    public static bool Strict => Environment.GetEnvironmentVariable("INDEXNODE_CUDA_STRICT") == "1";

    public static string ParseDevice(string? v)
    {
        var d = (v ?? "cpu").Trim().ToLowerInvariant();
        return d is "cpu" or "cuda" ? d : throw new ArgumentException($"--device: cpu or cuda (got '{v}')");
    }

    /// <summary>Frames per model call: the CPU size, or the GPU size (64-256 keeps an A100 busy).</summary>
    public static int Batch(int cpu, int gpu) => Cuda ? gpu : cpu;

    public static SessionOptions Make(int intraThreads, bool quiet = false) => Make(intraThreads, quiet, Device, DeviceId);

    public static SessionOptions Make(int intraThreads, bool quiet, string device, int deviceId = 0)
    {
        var so = new SessionOptions { IntraOpNumThreads = Math.Max(1, intraThreads), InterOpNumThreads = 1 };
        so.AddSessionConfigEntry("session.intra_op.allow_spinning", "0");
        so.AddSessionConfigEntry("session.inter_op.allow_spinning", "0");
        if (quiet) so.LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_ERROR;
        if (device == "cuda")
        {
            try
            {
                if (!OrtEnv.Instance().GetAvailableProviders().Contains("CUDAExecutionProvider")) throw new InvalidOperationException("CUDAExecutionProvider is not available in this ONNX Runtime build");
                so.AppendExecutionProvider_CUDA(deviceId);
            }
            catch (Exception e)
            {
                so.Dispose();
                throw new InvalidOperationException($"--device cuda: the ONNX Runtime CUDA provider could not be loaded ({e.Message}). This build needs the GPU package (Microsoft.ML.OnnxRuntime.Gpu.Linux), CUDA 12 and cuDNN 9; refusing to fall back to the CPU.", e);
            }
            if (Strict) so.AddSessionConfigEntry("session.disable_cpu_ep_fallback", "1");
        }
        return so;
    }

    /// <summary>A session on the configured device; logs the provider the first time.</summary>
    public static InferenceSession Session(string path, int intraThreads, bool quiet = false)
    {
        var so = Make(intraThreads, quiet);
        try
        {
            var s = new InferenceSession(path, so);
            LogProvider(Path.GetFileName(path));
            return s;
        }
        catch (OnnxRuntimeException e) when (Cuda) { throw new InvalidOperationException($"--device cuda: {Path.GetFileName(path)} cannot run entirely on the GPU ({e.Message}); refusing to fall back to the CPU. INDEXNODE_CUDA_STRICT=1 is set, which refuses any CPU node.", e); }
        finally { so.Dispose(); }
    }

    private static readonly HashSet<string> logged = new();
    private static void LogProvider(string model)
    {
        lock (logged) if (!logged.Add(Device)) return;
        Log.Info($"onnxruntime: session {model}: provider {(Cuda ? "CUDAExecutionProvider (device " + DeviceId + (Strict ? ", strict: no CPU nodes" : ", small shape ops may stay on CPU") + ")" : "CPUExecutionProvider")}");
    }
}
