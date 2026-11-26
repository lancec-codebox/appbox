# IndexNode for Android

A phone node of the drama index. A small .NET 11 (CoreCLR, untrimmed) Android host with a WebView UI loads the node logic as a **plugin DLL**, which uses the same `IndexNode.Core` as the desktop node (same protocol, claim order, download stage, registry record). yt-dlp, ffmpeg and ffprobe run in Medusa on the same phone.

## Layout
- `Contract/` (netstandard2.1, `IndexNode.Contract`): `IHost`, `IPlugin`, `ITools`, `IToolWorkspace`. The only thing host and plugin share.
- `Loader/` (net10.0, plain): hash-verified plugin loading in a collectible context, `PluginManager` (update, smoke test, last good, bundled fallback), `Bridge` (WebView message router), `HostPolicy` (run conditions, setup parsing, node id, redaction), `StoreGate` (the SAS is added here only).
- `Medusa/` (net10.0): `MedusaClient` + `MedusaV2Tools` (pairing, runs, workspaces, Range reads), `MedusaV1Tools` (plain-video fallback).
- `Plugin/` (net11.0): `NodePlugin`: the node loop on `IndexNode.Core`, using only `IHost` and `ITools`. One DLL: Core is embedded as a resource.
- `Host/` (net11.0-android): activity, WebView page (`Assets/web/index.html`), WorkManager worker, `AndroidHost`, settings. No node logic.
- `Tests/`: xunit (loader, bridge, policy, Medusa against a fake server, the plugin on a fake store). Linux, no Android needed.

## Build and test
```
dotnet test indexnode-android/Tests -c Release            # 61 tests
dotnet workload install android                            # once; SDK 11
dotnet build indexnode-android/Plugin -c Release           # -> Plugin/bin/Release/net11.0/IndexNode.Plugin.dll
dotnet build indexnode-android/Host -c Release <signing>   # -> Host/bin/Release/net11.0-android/*-Signed.apk
```
Debug signing: `keytool -genkeypair -keystore debug.keystore -storepass android -keypass android -alias androiddebugkey -keyalg RSA -keysize 2048 -validity 10000 -dname "CN=Android Debug"`, then `-p:AndroidKeyStore=true -p:AndroidSigningKeyStore=debug.keystore -p:AndroidSigningStorePass=android -p:AndroidSigningKeyAlias=androiddebugkey -p:AndroidSigningKeyPass=android`.

## Release notes

### 0.1.6 (host 0.1.6, versionCode 7, plugin 0.1.2; needs IndexNode 0.13.62 on the service)
- **A Search tab** (owner, 2026-11-25). Choose a picture, or share a screenshot to IndexNode from another app (the app is a share target for pictures), then:
  - **Find the scene:** the dramas the picture looks like, best first, each with the matched frame and the frames around it (tap a frame to open the video at that moment), and "Who is in my picture?" to compare the faces with that drama's own cast.
  - **Find the actors:** each face of the picture with the actors it looks like; tap an actor for the dramas they appear in and shots of them in each, the face marked.
- **How it talks to the service.** The page calls one bridge method, `service`, with `{op: status|search|actors|frames|actor|shots, ...}`; `Loader/ServiceCalls.cs` turns each into one fixed route of `indexnode serve` (`/v1/search`, `/v1/frames`, `/v1/actors/...`) with the check server's token, so the page cannot make the app call anything else. The service is the check server of the setup. When the setup has none, or its address never answers, the app reads `index/service.json` in the store (`{"url": "https://...", "token": "..."}`), once per app start. The service sleeps when idle: a call waits up to 150 s while it wakes ("Waking the service…").
- **Links leave the app.** A link to a video opens in the phone's YouTube app or browser, not inside the page.
- **Fix: the top of the page was hidden.** 0.1.5 drew Android's own title bar over the page's header. The activity has no title bar now and the page is padded by the system bars and the keyboard.
- Picture limits: the page reduces a picture to 1600 px on its long side before sending; a shared picture may be up to 24 MB.
- Not verified on a phone (compiled by CI only): the picture chooser, the share target, the window padding. The page itself was rendered at phone width against real answers of the service. Tests: `Tests/ServiceCallsTests.cs`.
- The "Download a video" card and the HD copy planned for 0.1.6 move to the next build.

### 0.1.5 (host 0.1.5, versionCode 6, plugin 0.1.2)
- **The app updates itself, like Medusa.** On start, and from Settings, App, "Check for update", it reads `host.json` on the rolling release (`indexnode-android-plugin`), which CI writes for each new host version: the version, the APK's URL in this repository's releases, its SHA-256 and size. A newer version shows as a banner on the Status tab with an **Update** button. The app downloads the APK, checks the size and hash, and hands it to a PackageInstaller session; Android shows its own confirmation. The first time, Android asks to allow IndexNode to install apps: allow it, come back, tap Update again. An update is refused while a run is in progress. Rules in `Loader/HostUpdate.cs` (tested); the Android part is `Host/AppUpdater.cs`.
- **A new page.** Four tabs: Status (state, Run now and Stop, a "Ready to run" checklist that names what blocks a run: store, Medusa, network, power, daily cap, check server, plugin), Activity (the log with a filter, Share, Clear), Setup (three steps: store, Medusa pairing, check server), Settings (app and update, when to run, limits, work, plugin, diagnostics). Light and dark follow the phone.
- **Reload.** The ↻ button in the header loads the page again from its source (the page in the app, or the UI URL) with the WebView's cache cleared.
- **Fix:** the app reported "host 0.1.3" in 0.1.4. The version is now read from the build (`ApplicationDisplayVersion` as assembly metadata), so the page, the diagnostics and the plugin's host check always match the APK.
- 0.1.4 and older have no updater: 0.1.5 is installed by hand once (the APK from release `indexnode-android-v0.1.5`), and later versions from inside the app.
- Not verified on a phone: the update's install step (the PackageInstaller session and Android's prompt) has only been compiled.

