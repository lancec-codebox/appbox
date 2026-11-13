# Drama index: work protocol (v1, blob storage)

Nodes (Medusa on Android, and the desktop node) share work through one **Azure Blob Storage container**, reached by a **container SAS URL** that is entered into each node. No account name or key is ever built into an app. The SAS URL looks like:

`https://<account>.blob.core.windows.net/<container>?sv=...&sig=...`

Today that is the owner's Nexis drive container **media-wus2**: hexad variable `media_wus2_sas_url`, Key Vault backed. Everything lives under the prefix **`index/`**. Use `x-ms-version: 2021-12-02` or newer.

## Nexis ghosting applies

A Nexis dehydrate timer runs on this container every 30 minutes with `--min-dehydrate-size 2000000 --expiry 20m`. It turns any blob of 2 MB or more that hasn't been modified or touched for 20 minutes into a "ghost": the data is kept as uncommitted blocks, which is free.

The owner wants that for the index videos. So:

- **Small blobs (under 2 MB) are never ghosted:** job files, markers, `frames.json` and `info.json`. Read and write them normally.
- **Big blobs (`video.mp4`, `compact.mp4`, `sscd.f16`) may be ghosted.** Nexis keeps its state in blob index tags: `state` (`active`, `transitioning` or `ghost`), `block_prefix`, `size`, `snapshot`, `last_access`, `last_refresh_time`. Before reading a big blob, a node must:
  1. **Touch it:** set the Nexis tag `last_access` to the current UTC time (Nexis `Timestamp` format), keeping the other tags. Do this again every 5 minutes while the blob is in use. Nexis skips a blob whose `last_access` is newer than its expiry.
  2. **Hydrate it if `state` is `ghost`:** commit its uncommitted blocks back. Reuse Nexis's own logic: `Q:\src\codebox\Nexis.Azure.Utilities` (`DehydrateOperation` with `requireHydrated`, `DriveOperationBase`). Don't reimplement it from memory.
- **Never write Nexis's tags** except `last_access`.
- **Index tags all start with `idx_`,** to stay clear of Nexis's tags. Azure allows at most 10 tags per blob.

## Jobs

One blob per job: `index/jobs/<stage>/<jobId>.json`. The stages are `download`, `process` and `comments`. The job's state is in its index tags:

- `idx_stage`: `download` or `process`
- `idx_state`: `pending`, `done` or `failed`
- `idx_added`: the time it was added, in sortable UTC
- `idx_due`: the earliest time it may run, in sortable UTC; for most jobs, the same as `idx_added`

`jobId` is the YouTube video id (11 characters), or a hash of the URL otherwise. `nodeId` is the node's name plus 6 random hex characters, fixed per install.

**Find work:** Find Blobs by Tags on the container (`?restype=container&comp=blobs&where=`), with ``"idx_stage"='download' AND "idx_state"='pending' AND "idx_due" <= '<now>'``. This needs the SAS permission `f`. If the SAS lacks it, fall back to listing `index/jobs/<stage>/` with `include=tags`. Take the oldest `idx_added` first.

**Claim:**
1. Acquire a **blob lease** of 60 s (`x-ms-lease-action: acquire`, `x-ms-lease-duration: 60`). A 409 means another node has the job; move on to the next.
2. Renew it every 30 s while working (`x-ms-lease-action: renew`).
3. If the node dies, the lease simply expires and the job is free again. No reaper is needed.
4. While working, keep a progress note in the blob's metadata, written with the lease id: `node`, `progress`, `heartbeat`.

**Finish:** write the outputs. Then rewrite the job blob with its lease id, adding `finishedAt`, `nodeId` and `elapsedSeconds`, and set `idx_state=done`. Then release the lease.

**Fail:** a node lets go of a job **immediately** when it finds it can't do it.
- **Permanent failures:** the video is removed, private, members-only, age-gated, region-blocked, or the URL isn't supported. Rewrite the job with `lastError` and `permanent: true`, set `idx_state=failed`, and release the lease. Don't retry.
- **Transient failures:** a network or 5xx error, a full disk, an upload error. Increment `attempts`, record `lastError` and release the lease at once. When `attempts` reaches 3, set `idx_state=failed`.
- **Bot blocks and rate limits** ("Sign in to confirm you're not a bot", HTTP 429): release at once, don't count the attempt, and back off as described under Nodes.

Rewriting a blob (Put Blob) drops its tags unless they're sent again in `x-ms-tags`. Always send the full `idx_*` set with each rewrite.

