# Medusa 1.8.4

Index node: downloads no longer fail on YouTube's 403 for video data.

- **Cause:** the node asked for WebM/AV1/VP9 + Opus (`-S res:480`, `bv*+ba[acodec=opus][abr<=80]/bv*+ba/b`). YouTube often answers 403 for those formats to clients without PO tokens, so jobs failed with "unable to download video data: HTTP Error 403: Forbidden" after 3 attempts.
- **Retry as MP4:** on a 403 for the video (not for subtitles) the node retries once in the same run with Medusa's own MP4 selector (`bv*[height<=H][ext=mp4]+ba[ext=m4a]/b[height<=H][ext=mp4]/b[height<=H]`, H = the job's maxHeight, default 480). `downloaded.json` records `"formatFallback": "mp4"` when that happened; processing does not care about the codec.
- **A 403 is not the job's fault:** it never counts toward the 3 attempts. If the MP4 retry also gets 403 the job goes back to **pending** (not failed) with `lastError` set, and this node leaves it alone for 6 hours (other nodes may take it).
- **Bot blocks unchanged:** 429, "sign in to confirm you're not a bot" and similar still release the job and back the node off for an hour.
- **Status:** the headline says "Downloading: <title> \u2014 403 on video, retrying as MP4" during the retry; Recent jobs shows "released: 403 twice".
- Tests: classification (403 video, 403 subtitles, 429, bot), one MP4 retry, no retry for other errors, attempts untouched, release to pending after two 403s, the 6-hour skip (and another node not affected), the `formatFallback` marker.
