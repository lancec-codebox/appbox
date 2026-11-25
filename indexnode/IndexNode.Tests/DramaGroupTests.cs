using System.Text;
using System.Text.Json.Nodes;
using Xunit;

namespace IndexNode.Tests;

public class DramaGroupTests
{
    static readonly PreCheckSettings S = new();
    const double WStart = 0, WLen = 300;
    static PreCheckAnalysis An(int frames, IEnumerable<FrameHit> hits, Dictionary<string, string?>? groups = null, Dictionary<string, IReadOnlyList<GroupCopy>>? members = null, IEnumerable<PreCheckMatch>? top = null) =>
        new(null, 5, frames, 0, top?.ToList() ?? [], 0, new Dictionary<string, double>()) { Hits = hits.ToList(), UploadGroups = groups ?? new(), GroupMembers = members ?? new() };
    // a frame's own time in the window, plus an offset: what a perfect copy shifted by <off> matches
    static FrameHit H(int f, string up, double off, double cos = 0.9, int n = 60) => new(f, up, WStart + (f + 0.5) / n * WLen + off, cos);

    [Fact] public void Join_takes_the_largest_group_at_five_hits()
    {
        var a = An(60, Enumerable.Range(0, 40).Select(f => H(f, "A", 120)).Concat(Enumerable.Range(40, 8).Select(f => H(f, "B", 10))), new() { ["A"] = "g1", ["B"] = "g2" });
        var p = DramaGroups.Propose(a, WStart, WLen, S);
        Assert.Equal("join", p.Kind); Assert.Equal("g1", p.Group); Assert.Equal("A", p.JoinUpload); Assert.False(p.Mixed);
        Assert.Equal(40, p.Hits); Assert.Equal(40 / 60.0, p.Share, 3); Assert.Equal(120, p.Offset, 1);
        Assert.Equal(40 / 60.0, p.MatchShare, 3); Assert.Equal(40 / 60.0, p.DupShare, 3);   // cosine 0.9 clears both bars
    }

    [Fact] public void Join_sums_a_groups_uploads_and_records_the_two_shares()
    {   // four hits in each of two members of one group = 8 >= 5; the group beats a lone upload with 6
        var a = An(60, Enumerable.Range(0, 4).Select(f => H(f, "A", 0, 0.8)).Concat(Enumerable.Range(4, 4).Select(f => H(f, "B", 0, 0.9))).Concat(Enumerable.Range(10, 6).Select(f => H(f, "C", 0))), new() { ["A"] = "g1", ["B"] = "g1", ["C"] = null });
        var p = DramaGroups.Propose(a, WStart, WLen, S);
        Assert.Equal("g1", p.Group); Assert.Equal(8, p.Hits);
        Assert.Equal(8 / 60.0, p.MatchShare, 3); Assert.Equal(4 / 60.0, p.DupShare, 3);   // only B's four reach 0.85
    }

