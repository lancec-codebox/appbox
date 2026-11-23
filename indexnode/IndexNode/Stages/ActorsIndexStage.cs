namespace IndexNode;

/// <summary>0.13.23: the "actors-index" queue stage: builds the next actor index version from index/actors/units/ (carrying forward unchanged uploads by ETag; "up to date" writes nothing). The only writer of the index and of the actors in state.json.</summary>
public sealed class ActorsIndexStage(IStore store) : IStageHandler
{
    public ActorsIndex.BuildReport? Last { get; private set; }
    public async Task RunAsync(JobContext ctx)
    {
        ctx.Report("actors-index", "building"); var ct = ctx.Token; var io = new ActorsIO(ctx.Store, ctx.Paths);
        // 0.13.27: the AI check is the queue's hold (actors / process entry), not the aggregate; an upload the queue does not know falls back to its own video.json
        var q = ctx.Queue;
        var rep = await ActorsIndex.BuildAsync(io, ct, id => Task.FromResult(q != null && q.Read(st => st.Entry(id, "actors")?.Hold == "ai" || st.Entry(id, "process")?.Hold == "ai")), 2);   // 0.13.30: queue state only, no video.json read per upload
        Last = rep; if (!rep.UpToDate) ctx.PublishedVersion = rep.Version; Log.Info("actors-index: " + rep.Note);
    }
}
