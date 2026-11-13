# IndexNode

The desktop node of the drama index, the counterpart of [Medusa](../medusa/) on Android. Both share work through one Azure Blob Storage container, reached by a container SAS URL. IndexNode downloads videos with yt-dlp (also from a Brave profile's Watch Later list), extracts frames, computes SSCD descriptors, compresses to AV1 and collects comments, and writes everything back to the container.

Protocol (blob layout, jobs, tags, Nexis ghosting): [PROTOCOL.md](IndexNode/PROTOCOL.md).

## Commands

```
indexnode run --stages download,process,comments [--once] [--max-jobs N] [--jobs process=2,download=1] [--threads N]
              [--sources watch-later --source-every 6h] [--max-height 480]
indexnode enqueue <url|playlist-url>... [--max-height 480] [--format F]
indexnode enqueue --from watch-later [--max-height 480]
indexnode browser login | status
indexnode tools install --ytdlp-zipapp
indexnode status
indexnode requeue [--stage S]
indexnode control run|drain|stop [--stage download|process|comments] [--message M]
indexnode config set-sas <url>
indexnode config set <jobs|threads|model-url|model-path|node-name|ytdlp.command|browser.exe|browser.profile-dir|browser.port> <value>
```

All commands take `--prefix` (default `index/`). `INDEXNODE_SAS_URL` overrides the stored SAS.

## Setup on Windows

1. Install the .NET 10 runtime. Download `indexnode-<version>-win-x64.exe` from the [releases](https://github.com/lancec-codebox/appbox/releases) (tags `indexnode-v*`), check it against its `.sha256`, and rename it to `indexnode.exe`.
2. `indexnode config set-sas <container SAS URL>`.
3. **Brave profile.** Use a dedicated Brave profile for the node, not your daily one: `indexnode config set browser.profile-dir <folder>`, then `indexnode browser login` once and sign in to the site. The node drives it over the DevTools protocol (no Playwright, whose Node driver App Control may block).
4. **yt-dlp.** If App Control blocks `yt-dlp.exe`, run `indexnode tools install --ytdlp-zipapp` to install the Python zipapp build (needs Python); it records the result as `ytdlp.command` itself.
5. `indexnode run --stages download,process,comments`.

## Setup on Linux

1. Install the .NET 10 runtime, `yt-dlp` and `ffmpeg` (on the PATH). Download `indexnode-<version>-linux-x64`, check the `.sha256`, `chmod +x`.
2. `indexnode config set-sas <container SAS URL>`, then `indexnode run --stages download,process,comments`.
3. For Watch Later, start a Brave/Chromium profile and set `browser.exe` and `browser.profile-dir` as above.

## Build and test

```
dotnet build indexnode/IndexNode.Tests
dotnet test indexnode/IndexNode.Tests
```

Releases: push a tag `indexnode-vX.Y.Z`; the workflow publishes single-file, framework-dependent `indexnode` for `win-x64` and `linux-x64` with a `.sha256` each. The version comes from the tag.

## Nexis

Big blobs may be Nexis ghosts. [`IndexNode/Tools/NexisGhost.cs`](IndexNode/Tools/NexisGhost.cs) is a small port of the tag names, the `Timestamp` format and single-blob hydrate from Nexis (codebox commit `16c933d`); keep the two in step.