    [Fact] public void A_whole_upload_joins_on_the_hits_in_its_middle_half_and_shared_credits_join_nothing()
    {
        // 0.13.57 (owner, 2026-11-25): 240 frames sampled over a 48-minute episode; frame f is at f * 12 s. The last 20 frames (the closing credits) match another episode of the series.
        const double Len = 2880; double Own(int f) => f * 12.0;
        FrameHit At(int f, string up) => new(f, up, Own(f) - 77, 0.9);
        var credits = An(240, Enumerable.Range(220, 20).Select(f => At(f, "E31")), new() { ["E31"] = "g1" });
        Assert.Equal("new", DramaGroups.Propose(credits, 0, 0, S, Own, 0, Len).Kind);                       // none of the 20 hits is between the 25% and 75% marks
        Assert.NotEqual("new", DramaGroups.Propose(credits, 0, 0, S, Own).Kind);                         // without the length (the pre-check's windows) every hit counts, as before (one contiguous part at one offset: "mixed")
        Assert.NotEqual("new", DramaGroups.Propose(credits, 0, 0, new PreCheckSettings { GroupMiddleFrom = 0, GroupMiddleTo = 1 }, Own, 0, Len).Kind);   // switched off
        // opening titles the same way; and four stray frames in the middle are still under the five a join needs
        var titles = An(240, Enumerable.Range(0, 30).Select(f => At(f, "E31")).Concat(new[] { 100, 110, 120, 130 }.Select(f => At(f, "E31"))), new() { ["E31"] = "g1" });
        Assert.Equal("new", DramaGroups.Propose(titles, 0, 0, S, Own, 0, Len).Kind);
        // a real copy: hits all along. The group is admitted on its 121 middle hits, and then every hit counts for the shares and the offset
        var copy = An(240, Enumerable.Range(0, 240).Select(f => At(f, "A")), new() { ["A"] = "g2" });
        var p = DramaGroups.Propose(copy, 0, 0, S, Own, 0, Len);
        Assert.Equal("join", p.Kind); Assert.Equal("g2", p.Group); Assert.Equal(240, p.Hits); Assert.Equal(1.0, p.Share, 3); Assert.Equal(-77, p.Offset, 1);
        // the group is CHOSEN on the middle hits too: credits shared with g1 (30 frames at the end) do not outvote the drama it copies in the middle (20 frames)
        var both = An(240, Enumerable.Range(210, 30).Select(f => At(f, "E31")).Concat(Enumerable.Range(100, 20).Select(f => At(f, "A"))), new() { ["E31"] = "g1", ["A"] = "g2" });
        var q = DramaGroups.Propose(both, 0, 0, S, Own, 0, Len);
        Assert.Equal("g2", q.Group); Assert.Equal(20, q.Hits);
    }

    [Fact] public void Fewer_than_five_hits_is_new()
    {
        var p = DramaGroups.Propose(An(60, Enumerable.Range(0, 4).Select(f => H(f, "A", 0)), new() { ["A"] = "g1" }), WStart, WLen, S);
        Assert.Equal("new", p.Kind); Assert.Null(p.Group); Assert.Equal(0, p.Hits);
        Assert.Equal("new", DramaGroups.Propose(An(60, []), WStart, WLen, S).Kind);
    }

    [Fact] public void Mixed_is_one_contiguous_part_under_sixty_percent_at_one_offset()
    {
        var a = An(60, Enumerable.Range(20, 20).Select(f => H(f, "A", 900)), new() { ["A"] = "g1" });   // a third of the sample, contiguous, 900 s in
        var p = DramaGroups.Propose(a, WStart, WLen, S);
        Assert.Equal("mixed", p.Kind); Assert.True(p.Mixed); Assert.Equal(900, p.Offset, 1); Assert.Equal("g1", p.Group);
        // scattered hits (gaps) are not one contiguous part
        var scattered = DramaGroups.Propose(An(60, Enumerable.Range(0, 10).Select(f => H(f * 6, "A", 900)), new() { ["A"] = "g1" }), WStart, WLen, S);
        Assert.Equal("join", scattered.Kind); Assert.False(scattered.Mixed);
        // an inconsistent offset is not either
        var wobble = DramaGroups.Propose(An(60, Enumerable.Range(20, 20).Select(f => H(f, "A", f % 2 == 0 ? 0 : 600)), new() { ["A"] = "g1" }), WStart, WLen, S);
        Assert.False(wobble.Mixed);
        // and 60% or more of the samples is a join, not a mixed cut
        Assert.False(DramaGroups.Propose(An(60, Enumerable.Range(0, 40).Select(f => H(f, "A", 900)), new() { ["A"] = "g1" }), WStart, WLen, S).Mixed);
    }

