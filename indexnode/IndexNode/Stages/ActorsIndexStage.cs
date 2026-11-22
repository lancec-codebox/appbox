namespace IndexNode;

/// <summary>0.13.23: the "actors-index" queue stage: builds the next actor index version from index/actors/units/ (carrying forward unchanged uploads by ETag; "up to date" writes nothing). The only writer of the index and of the actors in state.json.</summary>
public sealed class ActorsIndexStage(IStore store) : IStageHandler
{
    public ActorsIndex.BuildReport? Last { get; private set; }
    public async Task RunAsync(JobContext ctx)
    {
        ctx.Report("actors-index", "building"); var ct = ctx.Token; var io = new ActorsIO(ctx.Store, ctx.Paths);
        Dictionary<string, VideoJson.Doc?>? aiDocs = null;
        var rep = await ActorsIndex.BuildAsync(io, ct, async id => { aiDocs ??= await VideoJson.ReadManyAsync(store, ctx.Paths, new List<string>(), ct, all: true); return ActorsStage.AiFlag((aiDocs.TryGetValue(id, out var d) ? d : await VideoJson.ReadAsync(store, ctx.Paths, id, ct))?.Root); }, 2);
        Last = rep; Log.Info("actors-index: " + rep.Note);
    }
}
