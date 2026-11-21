using System.Text.Json.Nodes;
using Xunit;

namespace IndexNode.Tests;

/// <summary>0.13.15: each member's relation to the group's canonical, derived from fake stretches. Fixture: canonical "c" (6000 s) in group "g"; members m-copy, m-part, m-cut, m-longer, m-mixed, m-chain.</summary>
public class GroupRelationTests
{
    static readonly PreCheckSettings S = new();
    static RelStretch R(double from, double to, string src, double off) => new(from, to, src, off);

    static GroupSet Fixture()
    {
        GroupMember M(string id, double dur, double covered, double? off, bool mixed = false, params RelStretch[] st) => new() { Upload = id, Duration = dur, CoveredShare = covered, Offset = off, Mixed = mixed, Stretches = st };
        return new GroupSet
        {
            Groups = [new DramaGroup { Id = "g", Canonical = "c", Members =
            [
                new GroupMember { Upload = "c", Duration = 6000 },
                M("m-copy", 6010, 0.97, 0, false, R(0, 6000, "c", 0)),                                        // the whole canonical clock, same length
                M("m-part", 2880, 0.97, 1500, false, R(0, 2880, "c", 1500)),                                 // 48 min = canonical 1500-4380, one contiguous span
                M("m-cut", 1800, 0.92, 0, false, R(0, 600, "c", 0), R(600, 1200, "c", 2000), R(1200, 1800, "c", 4000)),   // three scattered spans (0-600, 2600-3200, 5200-5800)
                M("m-longer", 9000, 0.55, 0, false, R(0, 6000, "c", 0)),                                     // spans the whole canonical but only 55% of itself is covered (extra material)
                M("m-mixed", 900, 0.30, 100, true, R(0, 270, "c", 100)),
                M("m-chain", 3000, 0.95, 40, false, R(0, 3000, "m-part", 0)),                                  // matched against m-part (offset 1500 to the canonical) at +40: canonical 1540-4540
            ] }],
        };
    }

    [Fact]
    public void Every_kind_is_derived_from_the_stretches()
    {
        var set = Fixture(); set.Relate(S);
        GroupMember Get(string id) => set.Groups[0].Members.First(m => m.Upload == id);
        Assert.Equal("copy", Get("m-copy").Kind); Assert.Equal(1.0, Get("m-copy").CanonicalCovered); Assert.Equal(0.97, Get("m-copy").MemberCovered);
        Assert.Equal("part", Get("m-part").Kind); Assert.Equal(0.48, Get("m-part").CanonicalCovered);                 // 2880 / 6000, one span 1500-4380
        Assert.Equal("cut", Get("m-cut").Kind); Assert.Equal(0.3, Get("m-cut").CanonicalCovered);                     // 1800 / 6000 in three scattered spans
        Assert.Equal("longer", Get("m-longer").Kind); Assert.Equal(1.0, Get("m-longer").CanonicalCovered); Assert.Equal(0.55, Get("m-longer").MemberCovered);
        Assert.Equal("mixed", Get("m-mixed").Kind); Assert.Null(Get("m-mixed").CanonicalCovered);
        Assert.Equal("part", Get("m-chain").Kind); Assert.Equal(0.5, Get("m-chain").CanonicalCovered);                // through m-part: 40 + 1500 -> 1540..4540 = 3000 s
        Assert.Equal("copy", Get("c").Kind);
    }

    [Fact]
    public void A_copy_needs_a_duration_inside_the_duplicate_window_and_both_numbers_at_080()
    {
        var (cc, kind, spans) = GroupRelation.Derive(false, 0.95, 6000, 6000, [(0, 6000)], S); Assert.Equal(("copy", 1.0, 1), (kind, cc, spans));
        // both covered but 40 minutes longer than the canonical: not a copy (and not part/cut/longer either): mixed
        Assert.Equal("mixed", GroupRelation.Derive(false, 0.95, 8400, 6000, [(0, 6000)], S).Kind);
        // a 15% shorter copy (5100 s) is inside the window; 5000 s is not
        Assert.Equal("copy", GroupRelation.Derive(false, 0.9, 5100, 6000, [(0, 5100), (0, 900)], S).Kind);
        Assert.Equal("part", GroupRelation.Derive(false, 0.9, 4000, 6000, [(0, 4000)], S).Kind);
        // the threshold itself: 0.8 of the canonical is enough, 0.79 is not
        Assert.Equal("copy", GroupRelation.Derive(false, 0.8, 6000, 6000, [(0, 4800)], S).Kind);
        Assert.Equal("part", GroupRelation.Derive(false, 0.8, 6000, 6000, [(0, 4794)], S).Kind);
        Assert.Equal("longer", GroupRelation.Derive(false, 0.79, 7000, 6000, [(0, 4800)], S).Kind);
        // runs closer than the offset tolerance (30 s) are one span
        Assert.Equal(1, GroupRelation.Derive(false, 0.9, 1000, 6000, [(0, 400), (420, 800)], S).Spans); Assert.Equal(2, GroupRelation.Derive(false, 0.9, 1000, 6000, [(0, 400), (500, 900)], S).Spans);
    }

    [Fact]
    public void The_fields_are_written_to_the_group_file_and_the_catalog_rows()
    {
        var set = Fixture(); set.Relate(S);
        var back = GroupSet.FromJson(JsonNode.Parse(set.ToJson().ToJsonString())!.AsObject());
        var part = back.Groups[0].Members.First(m => m.Upload == "m-part");
        Assert.Equal(("part", 0.48, 0.97, 2880.0, 1500.0), (part.Kind, part.CanonicalCovered, part.MemberCovered, part.Duration, part.Offset));
        var cat = new Catalog { Uploads = set.Groups[0].Members.Select(m => new CatalogUpload(m.Upload, m.Duration, null, null, null, null, null, null, false, false, null, "processed")).ToList() };
        cat.ApplyGroups(back);
        var row = Catalog.FromGzip(cat.ToGzip()).Find("m-cut")!;
        Assert.Equal(("cut", 0.3, 0.92, 1800.0), (row.RelationKind, row.CanonicalCovered, row.MemberCovered, row.GroupDuration));
        Assert.Equal("g", row.GroupId); Assert.Equal("copy", Catalog.FromGzip(cat.ToGzip()).Find("c")!.RelationKind);
    }
}