    [Fact] public void Record_and_summary_carry_group_offset_and_both_shares()
    {
        var a = An(60, Enumerable.Range(0, 55).Select(f => H(f, "A", 30)), new() { ["A"] = "g1" }, null, [new PreCheckMatch("A", 55 / 60.0, 0.9, 0, 55)]);
        a = a with { Group = DramaGroups.Propose(a, WStart, WLen, S) };
        var d = PreCheck.Decide(a, 3000, _ => 3000, S);
        var rec = PreCheck.Record("x", d, a, new JsonObject(), null, (0, 300), 3000, S, "n", "v", false, 0, true);
        Assert.Equal("g1", (string?)rec["groupId"]); Assert.Equal(30, (double?)rec["groupOffset"]); Assert.True((double?)rec["matchShare"] >= 0.75); Assert.True((double?)rec["dupShare"] >= 0.85);
        var sum = PreCheck.Summary(d, a, false, S);
        Assert.Equal("g1", (string?)sum["group"]); Assert.Equal(30, (double?)sum["groupOffset"]); Assert.NotNull(sum["matchShare"]); Assert.NotNull(sum["dupShare"]);
        Assert.Equal("join", (string?)PreCheck.FromRecord(rec).Analysis.Group?.Kind);   // survives the service round trip
    }

    // ---- election
    static GroupMember M(string id, string? lang, double dur, string at, bool mixed = false) => new() { Upload = id, SpeechLanguage = lang, Dubbed = lang != null && lang != "zh", Duration = dur, ProcessedAt = at, Mixed = mixed };

    [Fact] public void Election_prefers_chinese_then_longest_then_first_processed()
    {
        var (id, why) = GroupSet.Elect([M("en1", "en", 4000, "2026-01-01"), M("zh-short", "zh", 2000, "2026-01-02"), M("zh-long", "zh", 3000, "2026-01-03")]);
        Assert.Equal("zh-long", id); Assert.Contains("Chinese audio", why); Assert.Contains("longest", why);
        var (id2, why2) = GroupSet.Elect([M("late", "zh", 3000, "2026-01-05"), M("early", null, 3000, "2026-01-02")]);   // null counts as zh; a tie goes to the first processed
        Assert.Equal("early", id2); Assert.Contains("first processed", why2);
        Assert.Null(GroupSet.Elect([M("m", "zh", 9000, "2026-01-01", mixed: true)]).Id);   // a mixed cut is never canonical
    }

    [Fact] public void Election_dub_only_group_has_no_canonical_until_a_chinese_copy_arrives()
    {
        var set = new GroupSet();
        set.Apply([new NewUpload("en1", "en", true, 3000, "2026-01-01", null)]);
        Assert.Null(set.Groups[0].Canonical); Assert.Contains("no Chinese-audio copy", set.Groups[0].CanonicalWhy);
        set.Apply([new NewUpload("fr1", "fr", true, 3100, "2026-01-02", new GroupProposal("join", "g-en1", "en1", 0, 0.9, 0.9, 40, 0.8, false, []))]);
        Assert.Single(set.Groups); Assert.Null(set.Groups[0].Canonical);
        set.Apply([new NewUpload("zh1", "zh", false, 2900, "2026-01-03", new GroupProposal("join", "g-en1", "fr1", 0, 0.9, 0.9, 40, 0.8, false, []))]);   // joined through the French copy
        var g = Assert.Single(set.Groups); Assert.Equal(3, g.Members.Count); Assert.Equal("zh1", g.Canonical); Assert.Contains("only Chinese copy", g.CanonicalWhy);
        Assert.Equal("fr1", g.Members.Single(m => m.Upload == "zh1").JoinedThrough);
    }

    [Fact] public void New_and_mixed_start_their_own_group_and_mixed_is_never_canonical()
    {
        var set = new GroupSet();
        set.Apply([new NewUpload("a", "zh", false, 3000, "2026-01-01", GroupProposal.None)]);
        set.Apply([new NewUpload("cut", "zh", false, 9000, "2026-01-02", new GroupProposal("mixed", "g-a", "a", 900, 0.8, 0.8, 20, 0.3, true, []))]);
        Assert.Equal(2, set.Groups.Count); var gc = set.Groups.Single(g => g.Id == "g-cut");
        Assert.True(gc.Members[0].Mixed); Assert.Null(gc.Canonical); Assert.Equal("a", set.Groups.Single(g => g.Id == "g-a").Canonical);
    }

