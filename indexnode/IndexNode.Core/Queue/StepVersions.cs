using System.Collections.Concurrent;

namespace IndexNode;

/// <summary>
/// 0.13.32: versions of steps and stages. The CODE version is what this node's code does (a node without the r50 model and the child probe cannot make the cast repair, so it stays at 1); the REQUIRED version lives in control.json (versions: { "cast": 2 }, set with
/// `indexnode control version cast 2`, announced by a ctl line). A done line records the version it finished at (a line without one is v1). A step whose recorded version is below the required one is ready again, in its repair mode, once its own dependencies are met.
/// </summary>
public static class StepVersions
{
    /// <summary>Versioned process steps (re-opened through the process entry) and versioned stages (their own entry).</summary>
    public static readonly string[] Steps = ["cast"], Stages = ["actors"];
    public static readonly string[] All = [.. Steps, .. Stages];
    static readonly ConcurrentDictionary<string, int> code = new(StringComparer.Ordinal);
    public static int Code(string stepOrStage) => code.GetValueOrDefault(stepOrStage, 1);
    public static void SetCode(string stepOrStage, int v) => code[stepOrStage] = v;
    public static void Reset() => code.Clear();
    /// <summary>The version the cast step makes at once the repaired cast state exists (units + r50 files under index/actors/); actors v2 reads only that.</summary>
    public const int CastRepaired = 2, ActorsRepaired = 2;
    /// <summary>0.13.65: actors v3 = every unit compared with every actor of the index (v2 dropped the candidates of every unit after an upload's first once the index passed 32 MB).
    /// Raising the required version to 3 in control re-runs the step for every upload in its repair mode (its own units and r50 files are reused; only the links are made again).</summary>
    public const int ActorsLinked = 3;
}
