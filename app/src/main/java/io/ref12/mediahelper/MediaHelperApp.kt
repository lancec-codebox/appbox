package io.ref12.mediahelper

import android.app.Application
import java.io.File
import java.util.concurrent.Executors

class MediaHelperApp : Application() {
    lateinit var settings: Settings
        private set
    lateinit var fetcher: YtDlpFetcher
        private set
    lateinit var jobs: JobManager
        private set

    override fun onCreate() {
        super.onCreate()
        settings = Settings(this)
        fetcher = YtDlpFetcher(this, settings)
        // One job at a time: yt-dlp is heavy and the phone is small.
        jobs = JobManager(Storage(File(filesDir, "data")), fetcher, Executors.newSingleThreadExecutor())
    }
}

val android.content.Context.app get() = applicationContext as MediaHelperApp