    [Fact] public void Group_follow_goes_to_the_proposed_uploads_current_group()
    {
        var set = new GroupSet { Groups = [new() { Id = "g-new", Members = [M("x", "zh", 3000, "2026-01-01"), M("y", "en", 3000, "2026-01-02")] }, new() { Id = "g-old", Members = [M("z", "en", 100, "2026-01-01")] }] };
        // the pre-check proposed joining g-old through x, but x has since moved into g-new: z's group, g-old, is not where the new upload goes
        set.Apply([new NewUpload("n", "zh", false, 5000, "2026-02-01", new GroupProposal("join", "g-old", "x", 0, 0.9, 0.9, 30, 0.5, false, []))]);
        Assert.Contains(set.Groups.Single(g => g.Id == "g-new").Members, m => m.Upload == "n");
        Assert.DoesNotContain(set.Groups.Single(g => g.Id == "g-old").Members, m => m.Upload == "n");
        Assert.Equal("n", set.Groups.Single(g => g.Id == "g-new").Canonical);   // longest Chinese copy now
    }

    [Fact] public void Join_through_an_upload_placed_in_the_same_build_waits_for_it()
    {
        var set = new GroupSet();
        set.Apply([new NewUpload("b", "zh", false, 1, "2026-01-02", new GroupProposal("join", null, "a", 0, 0.9, 0.9, 9, 0.5, false, [])), new NewUpload("a", "zh", false, 1, "2026-01-01", GroupProposal.None)]);
        var g = Assert.Single(set.Groups); Assert.Equal(["a", "b"], g.Members.Select(m => m.Upload).OrderBy(x => x));
    }

    // ---- the dub rule across the group
    static PreCheckAnalysis Dup(GroupProposal? g, string? myLang, string topLang) => new(null, 1, 60, 0, [new PreCheckMatch("top", 0.95, 0.95, 0, 57, topLang, topLang != "zh")], 0, new Dictionary<string, double>()) { Group = g, Speech = new SpeechResult(myLang, myLang != null && myLang != "zh", 1, 30, 60, null) };

    [Fact] public void Dup_rule_needs_every_copy_in_the_group_to_be_a_dub()
    {
        var allDubs = new GroupProposal("join", "g", "top", 0, 0.95, 0.95, 57, 0.95, false, [new("top", "en", true), new("other", "fr", true)]);
        var oneZh = new GroupProposal("join", "g", "top", 0, 0.95, 0.95, 57, 0.95, false, [new("top", "en", true), new("zhcopy", "zh", false)]);
        Assert.Equal("canonical", PreCheck.Decide(Dup(allDubs, "zh", "en"), 3000, _ => 3000, S).Decision);
        // the best match is a dub, but the group already has a Chinese copy: a plain duplicate
        var d = PreCheck.Decide(Dup(oneZh, "zh", "en"), 3000, _ => 3000, S);
        Assert.Equal("duplicate", d.Decision);
        // no group (an older index): the single best match decides, as before
        Assert.Equal("canonical", PreCheck.Decide(Dup(null, "zh", "en"), 3000, _ => 3000, S).Decision);
    }

