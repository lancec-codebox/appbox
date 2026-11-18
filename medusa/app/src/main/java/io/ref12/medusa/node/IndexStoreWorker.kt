package io.ref12.medusa.node

import android.content.Context
import androidx.work.Constraints
import androidx.work.CoroutineWorker
import androidx.work.ExistingWorkPolicy
import androidx.work.NetworkType
import androidx.work.OneTimeWorkRequestBuilder
import androidx.work.WorkManager
import androidx.work.WorkerParameters
import io.ref12.medusa.app
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import java.io.File

/** Stores finished library downloads in the index (write-only), under the node's conditions: Wi-Fi (unmetered) unless mobile data is allowed, charging if the node needs it. Resumes after an interruption. */
class IndexStoreWorker(private val ctx: Context, params: WorkerParameters) : CoroutineWorker(ctx, params) {
    override suspend fun doWork(): Result = withContext(Dispatchers.IO) {
        val s = NodeSettings(ctx)
        val sas = s.vault.load() ?: return@withContext Result.success()
        if (!s.storeLibrary) return@withContext Result.success()
        val client = BlobClient(sas)
        var retry = false
        for (e in s.storeQueue.entries()) {
            if (isStopped) { retry = true; break }
            try {
                val stage = File(ctx.cacheDir, "index-store/" + e.mediaId).apply { mkdirs() }
                val media = ctx.app.media
                // the info JSON and thumbnail are not kept by the library: fetched in their own calls, never on the video request
                val m0 = media.get(e.mediaId)
                val id = m0?.let { IndexStoreFiles.youtubeId(it.url) }
                if (id == null) { s.storeQueue.remove(e.mediaId); continue }
                if (!File(stage, "info.json").isFile || !File(stage, "thumb.jpg").isFile) {
                    val f = YtDlpNodeFetcher(ctx.app.fetcher, s, NodeEnv(), ctx.app.gate)
                    val job = DownloadJob(id, "https://www.youtube.com/watch?v=" + id, 720, null, false)
                    val raw = File(stage, "raw").apply { mkdirs() }
                    if (!File(stage, "info.json").isFile) f.fetchInfo(job, raw)?.copyTo(File(stage, "info.json"), true)
                    if (!File(stage, "thumb.jpg").isFile) f.fetchThumb(job, raw)?.copyTo(File(stage, "thumb.jpg"), true)
                    raw.deleteRecursively()
                }
                val files = IndexStoreFiles.collect(media, e.mediaId, stage)
                if (files == null) { s.storeQueue.remove(e.mediaId); continue }
                var last = -1
                val r = IndexStore.store(client, files, e.mode, s.nodeId, s.storePriority, NodeRunner.iso(System.currentTimeMillis()), e.done, { isStopped },
                    { sent, total -> val pct = (sent * 100 / total).toInt(); if (pct != last) { last = pct; s.lastStatus = ""; NodeLive.store = "Storing " + (m0.title.take(40)) + " \u2014 " + pct + "%"; StatusNotifier.refresh(ctx, s, null) } },
                    { s.storeQueue.update(e) })
                s.log("stored " + id + " in the index: " + r.name.lowercase() + " (" + e.mode.wire + ")")
                s.storeQueue.remove(e.mediaId); stage.deleteRecursively()
            } catch (x: BlobCancelled) { retry = true; break
            } catch (x: Exception) {
                e.attempts++; e.lastError = Redact.sas(x.message ?: x.javaClass.simpleName).take(300)
                s.log("store of " + e.mediaId + " failed: " + e.lastError); s.storeQueue.update(e)
                if (e.attempts < 8) retry = true else s.storeQueue.remove(e.mediaId)
            } finally { NodeLive.store = null }
        }
        if (retry) Result.retry() else Result.success()
    }

    companion object {
        const val UNIQUE = "index-store"
        fun constraints(s: NodeSettings) = Constraints.Builder()
            .setRequiredNetworkType(if (s.storeMobile) NetworkType.CONNECTED else NetworkType.UNMETERED)
            .setRequiresBatteryNotLow(true).setRequiresStorageNotLow(true).setRequiresCharging(s.conditions == NodeConditions.CHARGING_WIFI).build()

        /** Queues the work (the queue itself is kept in the settings, so a killed process loses nothing). */
        fun enqueue(ctx: Context, s: NodeSettings = NodeSettings(ctx)) {
            if (!s.storeLibrary || !s.vault.has()) return
            WorkManager.getInstance(ctx).enqueueUniqueWork(UNIQUE, ExistingWorkPolicy.KEEP, OneTimeWorkRequestBuilder<IndexStoreWorker>().setConstraints(constraints(s)).build())
        }
    }
}
