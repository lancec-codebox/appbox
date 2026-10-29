# Medusa protocol, v1.1

Medusa (repo <https://github.com/lancec-codebox/appbox>, folder `medusa/`, package `io.ref12.medusa`) is an Android app that captures a media URL with yt-dlp on the phone: subtitles and video. A web app (first client: LinguaWeave) talks to it only through this protocol and never runs yt-dlp. Any URL yt-dlp supports works.

## 1. Waking the app (deep link)

Scheme `medusa`, host `open`. From Chrome on Android:

```
intent://open?job=<jobId>&url=<mediaUrl>&return=<returnUrl>#Intent;scheme=medusa;package=io.ref12.medusa;S.browser_fallback_url=<installUrl>;end
```

- `job`: id chosen by the web app, used as the job id.
- `url`: the media URL. `return`: optional; when the job finishes the app opens it with `job=<id>&state=done|failed` appended (to the query, or after `?` inside a hash).
- `browser_fallback_url`: opened when the app is not installed (point it at <https://github.com/lancec-codebox/appbox/releases>).

The link starts the local server and a job with the defaults below (both parts, 720p).

## 2. Local API

`http://127.0.0.1:38917`, running while jobs run and for 10 minutes after the last job or request. CORS origins: `https://apps--hexad-project.ref12.dev`, `https://ref12.github.io`, `http://localhost:5173` (editable in the app). Preflights answer `Access-Control-Allow-Private-Network: true`; methods GET, HEAD, POST, PUT, DELETE, OPTIONS; `Content-Range`, `Content-Length`, `Accept-Ranges` are exposed.

### Status

`GET /v1/status` → `{ "app": "medusa", "version": "1.1.0", "ytDlp": "..." }`

### Jobs

`POST /v1/jobs` body:

```json
{ "id"?: "uuid", "url": "https://...", "languages"?: ["zh-Hans", "zh-CN", "zh", "zh-Hant", "zh-TW"],
  "want"?: ["subtitles", "video"], "quality"?: "720p" }
```

→ `{ "id": "..." }`. Defaults: `want` both, `quality` `720p` (also `480p`, `360p`; video is MP4). A running job for the same URL and languages is reused and its id returned. When the media is already captured with the wanted parts, the job is created with the requested id and is `done` at once. Invalid input → 400.

`GET /v1/jobs/{id}` →

```json
{ "id": "...", "state": "queued" | "fetching" | "downloading" | "done" | "failed",
  "progress"?: 0.0-1.0, "code"?: "...", "message"?: "..." }
```

`progress` goes 0..1 and never backwards. `message` is human readable (a `done` job may carry a note when one wanted part failed). Failure `code`: `no_captions`, `bot_check`, `unsupported`, `network`, `storage`, `ytdlp`.

`GET /v1/jobs/{id}/result` (done; 409 otherwise) → the media item below.

### Media

Captures persist on the phone until deleted.

```json
{ "mediaId": "...", "url": "...", "title": "...", "durationSec": 2712, "extractor": "youtube", "videoId": "...",
  "video": { "path": "/v1/media/<id>/video", "contentType": "video/mp4", "bytes": 123 } | null,
  "subtitles": { "source": "captions" | "auto-captions", "language": "zh-Hans",
                 "lines": [ { "start": 12.34, "end": 15.02, "text": "..." } ] } | null,
  "sync": [ { "at": 10.0, "offset": 0.25 } ] }
```

Auto-captions are the video's spoken language only, never machine translations. Lines are plain text, `start`/`end` in seconds.

- `GET /v1/media` → array of media items without `lines`.
- `GET /v1/media/{id}` → one media item. `DELETE /v1/media/{id}` → `{ "deleted": true }` (404 if unknown).
- `GET /v1/media/{id}/video` → the MP4 with `Accept-Ranges: bytes`. Single `Range` requests: 206 with `Content-Range`, 416 for an unsatisfiable range, 200 for none.

### Sync

Sync is an ordered list of anchors. From `at` (video seconds, matched against a line's original `start`) subtitles are shown `offset` seconds later (negative: earlier) until the next anchor. No anchors: no shift.

- `GET /v1/media/{id}/sync` → `{ "sync": [...] }`
- `PUT /v1/media/{id}/sync` body `{ "sync": [...] }` replaces the list (sorted, one anchor per time, rounded to ms); 400 if invalid.

App rules: Earlier/Later change the anchor in effect by 0.25 s; when that anchor is over 30 s old (or none exists) a new anchor is created at the current line's start, carrying the current offset. Sync to this line sets an anchor so the line plays now. Reset here removes the anchor in effect; Reset all clears the list.

## 3. Fallback without the local API

If the browser blocks 127.0.0.1, the job screen's Save transcript writes the media item JSON to Downloads as `<title>.media.json`; the web app can import it (it has no video).

## Versions

v1: subtitles only. v1.1: video capture, media and sync APIs, `progress`, failure codes. Later: on-device speech recognition when no captions exist.
