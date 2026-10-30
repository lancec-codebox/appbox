# Medusa protocol, v1.3

Medusa (repo <https://github.com/lancec-codebox/appbox>, folder `medusa/`, package `io.ref12.medusa`) is an Android app that downloads a media URL with yt-dlp on the phone: subtitles (in any language) and video. A web app (first client: LinguaWeave) talks to it only through this protocol and never runs yt-dlp. Any URL yt-dlp supports works.

## 1. Waking the app (deep link)

Scheme `medusa`, host `open`. From Chrome on Android:

```
intent://open?job=<jobId>&url=<mediaUrl>&return=<returnUrl>#Intent;scheme=medusa;package=io.ref12.medusa;S.browser_fallback_url=<installUrl>;end
```

- `job`: id chosen by the web app, used as the job id.
- `url`: the media URL. `return`: optional; when the job finishes the app opens it with `job=<id>&state=done|failed` appended (to the query, or after `?` inside a hash).
- `browser_fallback_url`: opened when the app is not installed (point it at <https://github.com/lancec-codebox/appbox/releases>).

Optional `languages=<comma list>` (for example `languages=en,fr`) works like `languages` in `POST /v1/jobs`. The link starts the local server and a job with the defaults below (both parts, 720p, the video's original language).

## 2. Local API

`http://127.0.0.1:38917`, running while jobs run and for 10 minutes after the last job or request. CORS origins: `https://apps--hexad-project.ref12.dev`, `https://ref12.github.io`, `http://localhost:5173` (editable in the app). Preflights answer `Access-Control-Allow-Private-Network: true`; methods GET, HEAD, POST, PUT, DELETE, OPTIONS; `Content-Range`, `Content-Length`, `Accept-Ranges` are exposed.

### Status

`GET /v1/status` → `{ "app": "medusa", "version": "1.4.0", "ytDlp": "..." }`

### Jobs

`POST /v1/jobs` body:

```json
{ "id"?: "uuid", "url": "https://...", "languages"?: ["en", "fr"],   // or e.g. ["zh-Hans", "zh-CN", "zh"]
  "want"?: ["subtitles", "video"], "quality"?: "720p" }
```

→ `{ "id": "..." }`. `languages` is optional and has no default: when omitted (or empty) Medusa picks the video's original spoken language, manual captions first, then that language's auto-captions. When given, the first language with a track wins, manual captions before auto-captions; `zh` and `zh-Hans` count as the same family, `zh-Hans` and `zh-Hant` do not. Defaults: `want` both, `quality` `720p` (also `480p`, `360p`; video is MP4). A running job for the same URL and languages is reused and its id returned. When the media is already downloaded with the wanted parts, the job is created with the requested id and is `done` at once. Invalid input → 400.

`GET /v1/jobs/{id}` →

```json
{ "id": "...", "state": "queued" | "fetching" | "downloading" | "done" | "failed",
  "progress"?: 0.0-1.0, "code"?: "...", "message"?: "..." }
```

`progress` goes 0..1 and never backwards. `message` is human readable (a `done` job may carry a note when one wanted part failed). Failure `code`: `no_captions` (no captions in the requested languages, or none at all when none were requested; the message names the languages that exist), `bot_check`, `unsupported`, `network`, `storage`, `ytdlp`.

`GET /v1/jobs/{id}/result` (done; 409 otherwise) → the media item below.

### Media

Downloads persist on the phone until deleted.

```json
{ "mediaId": "...", "url": "...", "title": "...", "durationSec": 2712, "extractor": "youtube", "videoId": "...",
  "video": { "path": "/v1/media/<id>/video", "contentType": "video/mp4", "bytes": 123 } | null,
  "subtitles": { "source": "captions" | "auto-captions", "language": "zh-Hans",
                 "lines": [ { "start": 12.34, "end": 15.02, "text": "..." } ] } | null,
  "sync": [ { "at": 10.0, "offset": 0.25 } ] }
```

`title` is always the original title. When the site serves the title in the user's language (YouTube translated titles) and it differs, the item also has `"localizedTitle"` and `"localizedLanguage"` (e.g. `"en"`); both are absent otherwise (v1.3.1, additive: older clients ignore them). Clients show `localizedTitle` first and the original under it.

`subtitles` is the chosen (main) track with its lines. `tracks` lists every subtitle track the video offers (`[]` for items downloaded before v1.2); auto-captions are the video's spoken language only, never machine translations. Lines are plain text, `start`/`end` in seconds.

- `GET /v1/media` → array of media items without `lines`.
- `GET /v1/media/{id}` → one media item. `DELETE /v1/media/{id}` → `{ "deleted": true }` (404 if unknown).
- `GET /v1/media/{id}/video` → the MP4 with `Accept-Ranges: bytes`. Single `Range` requests: 206 with `Content-Range`, 416 for an unsatisfiable range, 200 for none.

### Subtitle tracks

- `GET /v1/media/{id}/subtitles?language=xx` → `{ "source", "language", "lines": [...] }` of that track. Downloads it on demand (once, then kept) and does not change the item's main track. 400 without `language`; 404 with code `no_captions` when the video has no such track, 502 for other failures (body `{ "error", "code" }`).
- `POST /v1/media/{id}/subtitles` body `{ "language": "xx" }` makes that track the main one and returns the updated media item. Same errors.

### Added tracks (v1.3)

Another app can add a subtitle track it produced (for example a translation) so it plays in Medusa.

- `POST /v1/media/{id}/tracks` body `{ "language": "en", "label"?: "English (AI)", "source": "added", "origin"?: "linguaweave", "lines": [ { "start", "end", "text" } ] }` → `{ "trackId" }`. An added track with the same language and origin is replaced (and keeps its trackId); other origins and the downloaded tracks stay.
- The item's `tracks` then lists it as `{ "language", "source": "added", "label", "trackId", "origin"? }` (`name` is the same text as `label`).
- `GET /v1/media/{id}/subtitles?track=<trackId>` (with or without `language`) → `{ "source": "added", "language", "lines" }`. 404 `no_captions` for an unknown trackId.
- `DELETE /v1/media/{id}/tracks/{trackId}` → `{ "deleted": true }`; 404 if the media or the track is unknown. Only added tracks can be deleted.
- Validation (400 `{ "error", "code": "invalid_track" }`): `language` is a tag like `en` or `pt-BR`; `source`, when given, is `added`; `origin` is 1-40 characters of letters, digits, `.`, `_`, `-`; `label` is plain text, at most 60 characters; `lines` is not empty (at most 20000), sorted by `start`, `start` >= 0, `end` > `start`, `text` a non-empty plain string (no markup or control characters, at most 2000 characters). A body over 2 MB is refused with 413. 404 for unknown media.

Sync anchors belong to the item (video time), not to a track: they apply to added tracks exactly as to downloaded ones, and changing tracks keeps them.

### Subtitle files (import and export)

Medusa opens and receives shared `.srt`, `.vtt` and `*.medusa-track.json` files. The JSON is the `POST .../tracks` body plus optional `mediaId`, `url` and `title`; Medusa picks the library item by `mediaId`, else by `url` (any spelling of the same video), else asks. The item screen and the player's subtitle menu export any track (or all) as SRT (default), WebVTT or that JSON, named `<title> [<language>].<ext>`, with Share and Save to Downloads.

### Sync

Sync is an ordered list of anchors. From `at` (video seconds, matched against a line's original `start`) subtitles are shown `offset` seconds later (negative: earlier) until the next anchor. No anchors: no shift.

- `GET /v1/media/{id}/sync` → `{ "sync": [...] }`
- `PUT /v1/media/{id}/sync` body `{ "sync": [...] }` replaces the list (sorted, one anchor per time, rounded to ms); 400 if invalid.

App rules: Earlier/Later change the anchor in effect by 0.25 s; when that anchor is over 30 s old (or none exists) a new anchor is created at the current line's start, carrying the current offset. Sync to this line sets an anchor so the line plays now. Reset here removes the anchor in effect; Reset all clears the list.

## 3. Fallback without the local API

If the browser blocks 127.0.0.1, the job screen's Save transcript writes the media item JSON to Downloads as `<title>.media.json`; the web app can import it (it has no video).

## Versions

v1: Chinese subtitles only. v1.1: video download, media and sync APIs, `progress`, failure codes. v1.2: subtitles in any language: no default language (original spoken language when omitted), `tracks`, the `subtitles` endpoints, `languages` in the deep link. v1.1 clients keep working: they send explicit `languages`, and the item keeps every v1.1 field. v1.3: added tracks (producer-supplied subtitles), the `track` query on `/subtitles`. v1.3.1: `localizedTitle` and `localizedLanguage` on media items. Later: on-device speech recognition when no captions exist.