### 0.1.4 (host 0.1.4, versionCode 5, plugin 0.1.2; IndexNode 0.13.42)
- **One downloader for the desktop and the phone.** The plugin no longer has its own copy of the node wiring: it runs `DownloaderNode` from IndexNode.Core, the same class the desktop node uses for its download stages (download, sample, refresh, availability, and the two calls to the check server). The phone differs only in its tools (Medusa instead of local yt-dlp and ffmpeg), its store transport (the host adds the SAS) and where the settings come from. A downloader feature added to Core reaches the phone with the next plugin, without a reinstall.
- **New on the phone because of that:** the `sample` step (two 5-minute segments at 360p; a node that runs `download` takes samples too), the inline check of a sample, and the download service's hand-over (`POST /v1/download/next`) when the work queue has nothing ready.
- **The check server in the setup.** `indexnode medusa-setup` adds this node's check server (config `check.url` and its token) to the QR and the file as `checkUrl` and `checkToken`; `--no-check` leaves them out, and `--no-sas` leaves the token out. The host keeps them in its private preferences and redacts the token like the SAS. A phone set up before 0.1.4 keeps working without a check server (work queue only); scan a new setup QR to add it.
- **Fix (IndexNode.Core):** a download service that had nothing to hand over still counted as one started job, against `--max-jobs` and against the phone's daily cap.
- The plugin published until now (0.1.1, built 2026-11-19) predates the job queue and cannot claim work; 0.1.2 is the first that runs on the current store. Needs a supervised first run on a phone: the sample step has not run through Medusa yet.

### 0.1.3 (host 0.1.3, versionCode 4, plugin 0.1.1)
- **Fixes from the Pixel log:** the diagnostics page loads (the bridge allow-list lacked `getDiagnostics`/`clearDiagnostics`; a test now checks the page, `Bridge.Methods` and `MainActivity` agree), "Run now" or a WorkManager start during a run logs "a run is already in progress" and is a no-op instead of "run failed", and the phone reports one stable node instance (saved `instanceId`) so a restart no longer warns "another process is already reporting" about its own last heartbeat.

### 0.1.1 + 0.1.2 (host 0.1.2, versionCode 3)
- **Fix: "Run now" crashed the app.** WorkManager's `SystemForegroundService` is now declared in the manifest with `foregroundServiceType="dataSync"` (merged into WorkManager's own entry), which `startForeground` needs on Android 10+ (and Android 14 insists on). `RunWorker` waits for the foreground promotion inside its `try`: a refusal is logged and the run continues without the notification instead of killing the app; it also implements `ForegroundInfo` for expedited work on API < 31.
- **Crash diagnostics.** Every unhandled failure (Android/AppDomain/unobserved task/Java default handler) writes `last-crash.txt` in the app's files dir (UTC time, host and plugin versions, device, Android version, exception type, message, full stack) and a rolling log of the last ~500 lines is kept. The top of the app shows the last crash and the recent log, with **Copy diagnostics** (no SAS or `sig=` ever) and **Clear**.
- 0.1.1: a VPN over Wi-Fi counts as Wi-Fi, precise status verdicts (no network, Wi-Fi without internet, on mobile data, Wi-Fi is metered, not charging), WorkManager uses `NetworkType.Connected` and the worker decides, releases are created with `--latest=false`.

## Releases (`.github/workflows/indexnode-android.yml`)
Tests, plugin and APK build on every push; no store secret anywhere. On `main`: a new host version (`ApplicationDisplayVersion` in Host.csproj) publishes release `indexnode-android-v<version>` with the APK; `host.json` on the rolling release then names that version, its APK and the APK's SHA-256 (what the app's own update check reads); a new plugin version (`<Version>` in Plugin.csproj) replaces the DLL and `manifest.json` on the rolling release `indexnode-android-plugin`. Signing uses the Medusa release keystore: secrets `MEDIA_HELPER_KEYSTORE_BASE64`, `MEDIA_HELPER_KEYSTORE_PASSWORD` (store) and `MEDIA_HELPER_KEY_PASSWORD` (key, a separate secret), alias `vars.MEDUSA_KEY_ALIAS` or `media-helper`, the same as `medusa.yml`. On `main` a missing keystore secret **fails the build**, so CI never publishes a debug-signed APK; branches and forks without the secrets are debug-signed (with a warning) so the tests and the build still run. A stable key matters: later APKs install over this one only when signed with the same key. Bump `ApplicationVersion` with the host version.

