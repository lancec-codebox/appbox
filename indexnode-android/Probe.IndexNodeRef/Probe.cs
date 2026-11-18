namespace Probe;
public static class P { public static string Run() => IndexNode.PlatformInfo.Platform() + " " + IndexNode.Protocol.ApiVersion + " " + IndexNode.Protocol.StepStages.Length; }
