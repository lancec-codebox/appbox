# IndexNode

The desktop node of the drama index, the counterpart of [Medusa](../medusa/) on Android. Both share work through one Azure Blob Storage container, reached by a container SAS URL. IndexNode downloads videos with yt-dlp (also from a Brave profile's Watch Later list), extracts frames, computes SSCD descriptors, compresses to AV1 and collects comments, and writes everything back to the container.

Protocol (blob layout, jobs, tags, Nexis ghosting): [PROTOCOL.md](IndexNode/PROTOCOL.md).

## Commands

```
indexnode run [--once] [--max-jobs N] [--workers N] [--threads N] [--worker-threads N]
              [--sources watch-later --source-every 6h] [--max-height 480]
indexnode enqueue <url|playlist-url>... [--max-height 480] [--format F]
indexnode enqueue --from watch-later [--max-height 480]
indexnode browser login | status
indexnode tools install --all        # yt-dlp, ffmpeg and every model, each sha256-pinned (about 1.2 GB)
indexnode tools install --ytdlp-zipapp
indexnode status
indexnode requeue [--stage S]
indexnode control run|drain|stop [--stage download|process|comments|watch] [--message M]
indexnode config set-sas <url>
indexnode config set <jobs|threads|model-url|model-path|node-name|ytdlp.command|browser.exe|browser.profile-dir|browser.port> <value>
```

All commands take `--prefix` (default `index/`). `INDEXNODE_SAS_URL` overrides the stored SAS.

## Centers and screenshot search (0.11.0)

`centers` is a step after `sscd`: per upload, spherical k-means (k = min(256, frames), k-means++, 10 iterations, seed from the upload id) over its stored SSCD vectors. Output `centers.zip` (stored, so a member block can be range-read) with `centers/centers.bin` and `centers/manifest.json`.

```
indexnode centers backfill [--ids <file>] [--since <time|6h|2d>] [--threads N] [--dry] [--limit N]
indexnode search-index build [--new-mean] | update
indexnode search --vector <f32|f16 file of 512 values> | --image <file> [--top-centers 300] [--top 20]
indexnode process --job <id> --steps centers
```

`centers.bin` (little-endian, offsets from the start of the file): header of 2144 bytes: 0 magic `IXCT`; 4 u32 version (1); 8 u32 k; 12 u32 dim (512); 16 u32 frames; 20 u32 build (1); 32 SHA-256 of the sscd f16 rows (32 bytes); 64 the upload's vector sum (512 f32); 2112 u64 centers offset (2144); 2120 u64 table offset; 2128 u64 members offset; 2136 u64 total length. Then k center vectors (f16, unit length), the center table (k x {u64 block offset, u32 block length, u32 member count}), then one member block per center, back to back: count x 512 f16 vectors, count x f32 times, count x i32 original row numbers (ascending). Bit codes are not stored per upload.

Global search table: `index/search/head.yaml` (current version) and `index/search/vN/`: `mean.f32`, `bits.bin` (64-byte sign codes of center - mean, one per center), `refs.bin` (u32 upload index, u32 center index per row), `uploads.yaml`. `update` writes a new version with the same mean; `build --new-mean` recomputes it. A query reads only the table, then range-requests the member blocks of the best centers.

**Local-only mode.** Input and output are separate stores, and the folder has the same `index/...` layout as the blob store (`index/videos/<id>/sscd.zip`, `index/videos/<id>/centers.zip`, `index/search/...`); no SAS and no network are needed once the data is local. `centers backfill`, `search-index build|update` and `search` all take:

```
--input-local <dir>    read the uploads' sscd.zip from a folder instead of the blob store
--output-local <dir>   write centers.zip and the search index to a folder instead of the blob store
--local <dir>          both at once
```

With neither, both are the blob store (SAS). Typical use: read sscd.zip from the remote store, write everything to a folder, search it locally (the member blocks are read from the folder):

```
indexnode centers backfill --output-local D:\idx
indexnode search-index build --output-local D:\idx
indexnode search --image shot.png --output-local D:\idx        # needs no SAS
```

With `--output-local` nothing is ever written to the store: a write that would reach it throws. The folder keeps each blob's metadata (`idx_sha256`, `idx_inputs`) in `<dir>/.indexnode-meta/`, so a second `backfill` skips what is built; `.indexnode-zips/` is the stage-zip cache.

TODO: featureless frames are not filtered out yet.

## Download pre-check and AI flag (0.12.0)

A download node with a search index and an SSCD model first downloads one 5-minute section (centred at 50%, <= 360p), embeds its frames and looks them up in the centers search index. A near-identical copy (share >= 0.80, similar duration) is not downloaded: the job ends in state `duplicate`. Every checked upload gets `index/videos/<id>/precheck.json` and an `ai` flag (SSCD frames probe v3); the flag never stops anything. No index or model: skipped, download as before. See PROTOCOL.md, "Download pre-check".

```
indexnode config set precheck.enabled false          # turn it off (or: indexnode run --no-precheck)
indexnode config set precheck.tau 0.70               # frame match cosine
indexnode config set precheck.skip-share 0.80        # share that skips the download
indexnode config set precheck.partial-share 0.60     # share that records another cut
indexnode config set precheck.duration-percent 0.15  # duration rule: max(15%, 600 s)
indexnode config set precheck.duration-min 600
indexnode config set precheck.top-centers 100
indexnode sample <id> --output-local <dir> --timebox 120   # 0.13.16: the sample download only, writes no store or queue (lancecl1); wrap it: timeout -k 10 130 indexnode sample ...
indexnode check <id> --input-local <dir> --timebox 120    # 0.13.16: the check on those files, write-free (the host); wrap it: pwsh -NoProfile -File Q:\src\workspace\drama-index\scripts\timebox.ps1 130 indexnode.exe check ...
indexnode jobs sample-first --dry-run                     # 0.13.16: pending downloads -> sample, pending process -> check; then: indexnode control run --stage process
indexnode config set sample-budget 200                    # 0.13.16: the sample stage's own budget (default unlimited)
indexnode config set precheck.window-seconds 300      # 0.13.15: two segments of this length
indexnode config set precheck.window-positions 0.30,0.70   # 0.13.15: centres of the two segments (30 frames each at precheck.frames 60)
indexnode config set precheck.order-tolerance-seconds 120  # 0.13.15: a member passes when gap_M >= gap_N - this
indexnode config set describe.main-min-screen 0.10    # 0.13.15: main cast needs this share of the lesser lead's face time, or a mention in title/teaser/synopsis
indexnode config set describe.main-max 6              # 0.13.15: at most this many main cast (most face time kept)
indexnode groups step <id> --dry-run                  # 0.13.15: the group step of one drama as the node runs it, writing nothing; prints the record and the time split
indexnode config set precheck.language-seconds 60     # seconds of audio tagged Speech with a language (~15 s chunks from the middle outwards, no VAD; music, events and nospeech dropped); 0 or all = every chunk. A non-zh call needs >= 3 chunks and 2/3 agreeing, else unclear (null = zh); under 10 s = no-speech
indexnode config set precheck.language-min-seconds 10
indexnode config set precheck.frames 60             # max SSCD frames per sample, evenly subsampled (a number, or 0/all); the AI thresholds are calibrated at 60
indexnode config set precheck.video-height 360       # sample video: best stream up to this height
indexnode config set precheck.audio-format wa        # sample audio for the language ID: lowest-bitrate audio-only (wa; a format id such as 249 also works)
indexnode config set precheck.window-position 0.5    # centre of the window, fraction of the duration
indexnode config set precheck.ai-flag 0.479          # ai=true at or above this score (0.529 is also recorded); calibrated at precheck.frames 60: with another frame count the record says thresholdsApply=false and a warning is logged
indexnode config set precheck.index-recheck-minutes 10
indexnode precheck <id|url>... [--ids <file>] [--output-local <dir>] [--window-position 0.5] [--keep-sample] [--exclude-self] [--threads N]   # ONLY the pre-check, read-only on the store -> <dir>/<id>/precheck.json (default ./precheck-out); one line per id with the timings
indexnode ai score [--ids <file>] [--all-frames] [--redo] [--local <dir>]   # score processed uploads from their sscd.zip -> videos/<id>/ai.json
indexnode --version
```

`indexnode status` shows `duplicate=N` in the download queue line.

## Download (0.5.1)

Each release has, per platform: `indexnode-<v>-<rid>[.exe]` (self-contained single file: no .NET install, ONNX Runtime and sherpa-onnx inside; win-x64 45.7 MiB, linux-x64 49.4 MiB) and `indexnode-<v>-<rid>-fdd[.exe]` (framework-dependent, needs the .NET 10 runtime; 25.9 MiB and 35.2 MiB). Each has a `.sha256`. On a new machine: download, run `indexnode tools install --all`, then `indexnode config set-sas <url>`.

Traffic: each node counts the bytes it moves (youtubeIn, storageRead, storageWrite, aiOut, aiIn, otherIn) and reports them in its registry record; `indexnode status --json` shows totals, last hour and last 24 h.

## Node name (stable identity)

`indexnode run --node-name <name>` (or `INDEXNODE_NODE_NAME`, or `config set node-name`; precedence in that order). A name given by flag or environment *is* the node id, with no machine-name or random suffix: every execution of a container job, which gets a new hostname each time, reports to one registry record, and its traffic and history keep accumulating. The record is never deleted. The GPU job uses `--node-name gpu-a100-westus3`. If another process reported under the same name less than 2 minutes ago, the node logs a warning and still runs (leases keep jobs safe); the record's `instances` lists the live processes.

## Setup on Windows


1. Install the .NET 10 runtime. Download `indexnode-<version>-win-x64.exe` from the [releases](https://github.com/lancec-codebox/appbox/releases) (tags `indexnode-v*`), check it against its `.sha256`, and rename it to `indexnode.exe`.
2. `indexnode config set-sas <container SAS URL>`.
3. **Brave profile.** Use a dedicated Brave profile for the node, not your daily one: `indexnode config set browser.profile-dir <folder>`, then `indexnode browser login` once and sign in to the site. The node drives it over the DevTools protocol (no Playwright, whose Node driver App Control may block).
4. **yt-dlp.** If App Control blocks `yt-dlp.exe`, run `indexnode tools install --ytdlp-zipapp` to install the Python zipapp build (needs Python); it records the result as `ytdlp.command` itself.
5. `indexnode run`.

## Setup on Linux

1. Install the .NET 10 runtime, `yt-dlp` and `ffmpeg` (on the PATH). Download `indexnode-<version>-linux-x64`, check the `.sha256`, `chmod +x`.
2. `indexnode config set-sas <container SAS URL>`, then `indexnode run`.
3. For Watch Later, start a Brave/Chromium profile and set `browser.exe` and `browser.profile-dir` as above.

## Build and test

```
dotnet build indexnode/IndexNode.Tests
dotnet test indexnode/IndexNode.Tests
```

Releases: bump `<Version>` in `IndexNode/IndexNode.csproj`, add a `## X.Y.Z` section to `RELEASE-NOTES.md` and land it on main. The main build tests, then, when the tag `indexnode-vX.Y.Z` does not exist yet, publishes the single-file (self-contained, framework-dependent, GPU) builds for `win-x64` and `linux-x64` with a `.sha256` each, creating the tag at that commit. A push without a version bump only builds and tests. Do not push release tags by hand.

## Nexis

Big blobs may be Nexis ghosts. [`IndexNode/Tools/NexisGhost.cs`](IndexNode/Tools/NexisGhost.cs) is a small port of the tag names, the `Timestamp` format and single-blob hydrate from Nexis (codebox commit `16c933d`); keep the two in step.

## Followed sources, limits and AI

- Priority (0.8.5): `enqueue <url>... --priority N`, `sources add|import ... --priority N`, `sources priority <source id|url> N` (re-tags the source's pending jobs; lists the playlist with yt-dlp for older jobs, so run it on a machine YouTube allows), `priority set N --job <id>... | --from <file> | --channel <id|url> [--stage S] [--with-steps]`, `priority list [--stage S] [--top 20]`. Higher runs first, default 0, range -1000..1000; claim order is priority, then added, then name; jobs a job creates inherit its priority. Medusa still claims in the old order. See PROTOCOL.md.
- `indexnode sources add <url> [--every 1h] [--backfill N] [--follow-channels]`, `sources list`, `sources remove <id>`, `sources import <channels.json> [--every 1d]`: followed playlists/channels become `watch` jobs, polled with yt-dlp's flat listing (no cookies). Default stages are `watch,process,comments`; `download` only with `--stages`.
- Limits: `--budget N`, `--schedule "<cron>" --window 3h` (or `config set budget|schedule|window`); `control.json` → `nodes.<nodeId>` overrides. Own cron parser. The registry shows `budget spent` / `next window <time>`.
- AI: `config set ai.base-url|ai.key|ai.text-model|ai.vision-model|ai.api` (`ai.api`: chat, responses or auto; auto retries a model on the Responses API when chat says unsupported_api_for_model), `--ai-*` flags or `INDEXNODE_AI_*` (flag > env > config); `indexnode ai test`. The key is redacted. No pipeline step uses it yet; `processed.json` lists `skipped`.

## Cast (faces)

`cast` finds the main characters without AI (buffalo_l: average linkage cut 0.65, centroid merge 0.5, main = at least 20 faces; `describe` returns `samePerson` groups that publish merges, so a character split over two clusters is harmless): face detection on the keyframes, clustering, a second merge pass, portraits, contact sheets (`cast/sheet_<cluster>.jpg`, 12 faces of each main cluster) and poster two-shot candidates. `cast.model` is `buffalo_l` (default) or `yunet-sface`. Licence: the InsightFace `buffalo_l` weights are non-commercial (research) licence; used here for internal data processing; YuNet and SFace are permissive and stay selectable. Real-data run on a processed job: `indexnode process --job <id> --steps cast,describe`, then `indexnode publish --job <id>`.

## Model checks

`scripts/check-models.sh` (set `REF` to the unpacked drama-ref pack) reruns the real-model checks on short cuts of the test drama. Last run, 4-core AMD EPYC runner:

- OCR 20:00-30:00: 208 of 215 reference cues matched with CER <= 0.1 (96.7%); 11.0 frames/s on one worker, 4.7 per core with 4 workers. RapidOCR (Python) on the same 200 frames, same machine: 10.7 frames/s.
- Clip (silver-mask query on 90:00-95:00: top 5 are 1:31:15, 1:30:54, 1:31:18, 1:30:50, 1:31:09; four show the masked man): cosine against transformers 1.0 on 5 frames when both get the same 224x224 pixels; 0.971-0.992 when the reference resizes with PIL (resize-filter difference only).
- Transcribe 20:00-25:00: CER 0.0657 against full_sensevoice.json (4 threads here, 16 in the reference), RTF 0.030. Accepted as is.
- Sync 20:00-30:00: +0.130 s (reference OCR with the reference transcript: +0.106 s). Accepted as is.


## GPU node (0.6.1)

`indexnode run --gpu --node-name gpu-a100-westus3 --exit-when-idle 10m` runs only the GPU stages (sscd, clip, cast; ocrsubs when listed in `--stages`) on the ONNX Runtime **CUDA** provider.

- **No silent CPU fallback.** If the CUDA provider cannot load or is not registered, the step fails with an explicit message. Small shape operations may run on the CPU (ONNX Runtime places them there on purpose); `INDEXNODE_CUDA_STRICT=1` refuses any CPU node. Every session logs its provider.
- **Models and batches.** SigLIP uses the fp16 vision model (186 MB), SSCD the fp32 model on the GPU (the int8 QDQ model stays the CPU default; the quantised ops are not all on CUDA). Batches are 64 (SSCD) and 128 (SigLIP) frames; the CPU keeps 32. Frame chunks are read from blob as before.
- **Only videos whose frames are done.** When the `frames` step finishes, it writes `index/gpu-ready/<step>.<jobId>` for each pending sscd/clip/cast job. The GPU node deletes the marker when the job is done or fails for good (not when it is released). `indexnode gpu markers [--dry]` writes the markers for jobs whose frames were made before 0.6.0.
- **GPU routing is opt-in.** GPU routing is off by default (0.8.6): CPU nodes claim sscd/clip/cast at once and no gpu-ready markers are written, so nothing wakes the GPU job. Turn it on for every CPU node at once, with no restart, by `indexnode control gpu-hold 30m` (`index/control.json` gets `"gpu": {"hold": "30m"}`); turn it off with `indexnode control gpu-hold off`. While it is on, a finished `frames` step writes the markers and a CPU node takes a marker-stage job only after it has been ready for the hold. A node's `--gpu-fallback <duration>` overrides the control file for that node (`--gpu-fallback off` forces no hold); a GPU node (`--gpu`) always claims its stages at once. Jobs that finished `frames` while routing was off have no marker: after switching it on, run `indexnode gpu markers` to write them. The hold counts from the marker's `readyAt` (without a marker, the later of `idx_added` and `idx_due`).
- **cast on the GPU.** Keyframes are detected in groups of 16 with 4 detector runs at once (the SCRFD model has a fixed batch of 1), and the faces of a group are embedded in one ArcFace batch (up to 64). The credits OCR runs on the GPU OCR sessions, the first and last 3-minute windows at the same time and every 2nd second (CPU nodes: every second, one after the other). Per-drama time: see the release notes for the measured value.
- **`--exit-when-idle <time>`** stops the node after that long with no job running (a container started for a marker leaves when the work is gone).
- **`--workers N`** (config `workers`, default 2, 1 on a GPU node): independent workers per node. Each takes the best waiting job across all the stages the node runs (priority, then added, then name), runs it and repeats; no limit is shared between them. `--threads` is split evenly across workers unless `--worker-threads N` is given. `--jobs` and `--max-heavy` are ignored (a warning is printed).
- **Timing.** Each job ends with `time by step: fetch 3.2s, sscd 41.0s [cuda]`, next to the existing per-phase throughput lines.

### Container and Azure Container Apps job

`indexnode/Dockerfile` (nvidia/cuda 12.8 + cuDNN 9 + .NET 10; ORT 1.23.2 GPU matches CUDA 12, CUDA 13 builds of ORT start at 1.27) is built and pushed to `ghcr.io/<owner>/indexnode-gpu` by `.github/workflows/indexnode-gpu.yml` on a push to main with a new IndexNode version (no image for it yet), or by hand. `--build-arg BAKE_MODELS=1` bakes the models in.

`indexnode/deploy/gpu-job.bicep` is an event-triggered job (`Microsoft.App/jobs`, A100 workload profile) with the KEDA `azure-blob` scaler on `index/gpu-ready/`. It is **not deployed** by anything here. Parameters you supply: the environment id, the workload profile name, the image, the container SAS (`INDEXNODE_SAS_URL`) and a scaler connection string (`BlobEndpoint=...;SharedAccessSignature=...`, list + read on the container). After deploying, check that one marker starts exactly one execution (`az containerapp job execution list`), and that the execution ends 10 minutes after the last marker is gone.
