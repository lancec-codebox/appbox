namespace IndexNode;

/// <summary>The desktop node's model housekeeping, handed to the engine (the shared engine knows no models).</summary>
public static class DesktopModels
{
    public static void Unload()
    {
        int n = OrtOptions.UnloadShared() + Transcribe.UnloadRecognizers();
        if (n > 0) { GC.Collect(); Log.Info($"unloaded {n} model(s)"); }
    }
}
