using System.Text.Json.Nodes;
using Xunit;

namespace IndexNode.Tests;

public class CastOrderTests
{
    static JsonObject C(int id, string role, double secs) => new() { ["id"] = "c" + id, ["faceCluster"] = id, ["role"] = role, ["screenSeconds"] = secs };
    static JsonObject R(int from, int to, string kind) => new() { ["from"] = from, ["to"] = to, ["kind"] = kind };

    [Fact]
    public void Leads_then_main_by_screen_time_then_kin_of_leads_by_lead_then_kin_of_main_then_the_rest_by_screen_time()
    {
        var cast = new JsonArray(
            C(9, "supporting", 500),            // the most screen time, but no kin: last group, first of it
            C(8, "supporting", 10),             // a cousin of the main rival: group 4
            C(7, "supporting", 300),            // the male lead's sibling
            C(6, "supporting", 200),            // the female lead's grandmother
            C(5, "supporting", 20),             // the female lead's parent
            C(4, "main", 40),                   // fewer seconds than c3: after it
            C(3, "main", 50),                   // most screen time of the main cast: first
            C(2, "male lead", 90),
            C(1, "female lead", 100),
            C(0, "supporting", 5));
        var rel = new JsonArray(R(6, 1, "grandparent"), R(1, 5, "child"), R(7, 2, "sibling"), R(8, 4, "cousin"), R(0, 9, "friend"));
        var o = Publish.OrderCast(cast, rel);
        Assert.Equal(new[] { "c1", "c2", "c3", "c4", "c6", "c5", "c7", "c8", "c9", "c0" }, o.Select(x => (string)x!["id"]!).ToArray());
        string? G(string id) => o.First(x => (string)x!["id"]! == id)!["castGroup"] is JsonValue v ? (string)v! : null;
        Assert.Equal("c1", G("c6")); Assert.Equal("c1", G("c5")); Assert.Equal("c2", G("c7")); Assert.Equal("c4", G("c8"));
        Assert.Null(G("c9")); Assert.Null(G("c0"));
    }

    [Fact]
    public void Dual_leads_both_come_first_and_a_friend_relation_is_not_kin()
    {
        var o = Publish.OrderCast(new JsonArray(C(1, "supporting", 80), C(2, "female lead", 10), C(3, "female lead", 20)), new JsonArray(R(1, 2, "friend")));
        Assert.Equal(new[] { "c3", "c2", "c1" }, o.Select(x => (string)x!["id"]!).ToArray()); Assert.Null(o[2]!["castGroup"]);
    }
}