## How it works
- **Plugin updates**: manifest `{version, sha256, dll, minHost}` at the rolling release; checked on app start and before each run. The DLL is hash-checked, loaded beside the running plugin and must pass its `SmokeAsync` (no network) before the host switches; any failure leaves the running plugin alone. The last good version and the one before it stay on disk; the plugin bundled in the APK is the last resort.
- **Store and SAS**: the SAS lives in the app's private preferences. The plugin builds store requests against the container URL without it; `AndroidHost.StoreAsync` appends the SAS natively, refuses paths outside the container, and redacts logs and exceptions.
- **Refresh (IndexNode 0.10.3):** the plugin runs download and refresh, like a desktop download node: a lookup when a source is added, then its schedule. Setting "No refresh" is `--no-refresh`. Listing calls go to Medusa as background runs (light: `--flat-playlist`, `-J`).
- **Medusa**: `/v2` after pairing (the token is kept in preferences); until Medusa ships /v2 (or when not paired) the setting "Use Medusa /v1" gives plain video downloads only: no subtitles, listings or ffmpeg.
- **Runs**: WorkManager periodic job (default about hourly) plus an expedited "Run now". Conditions Wi-Fi only and charging only (both on by default), daily cap, budget; a foreground notification with a static icon shows during a run.
- **Setup**: `indexnode medusa-setup` QR/file (`medusa://node-setup?d=...`, the https page link, or the JSON): SAS, node name, conditions, cap, budget. Node id `android-<model>-<hex>`, kind `android`.

## Phone checklist
1. **Install** `indexnode-android.apk` from release `indexnode-android-v0.1.0` (https://github.com/lancec-codebox/appbox/releases/tag/indexnode-android-v0.1.0); allow installing from your browser or file manager when asked. Medusa 1.10.0 is already on the phone: open it once so its local server runs.
2. **Turn off Medusa's own index node** so the two do not both download: in Medusa open **Settings, Index node settings** and switch **Enable the index node** off (the node shows "Index node is off"). Medusa still serves yt-dlp and ffmpeg to this app.
3. **Setup.** On the desktop where `indexnode` is signed in to the store (and, for the check server, has `check.url` in its config), run `indexnode medusa-setup --name <phone name> --qr` (add `--daily-cap N`, `--budget N` or `--conditions wifi` if wanted; `--file <path>.medusa-node.json` writes a file instead). It draws a QR and saves a PNG and a `.medusa-node.json` in `~/.local/share/indexnode/setup` (on Windows the user profile equivalent). The QR and file contain the store SAS: keep them private. In the app, scan the QR with the phone camera (it opens the app), or paste the `medusa://node-setup?d=...` link, the https link or the file's JSON into the **Setup** box and tap **Import setup**. Status then shows the store, the node name and a node id like `android-<model>-<hex>`.
4. **Pair with Medusa:** tap **Pair with Medusa**, approve the request in Medusa, tap again: Pairing shows **Paired: yes**.
5. **Conditions:** **Wi-Fi only** needs an unmetered, validated Wi-Fi (or ethernet) connection, directly or under a VPN (a VPN over Wi-Fi counts as Wi-Fi, and Metered comes from the Wi-Fi under it). The Status screen shows the verdict (no network, Wi-Fi connected no internet, on mobile data, Wi-Fi is metered, not charging) and the raw facts (active transport, vpn, metered, validated). In Settings keep **Wi-Fi only** and **Charging only** on (both are the default), save; plug the phone in on Wi-Fi.
6. **Run now** with at least one pending download job in the store (`indexnode enqueue <url>`). Success looks like: a foreground notification "IndexNode" with the video and progress; Status shows state running, the current job and progress, then **Today: 1**; in the store `index/videos/<id>/download.zip` and `downloaded.json` appear (`nodeId` is the phone's) and the job is `done`; the node is listed in the registry with kind `android` (`indexnode status`); the log (**Share**) ends without an error.
7. **Plugin update without reinstalling:** Plugin shows the bundled version. After a newer plugin is on the rolling release `indexnode-android-plugin` (bump `<Version>` in `Plugin.csproj`, merge to main), tap **Check for update** (or just start a run: it checks first). The page shows the new version and the log says `plugin switched <old> -> <new>`. A manifest with a lower or equal version does nothing.

## Changes from the spike
- Spike branding, demo plugins (V1, V2, Probe), release assets, the Mono/AOT variant and the trimming variants removed: the product is one variant (CoreCLR, untrimmed); ReadyToRun is not applied when trimming is off (XA0119), so the APK is JIT only.
- The contract grew from three HTTP calls to the capabilities above; the plugin gets the store only through `StoreAsync` (the spike gave plugins raw HTTP).
- The loader verifies, smoke-tests and keeps versions (the spike only verified); the collectible context detaches its `Resolving` handler on unload (otherwise it is never collected).
- The WebView bridge keeps the spike's origin rules but routes through the tested `Bridge` class with a fixed method list.
