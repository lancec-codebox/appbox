using System.Text.Json.Nodes;
using Xunit;

namespace IndexNode.Tests;

/// <summary>0.13.38: every actor has an entry (the index head's counts and one row per actor in the media table); the AI stages come after every processing stage of the upload.</summary>
public class ActorRowsAndAiOrderTests
{
    static (string Id, string ETag, List<ActorsIndex.IUnit> Units) Up(string id, params (string Actor, string Prod, int N)[] units) =>
        (id, "e", units.Select((u, i) => new ActorsIndex.IUnit($"{id}#{i}", u.Actor, u.Prod, u.N, 0, 1)).ToList());

    [Fact]
    public void Stats_count_each_actor_once_with_its_productions_uploads_units_and_faces()
    {
        var ups = new[] { Up("u1", ("A1", "p1", 10), ("A2", "p1", 5), ("A1", "p1", 3)), Up("u2", ("A1", "p2", 7), ("A3", "p2", 1)), Up("u3", ("A3", "p2", 2)) };
        var s = ActorRows.Stats(ups);
        Assert.Equal(3, s.Count);
        Assert.Equal(new ActorRows.Stat(2, 2, 3, 20), s["A1"]);   // 2 productions, 2 uploads, 3 units, 20 faces
        Assert.Equal(new ActorRows.Stat(1, 1, 1, 5), s["A2"]);
        Assert.Equal(new ActorRows.Stat(1, 2, 2, 3), s["A3"]);    // two uploads of the same production: one production
        Assert.Equal(1, ActorRows.Multi(s));
    }

    [Fact]
    public async Task Rows_only_the_changed_actors_are_written_and_a_merged_actor_keeps_its_row_with_no_units()
    {
        var before = ActorRows.Stats([Up("u1", ("A1", "p1", 10), ("A2", "p1", 5)), Up("u2", ("A9", "p2", 4))]);
        var now = ActorRows.Stats([Up("u1", ("A1", "p1", 10), ("A2", "p1", 5)), Up("u2", ("A1", "p2", 4)), Up("u3", ("A4", "p3", 1))]);   // A9 was merged into A1; A4 is new; A2 unchanged
        var rows = ActorRows.Rows(before, now, 7);
        Assert.Equal(new[] { "A1", "A4", "A9" }, rows.Select(r => r.RowKey).ToArray());
        Assert.Equal(0, (int)rows.Single(r => r.RowKey == "A9").Props["units"]!);
        Assert.Equal(2, (int)rows.Single(r => r.RowKey == "A1").Props["prods"]!);
        Assert.Equal(3, ActorRows.Rows(null, now, 7).Count);   // no earlier index: every actor
        var t = new FakeMediaTable();
        await t.InsertOrMergeAsync(ActorRows.Partition, "A1", new JsonObject { ["v_abc"] = "{\"name\":\"kept\"}" }, default);   // a name vote already on the row
        int reqs = await ActorRows.WriteAsync(t, rows, default);
        Assert.Equal(1, reqs);
        var a1 = (await t.GetAsync(ActorRows.Partition, "A1", null, default))!;
        Assert.Equal(2, a1.Num("prods")); Assert.Equal(7, a1.Num("ver")); Assert.NotNull(a1.Str("v_abc"));   // a merge: the vote stays
        var many = Enumerable.Range(0, 250).Select(i => ("B" + i.ToString("D3"), new JsonObject { ["units"] = 1 })).ToList();
        Assert.Equal(3, await ActorRows.WriteAsync(t, many, default));   // 100 rows per request
    }

    static QSubject Subject(params (string Stage, string State, string? Hold, int? Ver)[] entries)
    {
        var s = new QSubject { Id = "u1", Data = "full" };
        foreach (var (stage, state, hold, ver) in entries) s.Entries[stage] = new QEntry { Stage = stage, State = state, Hold = hold, Ver = ver, Due = "", Added = "2026-01-01T00:00:00Z" };
        return s;
    }

    [Fact]
    public void Describe_waits_for_group_and_actors_an_ai_held_actors_entry_counts()
    {
        var ro = new ReadyOptions { Required = x => x == "actors" ? 2 : 0 };
        QEntry D(QSubject s) => s.Entries["describe"];
        var none = Subject(("process", "done", null, null), ("describe", "pending", null, null));
        Assert.Equal("group", StageDeps.AiWait(none, D(none), ro));
        var grouped = Subject(("process", "done", null, null), ("group", "done", null, null), ("describe", "pending", null, null));
        Assert.Equal("actors", StageDeps.AiWait(grouped, D(grouped), ro));
        var oldActors = Subject(("process", "done", null, null), ("group", "done", null, null), ("actors", "done", null, 1), ("describe", "pending", null, null));
        Assert.Equal("actors", StageDeps.AiWait(oldActors, D(oldActors), ro));   // done below the required version: not yet
        var ready = Subject(("process", "done", null, null), ("group", "done", null, null), ("actors", "done", null, 2), ("describe", "pending", null, null));
        Assert.Null(StageDeps.AiWait(ready, D(ready), ro));
        var held = Subject(("process", "done", null, null), ("group", "done", null, null), ("actors", "pending", "ai", null), ("describe", "pending", null, null));
        Assert.Null(StageDeps.AiWait(held, D(held), ro));   // an AI-flagged upload never gets actors: describe does not wait for it
        Assert.Null(StageDeps.AiWait(ready, ready.Entries["process"], ro));   // only describe is gated (translate follows describe)
    }

    [Fact]
    public void A_claim_does_not_take_describe_before_its_processing_stages_are_done()
    {
        var ro = new ReadyOptions { Required = x => x == "actors" ? 2 : 0 }; const string now = "2026-11-23T00:00:00Z";
        string[]? Claim(QSubject s) => StageDeps.ClaimStages(new QueueState(), s, now, _ => true, (_, _) => true, ro);
        var early = Subject(("download", "done", null, null), ("process", "done", null, null), ("group", "done", null, null), ("describe", "pending", null, null));
        Assert.DoesNotContain("describe", Claim(early) ?? []);
        var ready = Subject(("download", "done", null, null), ("process", "done", null, null), ("group", "done", null, null), ("actors", "done", null, 2), ("describe", "pending", null, null));
        Assert.Contains("describe", Claim(ready) ?? []);
    }
}
