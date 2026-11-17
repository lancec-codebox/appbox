# Medusa 1.8.2

Hotfix: tapping **Run now** crashed the app.

- **Cause (likely, high confidence):** "Run now" built an expedited one-time WorkManager request with the constraints Wi-Fi + battery not low + storage not low. WorkManager rejects an expedited request that has anything but a network constraint: `build()` throws `IllegalArgumentException` ("Expedited jobs only support network and storage constraints" / "cannot be battery not low constrained" depending on the version) on the main thread inside the click handler, so the app died. Fixed: the request keeps only the Wi-Fi (unmetered) condition; the daily cap, budget and control rules still apply inside the run. The foreground service manifest entry (dataSync + FOREGROUND_SERVICE_DATA_SYNC), the notification channel creation and the FLAG_IMMUTABLE PendingIntents were already correct; the notification's open-app intent no longer assumes a launch intent exists.
- **The Run now button and the notification action never crash:** any error is caught and shown as a toast with the reason ("Run now failed: <type: message>").
- **Last crash:** an uncaught exception (type, message, stack, app version, UTC time; SAS redacted) is written to `files/last-crash.txt`. The Index node screen shows "Last crash: <time> <type: message>" with **Share crash** (plain text) and **Clear crash** buttons.
