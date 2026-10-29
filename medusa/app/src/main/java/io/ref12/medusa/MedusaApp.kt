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

    val updates: UpdateManager by lazy {
        UpdateManager(BuildConfig.VERSION_NAME, cacheDir, HttpUpdateSource(), PackageApkInstaller(this),
            Executors.newSingleThreadExecutor(), System::currentTimeMillis,
            { settings.autoUpdateCheck }, { settings.lastAppUpdateCheck }, { settings.lastAppUpdateCheck = it },
            { jobs.activeCount() > 0 })
    }

    override fun onCreate() {
        super.onCreate()
        settings = Settings(this)
        fetcher = YtDlpFetcher(this, settings)
        val root = File(filesDir, "data")
        media = MediaStore(root)
        // One job at a time: yt-dlp is heavy and the phone is small.
        jobs = JobManager(Storage(root), media, fetcher, Executors.newSingleThreadExecutor())
    }
}

val android.content.Context.app get() = applicationContext as MedusaApp
