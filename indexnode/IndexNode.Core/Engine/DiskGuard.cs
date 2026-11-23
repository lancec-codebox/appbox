namespace IndexNode;

/// <summary>0.13.34: what counts as a local disk failure, and how much room is left where downloads land.</summary>
public static class DiskGuard
{
    public static Exception Root(Exception e) { while (e.InnerException != null) e = e.InnerException; return e; }
    /// <summary>A full disk or any other write failure of a local file (the HTTP copy of a download into a file wraps the IOException in an HttpRequestException).</summary>
    public static bool IsDiskError(Exception e)
    {
        for (Exception? x = e; x != null; x = x.InnerException)
        {
            if (x is OperationCanceledException) return false;
            if (x is IOException io && x is not (FileNotFoundException or DirectoryNotFoundException or EndOfStreamException) && !IsNetwork(io)) return true;
            if (x is AggregateException ag && ag.InnerExceptions.Any(IsDiskError)) return true;
        }
        return false;
    }
    static bool IsNetwork(IOException io) => io.InnerException is System.Net.Sockets.SocketException || io.GetType().Name.Contains("Http", StringComparison.Ordinal);
    public static long? Free(string path) { try { var full = Path.GetFullPath(path); while (!Directory.Exists(full) && Path.GetDirectoryName(full) is { } up) full = up; return new DriveInfo(full).AvailableFreeSpace; } catch { return null; } }
    public static string Fmt(long? b) => b == null ? "unknown" : b >= 1L << 30 ? (b.Value / (double)(1L << 30)).ToString("F1") + " GB" : (b.Value >> 20) + " MB";
}
