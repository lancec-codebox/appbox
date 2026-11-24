using System.Text.Json.Nodes;
using Xunit;

namespace IndexNode.Tests;

public class MediaImportTests
{
    static Dictionary<string, VideoJson.Doc> Docs(int n) => Enumerable.Range(0, n).ToDictionary(i => "v" + i.ToString("D4"), i => new VideoJson.Doc(null, new JsonObject
    {
        ["v"] = 1, ["id"] = "v" + i.ToString("D4"), ["youtube"] = new JsonObject { ["title"] = "t" + i, ["durationSeconds"] = 100 + i },
        ["ai"] = new JsonObject { ["score"] = 0.1, ["flag"] = i % 7 == 0 }, ["download"] = new JsonObject { ["x"] = 1 },
    }));

    [Fact]
    public async Task Local_then_apply_equals_direct_batches_are_100_and_a_rerun_changes_nothing()
    {
        var rows = MediaImport.BuildRows(Docs(250)); var rep = MediaImport.Plan(rows, 250);
        Assert.Equal(3, rep.Batches);
        var direct = new FakeMediaTable(); Assert.Equal(3, await MediaImport.ApplyAsync(direct, rows, default));
        var dir = Path.Combine(Path.GetTempPath(), "mimp-" + Guid.NewGuid().ToString("N"));
        try
        {
            MediaImport.WriteLocal(rows, rep, dir);
            var viaLocal = new FakeMediaTable(); await MediaImport.ApplyAsync(viaLocal, MediaImport.ReadLocal(dir), default);
            var a = await direct.QueryAsync(null, null, default); var b = await viaLocal.QueryAsync(null, null, default);
            Assert.Equal(250, a.Count);
            Assert.Equal(a.OrderBy(r => r.RowKey).Select(r => r.RowKey + r.Props.ToJsonString()), b.OrderBy(r => r.RowKey).Select(r => r.RowKey + r.Props.ToJsonString()));
            var r0 = await direct.GetAsync("upload", "v0007", null, default);
            Assert.Equal("true", r0!.Props["aiFlag"]!.ToString().ToLowerInvariant()); Assert.Equal("full", r0.Props["data"]!.ToString());
            await MediaImport.ApplyAsync(direct, rows, default);   // idempotent
            Assert.Equal(250, (await direct.QueryAsync(null, null, default)).Count);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }
}
