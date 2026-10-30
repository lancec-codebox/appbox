package io.ref12.medusa

import android.app.Application
import java.io.File
import java.util.concurrent.Executors

class MedusaApp : Application() {
    lateinit var settings: Settings
        private set
    lateinit var fetcher: YtDlpFetcher
        private set
    lateinit var media: MediaStore
        private set
    lateinit var jobs: JobManager
        private set
    lateinit var ocr: OcrManager
        private set

    /** Medusa screens currently on screen (main thread only), and when the last one went away. */
    @Volatile var visibleScreens = 0
        private set
    @Volatile var lastUiLeft = System.currentTimeMillis()
        private set

    val updates: UpdateManager by lazy {
        UpdateManager(BuildConfig.VERSION_NAME, cacheDir, HttpUpdateSource(), PackageApkInstaller(this),
            Executors.newSingleThreadExecutor(), System::currentTimeMillis,
            { settings.autoUpdateCheck }, { settings.lastAppUpdateCheck }, { settings.lastAppUpdateCheck = it },
            { jobs.activeCount() + ocr.activeCount() > 0 })
    }

    override fun onCreate() {
        super.onCreate()
        settings = Settings(this)
        fetcher = YtDlpFetcher(this, settings)
        val root = File(filesDir, "data")
        media = MediaStore(root)
        // One job at a time: yt-dlp is heavy and the phone is small.
        jobs = JobManager(Storage(root), media, fetcher, Executors.newSingleThreadExecutor(), { settings.titleLanguage() }, onNoSubtitles = { id -> settings.setOcrOffered(id, true) })
        ocr = OcrManager(this, media)
        // one-off device-language titles for items downloaded before 1.5.2: in the background, one at a time
        Thread {
            val lang = settings.titleLanguage() ?: return@Thread
            TitleBackfill(
                ids = { media.list().filter { Titles.supportsLocalized(it.extractor) }.map { it.mediaId } },
                needs = { settings.titleChecked(it) != lang },
                busy = { jobs.activeCount() > 0 },
                lookup = { id -> jobs.refreshTitle(id, done = { l -> settings.titleChecked(id) == l }, mark = { l -> settings.setTitleChecked(id, l) }) },
            ).run()
        }.apply { isDaemon = true; name = "title-backfill" }.start()
        // the server is up whenever a Medusa screen is: start it when one opens, and time the keep-alive from when the last one closes
        registerActivityLifecycleCallbacks(object : android.app.Application.ActivityLifecycleCallbacks {
            override fun onActivityStarted(activity: android.app.Activity) {
                visibleScreens++
                try { HelperService.start(this@MedusaApp) } catch (e: Exception) { HelperService.lastError = e.message }
            }
            override fun onActivityStopped(activity: android.app.Activity) {
                visibleScreens = maxOf(0, visibleScreens - 1)
                lastUiLeft = System.currentTimeMillis()
            }
            override fun onActivityCreated(activity: android.app.Activity, savedInstanceState: android.os.Bundle?) {}
            override fun onActivityResumed(activity: android.app.Activity) {}
            override fun onActivityPaused(activity: android.app.Activity) {}
            override fun onActivitySaveInstanceState(activity: android.app.Activity, outState: android.os.Bundle) {}
            override fun onActivityDestroyed(activity: android.app.Activity) {}
        })
    }
}

val android.content.Context.app get() = applicationContext as MedusaApp
