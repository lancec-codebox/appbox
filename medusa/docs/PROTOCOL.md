# Medusa protocol, v1

The Medusa is a separate, open-source Android app (repo Ref12/medusa, package `io.ref12.medusa`). It fetches a timed transcript for a media URL with yt-dlp on the phone and hands it to a web app (first client: LinguaWeave, the language app). The web app never runs yt-dlp and knows nothing about it; it only speaks this protocol. Generic, not YouTube-only: any URL yt-dlp supports (YouTube, Bilibili, TikTok, iQIYI ...).

## 1. Waking the helper (deep link)

The helper declares a BROWSABLE + DEFAULT activity for the scheme `medusa`, host `open`.

From a web page, Chrome on Android opens it with an intent URL (all parameter values URL-encoded):

```
intent://open?job=<jobId>&url=<mediaUrl>&return=<returnUrl>#Intent;scheme=medusa;package=io.ref12.medusa;S.browser_fallback_url=<installUrl>;end
```

- `job`: an id the web app chose (UUID); the helper uses it as the job id.
- `url`: the media URL.
- `return`: optional. When the job finishes the helper opens this URL with `job=<id>&state=done|failed` appended (as query parameters if it has no `#`, otherwise appended to the hash after `?`), which brings the user back to the web app.
- `browser_fallback_url`: Chrome opens it when the helper isn't installed; the web app points it at its install page.

Opening the link starts the helper's local server (section 2) and the job. The helper shows a small screen with progress and a Back to app button, and keeps working in a foreground service with a notification while a job runs.

## 2. Local API

While running, the helper serves HTTP on `http://127.0.0.1:38917`. It stops 10 minutes after the last job ends and no request has arrived.

CORS: allow origins `https://apps--hexad-project.ref12.dev`, `https://ref12.github.io`, `http://localhost:5173` (configurable list in the helper's settings). Answer preflights including `Access-Control-Allow-Private-Network: true` (Chrome's local network access).

- `GET /v1/status` → `{ "app": "medusa", "version": "1.0.0", "ytDlp": "2026.10.xx" }`
- `POST /v1/jobs` body `{ "id"?: string, "url": string, "languages"?: string[] }` → `{ "id": string }`. Default languages: `["zh-Hans", "zh-CN", "zh", "zh-Hant", "zh-TW"]`. Same URL + languages returns the cached result's job.
- `GET /v1/jobs/{id}` → `{ "id", "state": "queued" | "fetching" | "done" | "failed", "message"?: string }`
- `GET /v1/jobs/{id}/result` (state done) →

```json
{
  "url": "https://www.youtube.com/watch?v=...",
  "title": "...",
  "durationSec": 2712,
  "extractor": "youtube",
  "videoId": "...",
  "source": "captions" | "auto-captions",
  "language": "zh-Hans",
  "lines": [ { "start": 12.34, "end": 15.02, "text": "你好，我是..." } ]
}
```

`start`/`end` are seconds. Lines are plain text (no markup), in order, non-overlapping where the source allows. Traditional-only captions are returned as they are with `language` saying so; the web app may convert.

Failures carry a human message: no Chinese captions, site not supported, network, yt-dlp out of date (the helper updates yt-dlp itself and retries once first).

Results are kept on the phone for 30 days and keyed by URL + languages.

## 3. Fallback without the local API

If the web app cannot reach `127.0.0.1` (browser blocks it, permission denied), the helper's job screen offers Save transcript, which writes the same result JSON to Downloads as `<title>.media.json`. The web app offers Import transcript file and accepts that file.

## Versions

v1: captions and auto-captions only. v2 (later): on-device speech recognition (sherpa-onnx with SenseVoice Small or Paraformer) when there are no Chinese captions, transcribing just ahead of playback; `source: "asr"`.