## Job descriptors

`download`:
```json
{ "v": 1, "stage": "download", "jobId": "dQw4w9WgXcQ", "url": "https://www.youtube.com/watch?v=dQw4w9WgXcQ",
  "maxHeight": 480, "format": null, "subs": true, "addedAt": "2026-11-13T19:00:00Z", "attempts": 0 }
```
- `maxHeight` defaults to 480. `format`, when set, is a raw yt-dlp format string that takes priority over `maxHeight`.
- The default selector is `bv*[height<=H][ext=mp4]+ba[ext=m4a]/b[height<=H]/b`, merged to mp4.

`process`:
```json
{ "v": 1, "stage": "process", "jobId": "dQw4w9WgXcQ", "steps": ["frames", "sscd", "transcribe", "compress"],
  "compress": { "codec": "av1", "crf": 38, "preset": 8, "audioKbps": 48, "keepOriginal": false },
  "addedAt": "...", "attempts": 0 }
```
- `steps` run in that order. A step is skipped when its output already exists.
- `frames`, `sscd` and `transcribe` work on `video.mp4`, before `compress`.
- **`transcribe`:**
  - Extract the audio: 16 kHz mono via ffmpeg, kept only as a local temp file.
  - Run our own speech recognition, always, even when subtitles exist, because shipped subtitles are often wrong.
  - Write `asr/<model>.zh.vtt` and `asr/<model>.zh.json`, the JSON holding segments with word or character timings and confidences.
  - The model is chosen per job: `"transcribe": { "model": "<id>" }`, with the default set after an ASR comparison. A node without the model, or too slow for it (for example Medusa), never claims a job that needs it.
- `compress` re-encodes to AV1 with SVT-AV1 (`libsvtav1`) at the given CRF and preset, plus Opus audio, into mp4.

`comments`:
```json
{ "v": 1, "stage": "comments", "jobId": "dQw4w9WgXcQ", "url": "https://www.youtube.com/watch?v=dQw4w9WgXcQ",
  "maxComments": 1000, "sort": "top", "round": 0, "addedAt": "...", "attempts": 0 }
```
- yt-dlp `--write-comments --skip-download`, with `--extractor-args "youtube:max_comments=<N>,all,all,all;comment_sort=<sort>"`, no cookies. Each round takes the top comments, then a second pass sorted by `new`.
- Output: `index/videos/<jobId>/comments/<UTC yyyyMMddTHHmmssZ>.json`, a snapshot holding `{ "v": 1, "count", "commentCount" (YouTube's total), "comments": [{ "id", "parent", "author", "authorId", "text", "likes", "timestamp", "pinned", "byUploader" }] }`. Snapshots are never overwritten, so changes over time are kept.
- Refresh: after a round finishes, the job is rewritten with `round + 1`, `idx_state=pending` and `idx_due` set to the next refresh. Refreshes fall at 1 day, 3 days, 7 days and 30 days after the download, then every 30 days while the video is under a year old. After that the job ends as `done`.
- Comments mention cast names, other parts and sequels, and sentiment; later steps mine them for that.

## Outputs: `index/videos/<jobId>/`

**Download stage:**
- Files:
  - `video.mp4`: a block blob, uploaded with Put Block and Put Block List in 8 MiB blocks.
  - `info.json`: yt-dlp's `--write-info-json`, without the comments.
  - `description.txt`: the video description, in plain text.
  - `thumb.jpg`.
  - Subtitles:
    - `subs/<lang>.vtt` for uploaded subtitles in English (`en*`) and Chinese (`zh`, `zh-Hans`, `zh-Hant`, `zh-CN`, `zh-TW`, `zh-HK`).
    - `subs/<lang>.auto.vtt` for YouTube's automatic captions in the same languages, when present.
    - Missing subtitles are not an error.
- Then, written last, `downloaded.json`: `{ "v": 1, "bytes", "height", "durationSeconds", "subs": ["en", "zh-Hans.auto", ...], "nodeId", "at" }`.
- Then the node creates, for the same `jobId` and only where they don't exist yet:
  - the `process` job, with the default steps;
  - the first `comments` job, due at once.

