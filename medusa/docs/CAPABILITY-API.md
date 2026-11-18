# Medusa capability API (/v2)

A general-purpose local API for native apps (for example the IndexNode app for Android) that want to use Medusa's tools:
yt-dlp, ffmpeg, ffprobe and the bundled Python. It is served by the same local server as `/v1`
(`http://127.0.0.1:<port>`, the port in `SERVER_PORT`). `/v1` (LinguaWeave) is unchanged.

Medusa must be running. The server is up whenever a Medusa screen is visible and for the keep-alive time after that (Settings, Background).
A run keeps Medusa alive until it ends.

## Pairing

Every `/v2` call except the two pairing calls needs the header `X-Medusa-Token`. Any app on the phone can reach 127.0.0.1, and yt-dlp's
`--exec` runs commands, so the user must allow each app once.

1. The app asks: `POST /v2/pair` with `{"appName": "IndexNode"}` (1 to 60 characters), or starts Medusa's activity for a result.
2. Medusa shows "Allow IndexNode to use Medusa's tools?" (a dialog, and a notification in case the app is in the background).
3. On Allow Medusa makes a random token and shows it to nobody: it goes back to the app once. Medusa keeps only its hash.
4. The app sends the token in `X-Medusa-Token` on every call. Settings lists the paired apps; Revoke makes the token fail at once.

HTTP flow:

```
POST /v2/pair            {"appName":"IndexNode"}  ->  {"requestId":"...","state":"pending","expiresInSec":120}
GET  /v2/pair/{requestId}                         ->  {"state":"pending"}
                                                      {"state":"approved","token":"mt_..."}   (once; then 404)
                                                      {"state":"denied"}
```

A request expires after 120 seconds; at most 5 wait at once (429). 404 means unknown or expired.

Intent flow (the token comes back as the activity result, which is safer than polling):

```kotlin
val i = Intent("io.ref12.medusa.action.PAIR").putExtra("appName", "IndexNode")   // .NET: new Intent("io.ref12.medusa.action.PAIR")
startActivityForResult(i, 1)
// onActivityResult: resultCode == RESULT_OK -> data.getStringExtra("token"); RESULT_CANCELED -> not allowed
```

Without or with a wrong token every call answers `401 {"error": ..., "code": "unauthorized"}`. Browsers (requests with an `Origin` header) are refused with 403 on `/v2`.

## Conventions

JSON in and out, errors are `{"error": "...", "code": "..."}`. Responses are `Cache-Control: no-store`.

## Tools

### GET /v2/capabilities

```json
{"api":2,"medusa":"1.10.0",
 "tools":[{"name":"yt-dlp","available":true,"version":"2025.01.15"},
          {"name":"ffmpeg","available":true,"version":"ffmpeg version 6.0 ..."},
          {"name":"ffprobe","available":true,"version":"..."},
          {"name":"python","available":true,"version":"Python 3.11.x"}],
 "limits":{"priorities":["interactive","background"],"maxConcurrentHeavyRuns":1,"maxConcurrentLightRuns":2,"maxQueuedRuns":20,"maxTimeoutSec":21600,"defaultTimeoutSec":600,
           "maxUploadBytes":2147483648,"maxArgs":500,"workspaceTtlHours":24,"maxWorkspaces":20}}
```

A tool that cannot run on this device has `"available": false` (starting it answers 409 `tool_unavailable`).

### POST /v2/runs

`{"tool":"yt-dlp","args":["-J","URL"],"workspace":"<id>","timeoutSec":600,"priority":"interactive"}` -> `{"runId":"...","state":"queued"}`.

`tool` is one of `yt-dlp`, `ffmpeg`, `ffprobe`, `python`. `args` is the argument array, passed as is (no shell). The tool runs with the
workspace as its working folder (yt-dlp also gets `--paths home:` and `temp:` pointing at it), so use relative file names.
`timeoutSec` is 1 to `maxTimeoutSec` (default `defaultTimeoutSec`); the run is killed when it passes.
Runs execute in Medusa's process. `priority` is optional: `interactive` (default, a caller is waiting) or `background`.

#### Queueing and concurrency
Every tool run waits its turn at one gate that Medusa's own /v1 downloads and its index node share, so a run is only `running` while its tool really runs.
- **Heavy** work (downloads, ffmpeg, python) is limited to `maxConcurrentHeavyRuns` at once (default 1). **Light** work (yt-dlp with `--flat-playlist`, `--skip-download`, `-J`, `-F` and similar listing or metadata options, ffprobe) has its own limit `maxConcurrentLightRuns` (default 2) and never waits behind a download. Both are set in Medusa's Settings, "Downloads at once".
- When a slot frees, waiting **interactive** work goes before waiting **background** work (the index node's work is background). A run that already runs is never interrupted.
- Up to `maxQueuedRuns` runs may wait (429 `queue_full` when full).
- A waiting run reports `"state":"queued"` plus `"queue":{"position":2,"waitingFor":["index-node download 12ab","v2 run ..."],"message":"Waiting for index-node download 12ab (and 1 more ahead)"}`. `position` 1 means next. `weight` and `priority` are in every run's JSON.
Errors: 400 bad request, 404 `unknown_workspace`, 409 `tool_unavailable`.

### GET /v2/runs/{id}

```json
{"runId":"...","tool":"yt-dlp","state":"running","exitCode":null,"progress":42.5,"lineCount":120,
 "queuedAt":1730000000000,"startedAt":1730000001000,"finishedAt":null,"workspace":"...","weight":"heavy","priority":"interactive","queue":{"position":1,"waitingFor":["..."],"message":"..."},"tail":["last 20 output lines"]}
```

`state`: `queued`, `running`, `succeeded`, `failed`, `cancelled`, `timed_out`. `progress` is yt-dlp's download percent (from its
`[download]  42.5%` lines), null for other tools and until one is seen. Finished runs are kept for 6 hours.

