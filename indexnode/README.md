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
indexnode tools install --ytdlp-zipapp
indexnode status
indexnode requeue [--stage S]
indexnode control run|drain|stop [--stage download|process|comments|watch] [--message M]
indexnode config set-sas <url>
indexnode config set <jobs|threads|model-url|model-path|node-name|ytdlp.command|browser.exe|browser.profile-dir|browser.port> <value>
```

All commands take `--prefix` (default `index/`). `INDEXNODE_SAS_URL` overrides the stored SAS.

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
- AI: `config set ai.base-url|ai.key|ai.text-model|ai.vision-model`, `--ai-*` flags or `INDEXNODE_AI_*` (flag > env > config); `indexnode ai test`. The key is redacted. No pipeline step uses it yet; `processed.json` lists `skipped`.

## Model checks

`scripts/check-models.sh` (set `REF` to the unpacked drama-ref pack) reruns the real-model checks on short cuts of the test drama. Last run, 4-core AMD EPYC runner:

- OCR 20:00-30:00: 208 of 215 reference cues matched with CER <= 0.1 (96.7%); 11.0 frames/s on one worker, 4.7 per core with 4 workers. RapidOCR (Python) on the same 200 frames, same machine: 10.7 frames/s.
- Clip (silver-mask query on 90:00-95:00: top 5 are 1:31:15, 1:30:54, 1:31:18, 1:30:50, 1:31:09; four show the masked man): cosine against transformers 1.0 on 5 frames when both get the same 224x224 pixels; 0.971-0.992 when the reference resizes with PIL (resize-filter difference only).
- Transcribe 20:00-25:00: CER 0.0657 against full_sensevoice.json (4 threads here, 16 in the reference), RTF 0.030. Accepted as is.
- Sync 20:00-30:00: +0.130 s (reference OCR with the reference transcript: +0.106 s). Accepted as is.
