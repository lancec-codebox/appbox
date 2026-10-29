# Medusa

An Android app (package `io.ref12.medusa`) that captures a video link (YouTube, Bilibili, TikTok and other sites yt-dlp supports) on the phone: the video as 720p MP4 by default and its subtitles in any language (the video's original spoken language by default, or the language you pick). It plays the capture itself, with app-drawn subtitles you can nudge into sync, and serves it to a web app on the same phone over a local HTTP API. The first client is LinguaWeave. The API is in [docs/PROTOCOL.md](docs/PROTOCOL.md).

Install: download the APK from <https://github.com/lancec-codebox/appbox/releases> (tags `medusa-v*`) and allow your browser or file manager to install apps. yt-dlp inside the app updates itself.

## Using it

- **Library**: captured media on the phone. Tap to play, long-press to delete.
- **Player**: Media3/ExoPlayer with subtitles drawn by the app. Buttons: Earlier / Later 0.25 s, Sync to this line, Reset here, Reset all. Sync is stored per media as anchors and shared with web apps through the API.
- **Capture** field on the main screen: paste a link to test without a web app. Choose the subtitle language (original by default, none, or a language from the video's own list after "List this video's languages"). A default language can be set in the same screen.
- **Player track menu**: switch the subtitle track live, and optionally show a second track under the first (for example the original and English).

## Updates

Medusa checks the GitHub Releases of lancec-codebox/appbox when it opens (at most once a day; switch it off in the main screen) and on **Check for updates**. It considers only published, non-prerelease releases tagged `medusa-vX.Y.Z`. A newer one appears as a card with its release notes and Update / Later. It never starts while a capture runs. The APK is downloaded to the app's cache, its SHA-256 is checked against the release asset's `digest` (or the `medusa-X.Y.Z.apk.sha256` asset the release workflow attaches) and it is installed through Android's PackageInstaller. The first time, Android asks you to allow Medusa to install apps. Only APKs signed with the same key can replace the installed app.

## Building

```
cd medusa
./gradlew :app:testDebugUnitTest :app:assembleDebug   # app/build/outputs/apk/debug/medusa-<version>-debug.apk
./gradlew :app:connectedDebugAndroidTest              # local-server smoke tests, needs an emulator or phone
```

JDK 17, Android SDK 36. Releases: push a tag `medusa-vX.Y.Z`; `.github/workflows/medusa.yml` builds `medusa-X.Y.Z.apk`, signs it (secrets and key alias: see `docs/RELEASE.md`) and attaches it to a GitHub Release.

## How it works

`JobActivity` takes `medusa://open` links. `HelperService` (foreground) runs NanoHTTPD on `127.0.0.1:38917`. `JobManager` runs one job at a time through `YtDlpFetcher` (youtubedl-android and ffmpeg): subtitles via `--dump-single-json` plus one subtitle download (`TrackSelector`, `SubtitleConverter`), video via an MP4 format selection merged with ffmpeg. `MediaStore` keeps each capture as plain files under `media/<id>/`. `SyncMath` holds the sync rules, `RangeSupport` the HTTP Range logic.

Track choice: without a requested language, the video's original spoken language (manual captions, then its auto-captions); with requested languages, the first one that has a track, manual before auto. Auto-captions are only the spoken-language track, never a machine translation. Every offered track is listed in the item's `tracks` and can be fetched on demand.

yt-dlp is updated when over a day old at job start, and once more with a retry after a yt-dlp failure. The library bundles QuickJS for YouTube's JS challenges.
