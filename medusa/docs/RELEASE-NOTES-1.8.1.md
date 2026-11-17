# Medusa 1.8.1

Index node: stop requesting YouTube's machine-translated subtitle tracks, which got downloads rate-limited.

- **Cause:** the node passed `--sub-langs ...,en.*` with `--write-auto-subs`; `en.*` matches every auto-translated track (en-ar, en-ja, ...), dozens of timedtext requests per video.
- **Fix:** the video is downloaded alone; then the info JSON is read and subtitles are fetched in two separate calls with exact keys: uploaded = the Chinese keys present (zh, zh-Hans, zh-Hant, zh-CN, zh-TW, zh-HK) plus every `en` / `en-*` key under `subtitles`; automatic = only `*-orig` tracks (zh-orig, en-orig). No wanted track, no request.
- **A subtitle failure no longer fails the job or backs off the node.** The failed tracks are recorded as `subsFailed` in `downloaded.json` and the job finishes. Only a failure of the video, or an explicit bot / sign-in message or HTTP 429 on the video, is a bot block (one-hour back-off). A subtitle-track error of any status (403, 404, 429) is never classified as a bot block.
- **Errors are shown whole:** the full yt-dlp "ERROR:" line (HTTP status and reason) is kept in the job's failure record (up to 500 characters) and shown in Recent jobs (220 characters instead of 60).
