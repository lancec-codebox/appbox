# IndexNode

The desktop node of the drama index, the counterpart of [Medusa](../medusa/) on Android. Both share work through one Azure Blob Storage container, reached by a container SAS URL. IndexNode downloads videos with yt-dlp (also from a Brave profile's Watch Later list), extracts frames, computes SSCD descriptors, compresses to AV1 and collects comments, and writes everything back to the container.

Protocol (blob layout, jobs, tags, Nexis ghosting): [PROTOCOL.md](IndexNode/PROTOCOL.md).

## Commands

```
indexnode run [--once] [--max-jobs N] [--jobs process=2,download=1] [--threads N]
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

Releases: push a tag `indexnode-vX.Y.Z`; the workflow publishes single-file, framework-dependent `indexnode` for `win-x64` and `linux-x64` with a `.sha256` each. The version comes from the tag.

## Nexis

Big blobs may be Nexis ghosts. [`IndexNode/Tools/NexisGhost.cs`](IndexNode/Tools/NexisGhost.cs) is a small port of the tag names, the `Timestamp` format and single-blob hydrate from Nexis (codebox commit `16c933d`); keep the two in step.

## Followed sources, limits and AI

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


## GPU node (0.6.0)

`indexnode run --gpu --node-name gpu-a100-westus3 --exit-when-idle 10m` runs only the GPU stages (sscd, clip, cast; ocrsubs when listed in `--stages`) on the ONNX Runtime **CUDA** provider.

- **No silent CPU fallback.** If the CUDA provider cannot load, or a model has a node that would run on the CPU, the step fails with an explicit message (`INDEXNODE_CUDA_ALLOW_CPU_NODES=1` relaxes only the second check, for diagnostics). The first session logs the provider.
- **Models and batches.** SigLIP uses the fp16 vision model (186 MB), SSCD the fp32 model (the int8 QDQ model is a CPU model). Batches are 64 (SSCD) and 128 (SigLIP) frames; the CPU keeps 32. Frame chunks are read from blob as before.
- **Only videos whose frames are done.** When the `frames` step finishes, it writes `index/gpu-ready/<step>.<jobId>` for each pending sscd/clip/cast job. The GPU node deletes the marker when the job is done or fails for good (not when it is released). `indexnode gpu markers [--dry]` writes the markers for jobs whose frames were made before 0.6.0.
- **CPU fallback.** A CPU node takes an sscd/clip/cast job only after it has been ready for 30 minutes (marker `readyAt`; without a marker the later of `idx_added` and `idx_due`), so nothing stalls when the GPU is off. `--gpu-fallback 30m` changes the wait.
- **`--exit-when-idle <time>`** stops the node after that long with no job running (a container started for a marker leaves when the work is gone).
- **`--max-heavy N`** (config `max-heavy`, default 2, 1 on a GPU node, 0 = no limit): concurrent model steps per node; each gets its share of the threads. Downloads, uploads, comments, watch and describe do not count.
- **Timing.** Each job ends with `time by step: fetch 3.2s, sscd 41.0s [cuda]`, next to the existing per-phase throughput lines.

### Container and Azure Container Apps job

`indexnode/Dockerfile` (nvidia/cuda 12.8 + cuDNN 9 + .NET 10; ORT 1.23.2 GPU matches CUDA 12, CUDA 13 builds of ORT start at 1.27) is built and pushed to `ghcr.io/<owner>/indexnode-gpu` by `.github/workflows/indexnode-gpu.yml` on `indexnode-v*` tags or by hand. `--build-arg BAKE_MODELS=1` bakes the models in.

`indexnode/deploy/gpu-job.bicep` is an event-triggered job (`Microsoft.App/jobs`, A100 workload profile) with the KEDA `azure-blob` scaler on `index/gpu-ready/`. It is **not deployed** by anything here. Parameters you supply: the environment id, the workload profile name, the image, the container SAS (`INDEXNODE_SAS_URL`) and a scaler connection string (`BlobEndpoint=...;SharedAccessSignature=...`, list + read on the container). After deploying, check that one marker starts exactly one execution (`az containerapp job execution list`), and that the execution ends 10 minutes after the last marker is gone.