### GET /v2/runs/{id}/log?from=N&wait=S

Output lines (stdout and stderr together) as `text/plain`, one per line, from line index `from` (default 0). `wait` (up to 30) holds the
request until a new line arrives or the run ends. Headers: `X-Run-State`, `X-Log-From` (index of the first line returned; larger than
`from` when old lines were dropped, Medusa keeps the last 20000), `X-Log-Next` (use it as the next `from`).
Poll until `X-Run-State` is a finished state and no more lines come.

### POST /v2/runs/{id}/cancel

`{"runId":"...","state":"cancelled"|"running","cancelRequested":true}`. A queued run is cancelled at once; a running one is killed and
shows `cancelled` shortly after.

### POST /v2/tools/yt-dlp/update

Starts youtubedl-android's update of yt-dlp as a run (`tool` shows `yt-dlp-update`), returns `{"runId","state"}`; poll it like any run.
Asking again while one is active returns the same run.

## Workspaces

A workspace is a folder for one job's inputs and outputs. Paths are relative with `/`; no `..`, no absolute paths, no backslashes, no names
starting `.medusa-`, and links leading outside are refused (400 `bad_path`). Unused for 24 hours, a workspace is deleted.
Using it (runs, file calls) counts as use. At most `maxWorkspaces`.

| Call | Meaning |
|---|---|
| `POST /v2/workspaces` | `{"workspace":"<id>"}` |
| `DELETE /v2/workspaces/{id}` | deletes it and its files; 409 `workspace_busy` while a run uses it |
| `GET /v2/workspaces/{id}/files` | `{"files":[{"path":"a/b.mp4","size":123,"modified":1730000000000}]}` |
| `GET /v2/workspaces/{id}/files/{path}` | the file; supports `Range: bytes=...` (206 with `Content-Range`, 416 when out of range) |
| `PUT /v2/workspaces/{id}/files/{path}` | upload an input; needs `Content-Length`, at most `maxUploadBytes` (413); replaces the file; answers `{"path","size"}` |

## Device

`GET /v2/device`:
`{"network":"wifi|cellular|ethernet|vpn|other|none","vpn":false,"metered":false,"charging":true,"batteryPercent":80,"freeBytes":123456789}`
(a value Android will not tell is null). Use it to defer big downloads on cellular or a low battery.

## Example flow

```
POST /v2/pair {"appName":"IndexNode"}            -> requestId; user taps Allow; GET /v2/pair/{requestId} -> token
(all calls below send X-Medusa-Token: <token>)

POST /v2/workspaces                              -> {"workspace":"3f9c0a1b2d4e5f60"}

# 1. list a playlist
POST /v2/runs {"tool":"yt-dlp","workspace":"3f9c0a1b2d4e5f60","timeoutSec":300,
               "args":["--flat-playlist","-J","--no-warnings","https://www.youtube.com/playlist?list=PL..."]}  -> {"runId":"r1"}
GET  /v2/runs/r1/log?wait=30       (repeat with from=<X-Log-Next> until X-Run-State is finished)
     -> the JSON arrives on stdout, mixed with warnings on stderr: use "--no-warnings", or write it to a file instead:
        add "--print-to-file","%()j","playlist.json" and fetch it with GET /v2/workspaces/{id}/files/playlist.json

# 2. download one video with its info JSON, thumbnail and subtitles
POST /v2/runs {"tool":"yt-dlp","workspace":"3f9c0a1b2d4e5f60","timeoutSec":3600,
               "args":["-f","bv*[height<=720]+ba/b[height<=720]","--write-info-json","--write-thumbnail","--convert-thumbnails","jpg",
                       "--write-subs","--write-auto-subs","--sub-langs","en.*,zh.*","--sub-format","vtt/srt/best",
                       "-o","%(id)s.%(ext)s","https://www.youtube.com/watch?v=VIDEO_ID"]}  -> {"runId":"r2"}
GET  /v2/runs/r2                   -> {"state":"running","progress":42.5,...}   (poll every few seconds)
GET  /v2/runs/r2                   -> {"state":"succeeded","exitCode":0,...}

# 3. fetch the files
GET  /v2/workspaces/3f9c0a1b2d4e5f60/files
     -> VIDEO_ID.mp4, VIDEO_ID.info.json, VIDEO_ID.jpg, VIDEO_ID.en.vtt ...
GET  /v2/workspaces/3f9c0a1b2d4e5f60/files/VIDEO_ID.mp4      (add Range: bytes=START- to resume)

# 4. clean up
DELETE /v2/workspaces/3f9c0a1b2d4e5f60
```

Medusa does not retry or resume a run: if a run fails or the phone kills the app, start a new one (yt-dlp continues partial downloads
in the same workspace). Android may stop Medusa's server when it is in the background and idle; a running run keeps it alive.