    // ---- the index job writes the groups
    [Fact] public async Task Index_build_applies_proposals_writes_groups_and_stamps_uploads()
    {
        var store = new FakeStore(); var P = new Paths("index/");
        async Task Rec(string id, GroupProposal? g, double dur) => await VideoJsonSeed.PutAsync(store, P, id, dur, precheck: new JsonObject { ["at"] = "2026-01-0" + id.Length + "T00:00:00Z", ["video"] = new JsonObject { ["duration"] = dur }, ["group"] = g?.ToJson() });   // 0.13.2: the record is video.json's precheck section
        await Rec("en", GroupProposal.None, 3000); await Rec("zh2", new GroupProposal("join", null, "en", 0, 0.9, 0.9, 40, 0.7, false, []), 2900);
        SearchData Idx(int v, params (string Id, string? Lang)[] u) => new() { Version = v, MeanVersion = 1, Mean = new float[512], Bits = [], RefUpload = [], RefCenter = [], Uploads = u.Select(x => new SearchUpload(x.Id, "s", "e", 1, 0, 1, 1, 1, 0, x.Lang, x.Lang != null && x.Lang != "zh")).ToList(), BuiltAt = DateTimeOffset.Parse("2026-02-01T00:00:00Z") };
        var v1 = Idx(1, ("en", "en")); var g1 = await GroupBuild.ApplyAsync(store, P, null, v1, default);
        Assert.Equal("g-en", v1.Uploads[0].Group);
        // zh2 has a record but no centers.zip yet: short-circuited, it joins from its pre-check record (checkedBy precheck); the full check replaces that once it is processed
        Assert.Equal("zh2", g1.Groups[0].Canonical); Assert.Equal("precheck", g1.Groups[0].Members.Single(m => m.Upload == "zh2").CheckedBy); Assert.Equal("index", g1.Groups[0].Members.Single(m => m.Upload == "en").CheckedBy);
        var v2 = Idx(2, ("en", "en"), ("zh2", "zh")); var g2 = await GroupBuild.ApplyAsync(store, P, v1, v2, default);
        Assert.All(v2.Uploads, u => Assert.Equal("g-en", u.Group)); Assert.Equal("zh2", g2.Groups[0].Canonical);
        var back = await GroupStore.LoadAsync(store, P, 2, default); Assert.Equal("zh2", back!.Groups[0].Canonical); Assert.Equal(2, back.Groups[0].Members.Count);
        Assert.NotNull(await store.GetAsync("index/groups/v1.json", default));   // the old version stays
        // the stamp survives uploads.yaml
        var (_, _, _, ups) = SearchData.ParseUploadsYaml(v2.UploadsYaml()); Assert.All(ups, u => Assert.Equal("g-en", u.Group));
    }

    // ---- the one-time build
    [Fact] public void Center_shares_union_into_groups()
    {
        float[] V(params float[] x) { var v = new float[512]; for (int i = 0; i < x.Length; i++) v[i] = x[i]; Vec.Normalize(v); return v; }
        var a = V(1).Concat(V(0, 1)).ToArray(); var b = V(1).Concat(V(0, 1)).Concat(V(0, 0, 1)).ToArray(); var c = V(0, 0, 0, 1);
        Assert.Equal(1.0, GroupsCommand.Share(a, b, 0.8f)); Assert.Equal(2 / 3.0, GroupsCommand.Share(b, a, 0.8f), 3); Assert.Equal(0, GroupsCommand.Share(a, c, 0.8f));
        var comps = GroupSet.Components(["a", "b", "c", "d", "e"], [("a", "b"), ("b", "c"), ("d", "e")]);
        Assert.Equal(["a", "b", "c"], comps[0]); Assert.Equal(["d", "e"], comps[1]);
        var ups = new[] { "a", "b", "c" }.Select(id => new SearchUpload(id, "", "", 1, 0, 1, 1, 1, 0, id == "a" ? "en" : "zh", id == "a")).ToList();
        var set = GroupsCommand.FromPairs(ups, new Dictionary<(string, string), (double, double)> { [("a", "b")] = (0.9, 0.7), [("b", "c")] = (0.4, 0.1) }, 0.75, id => (id == "b" ? 3000 : 2000, null));
        Assert.Equal(2, set.Groups.Count); var g = set.Groups.Single(x => x.Members.Count == 2); Assert.Equal("b", g.Canonical);
    }
}
