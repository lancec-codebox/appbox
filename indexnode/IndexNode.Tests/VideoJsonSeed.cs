using System.Text;
using System.Text.Json.Nodes;

namespace IndexNode.Tests;

/// <summary>0.13.2: tests that plant an upload give it the video.json its readers now require.</summary>
public static class VideoJsonSeed
{
    public static async Task PutAsync(IStore s, Paths p, string id, double duration = 600, string? language = null, bool? dubbed = null, JsonObject? precheck = null, string title = "t", string channel = "c", double? aiScore = null, bool? aiFlag = null)
    {
        var root = new JsonObject { ["v"] = 1, ["id"] = id, ["youtube"] = new JsonObject { ["title"] = title, ["channel"] = channel, ["durationSeconds"] = duration, ["url"] = "https://www.youtube.com/watch?v=" + id }, ["download"] = new JsonObject { ["v"] = 1, ["durationSeconds"] = duration } };
        if (language != null) root["speech"] = new JsonObject { ["language"] = language, ["dubbed"] = dubbed, ["source"] = "transcribe" };
        if (aiScore != null) root["ai"] = new JsonObject { ["score"] = aiScore, ["flag"] = aiFlag, ["source"] = "precheck" };
        if (precheck != null) root["precheck"] = JsonNode.Parse(precheck.ToJsonString());
        await s.PutAsync(p.Video(id, "video.json"), Encoding.UTF8.GetBytes(root.ToJsonString()), null, null, false, default);
    }
}