**Process stage:**
- `frames.json`: `[{ "t": seconds, "kind": "scene"|"tick" }]`, with scene changes at `scene>0.3` plus a tick every 1 s. Extract in parallel segments, one ffmpeg process per physical core. A single ffmpeg uses only about 4 cores; segmenting made it 12.5× faster on a 16-core machine.
- `sscd.f16`: N×512 float16, L2-normalised SSCD (`sscd_disc_mixup`) descriptors, where row i matches frame i.
- `compact.mp4` (from `compress`): its duration must be within 1 s of the original, and it must decode end to end. When `keepOriginal` is false, delete `video.mp4` after `processed.json` is written.
- `asr/<model>.zh.vtt` and `asr/<model>.zh.json`, from `transcribe`.
- Then, written last, `processed.json`: `{ "v": 1, "steps", "frames", "model", "asrModel", "originalBytes", "compactBytes", "nodeId", "seconds", "at" }`.

A stage's marker (`downloaded.json`, `processed.json`) is the only signal that its outputs are complete.

## Nodes

- **Medusa (Android):** runs `download` and `comments`.
- **Desktop node:** runs any of `download`, `process` and `comments`, chosen per run. It also carries the tools: `enqueue` (URLs or a playlist, with `--max-height` and `--format`), `status` (counts per stage and state), and `requeue` (failed back to pending).
- **Rate limits:** wait 20–40 s, random, between YouTube downloads. After a bot block or a 429, back off for an hour.
- **The SAS URL is a secret.** Store it in the app's private storage, Android Keystore-backed on the phone, or a user-only config file or environment variable on the desktop. Never log it; redact `sig=` in all output.

## Control and node registry

**Global control:** `index/control.json` is one small blob that every node obeys, downloaders and processors alike.

```json
{ "v": 1, "mode": "run", "stages": { "download": "run", "process": "run" },
  "message": "optional note", "changedAt": "2026-11-13T20:00:00Z", "changedBy": "owner" }
```

The `mode` field applies to all stages; `stages` overrides it per stage. Each takes one of three values:
- `run`: work normally.
- `drain`: finish the jobs already claimed, but claim nothing new.
- `stop`: stop at once. Kill the running yt-dlp and ffmpeg, release every lease **without counting an attempt**, clean up local temp files, and stay idle.

The effective setting for a stage is the stricter of `mode` and `stages.<stage>`. A missing `control.json` means `run`.

Nodes re-read it at least every 30 s, at each lease renewal, with a conditional GET (`If-None-Match` on the ETag). They also re-read it before claiming any job. A change takes effect within 30 s. While stopped or drained, a node keeps polling and resumes on its own when the file says `run` again.

Write it with `indexnode control run|drain|stop [--stage download|process] [--message ...]`.

**Node registry:** every node keeps `index/nodes/<nodeId>.json` up to date. It writes the file at start, every 60 s, and on any change:

```json
{ "v": 1, "nodeId": "lancecm1-a1b2c3", "name": "lancecm1", "kind": "desktop" | "medusa",
  "version": "0.1.0", "platform": "windows-x64", "stages": ["download", "process"],
  "jobs": { "download": 1, "process": 2 }, "threads": 16, "conditions": "wifi+charging",
  "state": "working" | "idle" | "draining" | "stopped" | "backoff" | "offline",
  "control": "run", "running": [ { "jobId": "...", "stage": "process", "step": "sscd", "progress": "42%" } ],
  "lastSeen": "2026-11-13T20:00:30Z", "startedAt": "..." }
```

- A node writes `state: "offline"` when it shuts down cleanly. Any node whose `lastSeen` is more than 5 minutes old counts as offline.
- `indexnode status` lists every node: its kind, the stages it handles, its parallelism, state, running jobs, and last seen. Then come the queue counts and the control state.
- The tag `idx_kind=node` on these blobs lets them be found by tag.

## Desktop node notes (not part of the protocol)

- The Nexis tags on the wire carry Nexis's own prefix: `ghostd_state`, `ghostd_block_prefix`, `ghostd_size`, `ghostd_snapshot`, `ghostd_last_access`, `ghostd_refresh_time`. The protocol's short names above map to these. The node writes only `ghostd_last_access`.
- `--jobs download=1,process=2` (config `jobs`) sets how many jobs run at once per stage; each job has its own lease and renewal loop. `--threads N` (config `threads`, default the physical core count) is the CPU budget divided across the running process jobs: ffmpeg segments for `frames`, ONNX intra-op threads for `sscd`, `lp=` for SVT-AV1. The 20–40 s download gap holds across all download workers.
- `indexnode control run` or `drain` or `stop` without `--stage` sets `mode` and resets the per-stage overrides; with `--stage` it sets only that stage.
- The registry extra field `worker` on each running job is the worker number.
