using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace IndexNode.Tests;

public class ClassifyAllTests
{
    private static readonly Paths P = new("index-test/");
    private static readonly DateTimeOffset Now = new(2026, 11, 17, 12, 0, 0, TimeSpan.Zero);
    private static string Id(int i) => "v" + i.ToString("D10");

    private sealed class Chat(Func<string, string> answer) : IChat
    {
        public readonly List<string> Prompts = [];
        public Task<string> ChatAsync(string model, JsonNode content, CancellationToken ct) { var p = content.GetValue<string>(); Prompts.Add(p); return Task.FromResult(answer(p)); }
    }

    /// <summary>An AI that calls every upload of the prompt a drama, except ids listed in <paramref name="not"/>.</summary>
    private static Chat Yes(params string[] not) => new(p =>
    {
        var ids = p.Split('\n').Where(l => Regex.IsMatch(l, @"^v\d{10} \|")).Select(l => l[..11]).ToList();
        return "{\"videos\":[" + string.Join(",", ids.Select(i => $"{{\"id\":\"{i}\",\"candidate\":{(not.Contains(i) ? "false" : "true")},\"kind\":\"{(not.Contains(i) ? "recap" : "full")}\",\"language\":\"zh\",\"reason\":\"r\"}}")) + "]}";
    });

    private static JobDescriptor Dl(string id, double? minutes, string title = "A drama", string? channel = "Chan", string? availability = null, string added = "2026-11-17T10:00:00Z")
    {
        var d = JobDescriptor.NewDownload($"https://www.youtube.com/watch?v={id}", 480, null); d.JobId = id; d.AddedAt = added;
        d.Title = title; d.Duration = minutes * 60; d.ChannelName = channel; d.ChannelId = "UC" + new string('a', 22); d.Source = "ch-x"; d.Availability = availability;
        return d;
    }














    [Fact]
    public void Dead_video_detection_uses_the_availability_or_a_placeholder_title_without_duration()
    {
        Assert.True(ClassifyJobs.IsUnavailable(Dl("aaaaaaaaaaa", 60, availability: "private")));
        Assert.True(ClassifyJobs.IsUnavailable(Dl("aaaaaaaaaaa", null, "[Deleted video]")));
        Assert.False(ClassifyJobs.IsUnavailable(Dl("aaaaaaaaaaa", 60, "[Private video]")));             // a real duration: the title alone proves nothing
        Assert.False(ClassifyJobs.IsUnavailable(Dl("aaaaaaaaaaa", null, "Some title", availability: "public")));
    }

    /// <summary>An AI that answers every upload of a prompt except the ones in <paramref name="never"/> (which it never answers), and leaves out <paramref name="first"/> of the prompt's first call.</summary>
    private static Chat Omits(string[] never, string[]? firstOnly = null) { int call = 0; return new(p =>
    {
        var ids = p.Split('\n').Where(l => Regex.IsMatch(l, @"^v\d{10} \|")).Select(l => l[..11]).Where(x => !never.Contains(x)).ToList();
        if (call++ == 0 && firstOnly != null) ids = ids.Where(x => !firstOnly.Contains(x)).ToList();
        return "{\"videos\":[" + string.Join(",", ids.Select(x => $"{{\"id\":\"{x}\",\"candidate\":true,\"kind\":\"full\",\"language\":\"zh\",\"reason\":\"r\"}}")) + "]}";
    }); }


}
