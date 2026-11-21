# Cutover to the queue log (0.13.0)

0.13.0 keeps its job queue in one append log (`index/jobs/queue.log`, `index/jobs/queue.snapshot.json.gz`, `index/jobs/active/*`; every path here is under the store prefix `index/`). The snapshot is gzip-compressed and slimmed (finished subjects keep ids, states, priority, a short reason); a plain `queue.snapshot.json` from the first migration is still read and replaced by the `.gz` at the first rollover. It never reads or writes the old job blobs (`index/jobs/<stage>/<id>.json`).
Its control file is `index/jobs/control.json` (mode, stages, downloads, gpu, message). `index/control.json` is ignored; leave it at mode=stop for ever so an old binary never resumes on the old store.

## Steps
1. **Stop the old nodes**, and `indexnode control stop` them on the old binary (that writes `index/control.json` mode=stop; keep it).
2. **Ghostignore**: `.ghostignore.json` must list `index/jobs/` (and `index/holds/`, `index/gpu-ready/`) on every machine that syncs the store, so the queue log, the snapshot and the old job blobs are never synced or re-created there.
3. **Dry run**: `indexnode jobs migrate --to log --dry-run` (or `--verify`). It prints the blob-side counts per stage/state, the queue entries it would write per stage/state, and what is not carried over (holds, gpu-ready markers, comments jobs). Compare the two. It refuses (exit 1) when any job or hold is leased: stop the nodes or wait.
4. **Real run**: `indexnode jobs migrate --to log`. Writes the first segment (`index/jobs/queue.log/…`), the snapshot (`index/jobs/queue.snapshot.json.gz`) and `index/sources/<id>/seen.json`; the old blobs stay. `--force` builds the snapshot again.
5. **First nodes**: start one node on 0.13.0 (`indexnode run`); it exits non-zero with one line pointing at the migration if the snapshot is missing. Set the mode with `indexnode control run` (`index/jobs/control.json` is new: absent means run). Then start the rest.

## What carries over
Pending, failed (permanent, attempts, last error) and done state; unclassified downloads stay unclassified; refresh sources keep their schedules (channel `every 1d` becomes dynamic, `off` stays off); priorities and due times; the finished steps of part-processed dramas.
Dramas with processing already done resume from their first unfinished step and **skip the pre-check**; only dramas with no processing done get it (first step of their process claim).
Not carried: holds, GPU markers, comments jobs (counted in the report).

## Pre-check skip and trash
A skip (duplicate, AI, non-canonical dub) changes nothing: the `done` line carries the decision, the reason and `trash: download.zip/video`, and processing stops. `indexnode trash list` shows the marks; `indexnode trash empty --older-than 30d [--dry-run]` removes the video blobs by hand (download.zip stays) and appends `deleted`. No node runs it.

## Rollback limits
Before step 4 nothing has changed. After it the old blobs are untouched but stale: going back means old binaries on a store whose queue moved on (work done by 0.13.0 is unknown to them), and `index/control.json` is mode=stop, so you must set it to run yourself. Treat the migration as one way.

## 0.13.2: video.json and members.json

Before a 0.13.2 search-index job runs (it reads video.json for every upload, and fails loudly where there is none):

1. `indexnode videos backfill --dry-run` from a westus2 node: counts the uploads that have no video.json, the ones under an active claim (skipped: their claim writes it) and any with no duration anywhere (FAILED; fix or retire them).
2. `indexnode videos backfill`: creates the missing ones (create-only; run it again for the claimed ones once their claims end).
3. `indexnode sources migrate-seen --dry-run`, then without `--dry-run`: seen.json ids become members of `index/sources/<id>/members.json` and seen.json is deleted (a refresh also does it for its own source on first touch).
4. **0.13.1 live repair**: `indexnode jobs repair-steps --dry-run`, then `indexnode jobs repair-steps` (corrects the wrongly done steps, re-opens the finished dramas without centers.zip), then `indexnode sources set-tabs videos --all --dry-run` and without `--dry-run`.

## 0.13.12: rollout on a node that ran 0.13.11's `seq assign --renumber`

Do this once per sandbox, with the node stopped, so nothing claims a numbered-but-unstarted drama meanwhile:

1. Stop the node (`indexnode` service / process); wait for its lanes to finish or release their claims.
2. Install 0.13.12.
3. `indexnode seq clear-unstarted --dry-run` and check the count (the dramas that are downloaded but not started: process pending, no steps done, no frames file, no `precheck.at`; the first few ids are printed). Then `indexnode seq clear-unstarted`. It is safe after group steps have finished: the order of the remaining dramas is unchanged (gaps in the numbers are fine) and a cleared drama had no frames, so no finished group record changes.
4. Start the node. Group steps now wait in memory (no claim, no log line) behind the lowest numbered drama that has no frames; one summary line every 5 minutes says how many wait. A cleared drama is numbered when its pre-check starts.

Do not run `seq assign --renumber` again once group steps have finished (it refuses); `seq assign` itself only numbers dramas that are started.

