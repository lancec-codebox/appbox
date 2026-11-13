package io.ref12.medusa.node

import org.junit.Assert.*
import org.junit.Assume.assumeTrue
import org.junit.Test
import java.io.File

/** Runs only when INDEX_LIVE_SAS is set; works under index-test-medusa/ and removes everything it wrote. */
class LiveBlobTest {
    @Test fun claimAndUploadAgainstTheRealContainer() {
        val raw = System.getenv("INDEX_LIVE_SAS")
        assumeTrue(!raw.isNullOrBlank())
        val sas = SasUrl.parse(raw!!)!!
        val c = BlobClient(sas, root = "index-test-medusa")
        val id = "live" + System.currentTimeMillis()
        val job = "index-test-medusa/jobs/download/$id.json"
        val video = "index-test-medusa/videos/$id/video.mp4"
        val marker = "index-test-medusa/videos/$id/downloaded.json"
        val created = listOf(job, video, marker)
        val log = StringBuilder()
        try {
            val tags = mapOf("idx_stage" to "download", "idx_state" to "pending", "idx_added" to NodeRunner.iso(System.currentTimeMillis()))
            assertTrue(c.put(job, ("{\"v\":1,\"jobId\":\"$id\",\"attempts\":0}").toByteArray(), "application/json", tags))
            // the tag index can lag a little
            var found: JobRef? = null
            for (i in 0 until 20) { found = c.findJobs("download").firstOrNull { it.name == job }; if (found != null) break; Thread.sleep(2000) }
            log.append("find: " + (if (found != null) "found" else "missing") + " (sp=" + sas.permissions + ")\n")
            assertNotNull("the pending job should be found by tags", found)
            assertEquals("pending", found!!.tags["idx_state"])
            val lease = c.acquireLease(job, 60); assertNotNull(lease); log.append("lease acquired\n")
            assertNull("a second claim must get a 409", c.acquireLease(job, 60)); log.append("second claim refused\n")
            c.renewLease(job, lease!!); log.append("renewed\n")
            c.setMetadata(job, lease, mapOf("node" to "live-test", "progress" to "50%", "heartbeat" to NodeRunner.iso(System.currentTimeMillis())))
            val f = File.createTempFile("live", ".mp4").apply { writeBytes(ByteArray(9 * 1024 * 1024 + 123) { (it % 251).toByte() }); deleteOnExit() }
            var last = 0L
            c.putBlocks(video, f, "video/mp4", emptyMap(), { false }) { sent, _ -> last = sent }
            assertEquals(f.length(), last)
            val back = c.get(video); assertEquals(200, back.status); assertArrayEquals(f.readBytes(), back.body); log.append("uploaded 2 blocks, " + f.length() + " bytes read back equal\n")
            assertTrue(c.put(marker, "{\"v\":1}".toByteArray(), "application/json"))
            assertEquals("done only with the lease id", true, try { c.put(job, "{}".toByteArray(), "application/json", tags.plus("idx_state" to "done")); false } catch (e: BlobException) { e.status == 412 })
            c.put(job, "{\"v\":1,\"done\":true}".toByteArray(), "application/json", tags.plus("idx_state" to "done"), lease)
            assertEquals("done", c.getTags(job)["idx_state"]); log.append("rewritten with lease, tags kept\n")
            c.releaseLease(job, lease); log.append("released\n")
            assertNotNull("free again after release", c.acquireLease(job, 15)?.also { c.releaseLease(job, it) })
        } finally {
            for (p in created) try { c.delete(p) } catch (e: Exception) { log.append("cleanup failed for $p: " + e.message + "\n") }
            val left = c.listNames("index-test-medusa/")
            log.append("left under index-test-medusa/: " + left.size + "\n")
            File(System.getenv("LIVE_LOG") ?: "/tmp/live.log").writeText(log.toString())
            assertEquals(emptyList<String>(), left)
        }
    }
}
