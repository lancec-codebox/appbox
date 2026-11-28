using Xunit;

namespace IndexNode.Tests;

/// <summary>0.13.89: a live node's --set stage.X=stop|drain narrows what it claims and never loosens control.</summary>
public class LocalStageSetTests
{
    [Fact]
    public void A_live_node_stops_its_side_jobs_and_cannot_run_what_control_stopped()
    {
        var ctl = new ControlFile { Mode = "run", Stages = new() { ["download"] = "stop", ["process"] = "run", ["group"] = "stop" } };
        var c = WhatIf.ApplyLocalStages(ctl, ["stage.check=stop", "stage.search-index=stop", "stage.group=drain", "stage.actors=drain"]);
        Assert.Equal(Level.Stop, c.Effective("check"));
        Assert.Equal(Level.Stop, c.Effective("search-index"));
        Assert.Equal(Level.Stop, c.Effective("group"));    // control's stop wins over the node's drain
        Assert.Equal(Level.Drain, c.Effective("actors"));
        Assert.Equal(Level.Run, c.Effective("process"));    // untouched
        Assert.Equal(Level.Stop, c.Effective("download"));
        Assert.Equal(Level.Run, ctl.Effective("check"));    // the store's copy is not changed
    }

    [Fact]
    public void Only_stop_or_drain_of_a_stage_is_a_live_set()
    {
        Assert.True(WhatIf.IsLocalStageSet("stage.check=stop"));
        Assert.True(WhatIf.IsLocalStageSet("stage.search-index=DRAIN"));
        Assert.False(WhatIf.IsLocalStageSet("stage.check=run"));
        Assert.False(WhatIf.IsLocalStageSet("versions.cast=2"));
        Assert.False(WhatIf.IsLocalStageSet("mode=stop"));
        Assert.Throws<ArgumentException>(() => WhatIf.ApplyLocalStages(new ControlFile(), ["versions.cast=2"]));
    }
}
