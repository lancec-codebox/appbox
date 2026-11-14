namespace IndexNode.Redo;

public sealed record SscdRedoResult(int Jobs, int Int8, int Reset, int Marked, int Leased, int Fp32, int NoManifest);

public static class Sscd
{
    /// <summary>Resets the sscd jobs whose manifest was made by the int8 model: state pending, due now, attempts cleared, gpu-ready marker written. Nothing is deleted: the old sscd/ vectors are replaced by the step itself when it finishes.</summary>
    public static async Task<SscdRedoResult> RunAsync(IStore store, Paths paths, bool dry, CancellationToken ct)
    {
        int jobs = 0, int8 = 0, reset = 0, marked = 0, leased = 0, fp32 = 0, none = 0;
        foreach (var b in await store.ListAsync(paths.JobsDir("sscd"), true, false, ct))
        {
            if (b.Tags.GetValueOrDefault(Tags.Stage) != "sscd") continue;
            jobs++;
            var id = Path.GetFileNameWithoutExtension(b.Name);
            var man = await store.GetAsync(paths.Video(id, "sscd/manifest.json"), ct);
            if (man == null) { none++; continue; }
            if (!IndexNode.Sscd.IsInt8Manifest(man.Data)) { fp32++; continue; }
            int8++;
            if (b.LeaseState == "leased") { leased++; continue; }
            reset++;
            if (dry) continue;
            var lease = await store.AcquireLeaseAsync(b.Name, Protocol.LeaseSeconds, ct);
            if (lease == null) { leased++; reset--; continue; }
            try
            {
                var body = await store.GetAsync(b.Name, ct);
                var job = body == null ? JobDescriptor.NewStep("sscd", id) : JobDescriptor.Parse(body.Data);
                job.Attempts = 0; job.LastError = null; job.Permanent = null; job.FinishedAt = null; job.ElapsedSeconds = null;
                var added = b.Tags.GetValueOrDefault(Tags.Added) ?? job.AddedAt;
                await store.PutAsync(b.Name, job.ToBytes(), Tags.Job("sscd", "pending", added, Protocol.NowUtc()), lease, false, ct);
            }
            finally { await store.ReleaseLeaseAsync(b.Name, lease, CancellationToken.None); }
            // a marker from the first run keeps its old readyAt (first writer wins), which would let CPU nodes take the job at once: start from a fresh one
            await GpuRouting.DeleteMarkerAsync(store, paths, "sscd", id);
            await GpuRouting.WriteMarkerAsync(store, paths, "sscd", id, ct);
            marked++;
        }
        return new SscdRedoResult(jobs, int8, reset, marked, leased, fp32, none);
    }
}
