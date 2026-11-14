using Microsoft.ML.OnnxRuntime;

namespace IndexNode;

/// <summary>The one way IndexNode builds ONNX Runtime session options. Spinning is off: with more ORT threads than cores, spin-waiting halves the throughput (OCR measured 13 fps with 16x2 threads against 27 fps with 1 thread each or spinning off). The sum of the intra-op threads of all sessions running at once must stay within the node's thread budget: pass each session its share, not the whole budget.</summary>
public static class OrtOptions
{
    public static SessionOptions Make(int intraThreads, bool quiet = false)
    {
        var so = new SessionOptions { IntraOpNumThreads = Math.Max(1, intraThreads), InterOpNumThreads = 1 };
        so.AddSessionConfigEntry("session.intra_op.allow_spinning", "0");
        so.AddSessionConfigEntry("session.inter_op.allow_spinning", "0");
        if (quiet) so.LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_ERROR;
        return so;
    }
}
