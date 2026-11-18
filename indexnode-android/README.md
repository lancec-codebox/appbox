# IndexNode on Android: hot-loaded DLL spike (throwaway)

A .NET for Android host with a WebView UI loads node logic as DLLs fetched at run time (verify sha256, collectible AssemblyLoadContext, swap v1 to v2), and v2 drives Medusa's local API.

## Layout
- `Contract/` netstandard2.1 `IPlugin` and `IHost` (http get/put/send, cache dir, log, status).
- `Loader/` fetch, hash-verify-then-load, unload (plain net10.0, no Android code). `Loader.Tests/` 2 xunit tests.
- `Host/` the Android app: one WebView (`Assets/web/index.html`), JS bridge, `SpikeHost` (IHost + lifecycle).
- `Plugin.V1/` (hello), `Plugin.V2/` (Medusa flow). `Probe.IndexNodeRef/` (references the IndexNode project).
- `release/` APKs, `Plugin.V1|V2.dll` + `.json` manifests, `mock-medusa.py` (stand-in for Medusa on the emulator), SHA256SUMS.

## Build (Linux runner, no Windows or macOS needed)
```
dotnet workload install android                       # SDK 11.0.100 (Android 37.2.31); once
dotnet test Loader.Tests -c Release                    # hash-verify-then-load test
dotnet build Plugin.V1 -c Release; dotnet build Plugin.V2 -c Release   # then sha256 into Plugin.V*.json
SIGN="-p:AndroidKeyStore=true -p:AndroidSigningKeyStore=debug.keystore -p:AndroidSigningStorePass=android -p:AndroidSigningKeyAlias=androiddebugkey -p:AndroidSigningKeyPass=android"
# A: .NET 11, CoreCLR, ReadyToRun (the default runtime of .NET 11 for Android):
dotnet build Host -c Release $SIGN                                    # -> bin/Release/net11.0-android/*-Signed.apk
# B: .NET 10, Mono + full AOT (Mono is not supported on .NET 11: NETSDK1242):
cp global.net10.json global.json && dotnet workload install android   # SDK 10.0.404
dotnet build Host -c Release -p:HostTfm=net10.0-android $SIGN; rm global.json
```
Debug keystore: `keytool -genkeypair -keystore debug.keystore -storepass android -keypass android -alias androiddebugkey -keyalg RSA -keysize 2048 -validity 10000 -dname "CN=Android Debug"`.
Add `-p:RuntimeIdentifier=android-x64 -p:RuntimeIdentifiers=android-x64` for an emulator build.
Release builds take about 1 to 3 minutes each on a 4-core runner.

## Release csproj settings (Host/Host.csproj)
- `PublishTrimmed=true`, `TrimMode=partial`; `AndroidEnableProfiledAot=false`; `InvariantGlobalization=true`.
- A (net11, CoreCLR): `PublishReadyToRun=true`. **`RunAOTCompilation` is rejected with CoreCLR (XA1044).** JIT is always available, so loading is no problem.
- B (net10, Mono): `RunAOTCompilation=true`, `UseInterpreter=false`. The plugin DLL is not AOT-compiled, so it runs on the Mono JIT (allowed on Android); no interpreter is needed.
- `JsonSerializerIsReflectionEnabledByDefault=true`: trimmed apps turn reflection JSON off, which breaks any plugin using `JsonSerializer.Serialize(obj)`.
- **Trimming is the real hazard.** The host trims BCL code it does not use, and a plugin that calls one fails at run time. Found on device: `RuntimeInformation.OSArchitecture` raised MissingMethodException in v2. Fix used: `RootCoreLib=true` (root all of System.Private.CoreLib; about +3 MB per APK) plus `TrimmerRootAssembly` entries (RootMode=All) for System.Linq, Collections, Text.Json, Text.RegularExpressions, Net.Http and the contract/loader. `ILLink.Descriptors.xml` is the narrower per-type option. The cost: every BCL API a plugin may use must be kept, so the BCL cannot be trimmed.
- `network_security_config.xml` allows cleartext only to 127.0.0.1 and localhost (Medusa is http on loopback).

## Variant 3: untrimmed (CoreCLR, net11) - `release/indexnode-android-spike-untrimmed.apk`
Build: `dotnet build Host -c Release -p:Untrimmed=true $SIGN` (PublishTrimmed=false, no RootCoreLib, no trimmer roots or descriptor; ReadyToRun still on).
Plugins can use any .NET API: v2's `RuntimeInformation.OSArchitecture` call, which failed when trimmed without roots, works. arm64 APK 38.3 MB (x64 40.5 MB) against 16.5 MB trimmed. Emulator (x64): first screen 3.3 s, PSS 190 MB at start, 211 MB after loading v1, about 219 MB after v2 and its Medusa run, contexts collected after 1 GC round.

## WebView and bridge
`AndroidX.WebKit` `WebViewCompat.AddWebMessageListener` (package `Xamarin.AndroidX.WebKit`) is the better choice over `AddJavascriptInterface`: it takes an origin allow-list, is delivered with the source origin and an isMainFrame flag, and is JSON-over-strings. The page is served from the bundled assets through `WebViewAssetLoader` at `https://appassets.androidplatform.net/assets/web/index.html`, a real https origin. The bridge object `hostBridge` exists only for that origin, or for the origin of the UI URL the user saved (https, or http on loopback); the listener checks `isMainFrame` and the origin again. A `setUiUrl` call saves a remote page URL (blank resets to the bundled page); a failing remote page falls back to the bundled one. Bridge calls: `loadPlugin(manifestUrl)`, `runPlugin(args)`, `unload()`, `getLog()`, `getInfo()`, `setUiUrl(url)`; replies are `{id, ok, result|error}` JSON.
Works in the Release builds on the emulator (both variants). Awkward: the listener must be re-registered before every load of a new origin; `JavaScriptReplyProxy.PostMessage` is strings only, so JSON is double-encoded; `AddJavascriptInterface` would expose the object to every frame and offers no origin check, so it needs hand-written checks; on a WebView without `WEB_MESSAGE_LISTENER` support (very old system WebView) the bridge is missing and the page shows `bridge:NO`.

## Results (see the report in the final message for numbers and caveats)
Measured on an **x86_64 Android 34 emulator** (KVM), not on an ARM64 phone. ARM64 APKs were built but never run.
Test hook (test only): `adb shell am start -n <activity> --es js "<javascript run in the page after load>"`.

## Checklist for the phone
1. Install `indexnode-android-spike.apk` (allow unknown sources) next to Medusa; start Medusa so its local server runs.
2. Open "IndexNode Spike", tap **Load v1**, then **Run**: the log shows "hello from version 1".
3. Tap **Load v2** (v1 is unloaded first), then **Run**: v2 does the Medusa flow (status, job, result, media, 1 MB range read); the log shows each response.
4. Tap **Info** and **Unload** to see memory (pssKb) and whether the context was collected.
5. If the GitHub asset URLs are not reachable (private repo), type the manifest URLs into the two fields (`...Plugin.V1.json`; the DLL is fetched from the same name with `.dll`).
