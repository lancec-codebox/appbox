using System.Text.Json.Nodes;

namespace IndexNode.Tests;

public class CoverTests
{
    private static JsonArray Cands(string json) => (JsonArray)JsonNode.Parse(json)!;

    [Fact]
    public void Lead_shot_is_describes_pick_when_unmasked()
    {
        var c = Cands("""[{"k":0,"t":5,"quality":9,"masked":false,"box":[1,2,3,4]},{"k":1,"t":9,"quality":1,"masked":false,"box":[10,20,30,40]}]""");
        var s = Publish.PickLeadShot(c, 1)!.Value;
        Assert.Equal(1, s.K); Assert.Equal(9, s.T); Assert.Equal([10d, 20, 30, 40], s.Box);
    }

    [Fact]
    public void Lead_shot_skips_a_masked_pick_for_the_best_unmasked_one()
    {
        var c = Cands("""[{"k":0,"t":5,"quality":3,"masked":false},{"k":1,"t":9,"quality":8,"masked":true},{"k":2,"t":12,"quality":6,"masked":false}]""");
        Assert.Equal(2, Publish.PickLeadShot(c, 1)!.Value.K);   // pick 1 is masked: best unmasked by quality
        Assert.Equal(1, Publish.PickLeadShot(Cands("""[{"k":0,"t":1,"quality":1,"masked":true},{"k":1,"t":2,"quality":2,"masked":true}]"""), 1)!.Value.K); // all masked: the pick
        Assert.Null(Publish.PickLeadShot(Cands("[]"), null));
        Assert.Null(Publish.PickLeadShot(null, 0));
    }

    [Fact]
    public async Task Cover_filter_gives_a_360x640_crop_centred_and_clamped()
    {
        var ff = Tools.OnPath("ffmpeg"); if (ff == null) return;
        var dir = Path.Combine(Path.GetTempPath(), "indexnode-cover-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(dir);
        try
        {
            foreach (var (w, h, cx, cy) in new (int, int, double?, double?)[] { (1280, 720, 900, 300), (1280, 720, null, null), (1280, 720, 5, 5), (720, 1280, 360, 400), (1000, 1000, 990, 990) })
            {
                var src = Path.Combine(dir, "s.jpg"); var o = Path.Combine(dir, $"o{w}x{h}.jpg");
                var r = await Proc.RunAsync(ff, ["-v", "error", "-y", "-f", "lavfi", "-i", $"testsrc=size={w}x{h}:rate=1", "-frames:v", "1", src], default); Assert.Equal(0, r.ExitCode);
                r = await Proc.RunAsync(ff, ["-v", "error", "-y", "-i", src, "-vf", Publish.CoverFilter(cx, cy), "-frames:v", "1", o], default); Assert.True(r.ExitCode == 0, r.Stderr);
                var p = await Proc.RunAsync(Tools.OnPath("ffprobe") ?? "ffprobe", ["-v", "error", "-show_entries", "stream=width,height", "-of", "csv=p=0:s=x", o], default);
                Assert.Equal("360x640", p.Stdout.Trim());
            }
        }
        finally { Directory.Delete(dir, true); }
    }
}
