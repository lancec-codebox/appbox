package io.ref12.medusa.node

import okhttp3.mockwebserver.MockResponse
import okhttp3.mockwebserver.MockWebServer
import org.junit.After
import org.junit.Assert.*
import org.junit.Before
import org.junit.Test
import java.io.File
import java.net.URLDecoder

class BlobClientTest {
    private lateinit var server: MockWebServer
    private lateinit var client: BlobClient
    private fun sas(sp: String = "racwdlf") = SasUrl.parse(server.url("/media").toString() + "?sv=2022-11-02&sp=$sp&sig=SECRETSIG%2Bx")!!

    @Before fun up() { server = MockWebServer(); server.start(); client = BlobClient(sas(), retryDelayMs = 0, blockSize = 10) }
    @After fun down() { server.shutdown() }

    private val findXml = """<?xml version="1.0"?><EnumerationResults><Blobs>
        <Blob><Name>index/jobs/download/bbb.json</Name><Tags><TagSet><Tag><Key>idx_stage</Key><Value>download</Value></Tag><Tag><Key>idx_state</Key><Value>pending</Value></Tag><Tag><Key>idx_added</Key><Value>2026-11-13T19:00:02Z</Value></Tag></TagSet></Tags></Blob>
        <Blob><Name>index/jobs/download/aaa.json</Name><Tags><TagSet><Tag><Key>idx_stage</Key><Value>download</Value></Tag><Tag><Key>idx_state</Key><Value>pending</Value></Tag><Tag><Key>idx_added</Key><Value>2026-11-13T19:00:01Z</Value></Tag></TagSet></Tags></Blob>
        </Blobs><NextMarker/></EnumerationResults>"""

    /** Azure's Find Blobs by Tags returns only the tags the where expression names: this server does the same. */
    private fun azureFind(blobs: Map<String, Map<String, String>>) = object : okhttp3.mockwebserver.Dispatcher() {
        val wheres = java.util.Collections.synchronizedList(ArrayList<String>())
        override fun dispatch(request: okhttp3.mockwebserver.RecordedRequest): MockResponse {
            val where = request.requestUrl!!.queryParameter("where") ?: return MockResponse().setResponseCode(400)
            wheres.add(where)
            val conds = Regex("\"([^\"]+)\"\\s*(=|>=|>|<)\\s*'([^']*)'").findAll(where).map { Triple(it.groupValues[1], it.groupValues[2], it.groupValues[3]) }.toList()
            val xml = StringBuilder("<?xml version=\"1.0\"?><EnumerationResults><Blobs>")
            for ((name, tags) in blobs) {
                if (!conds.all { (k, op, v) -> val t = tags[k]; t != null && when (op) { "=" -> t == v; ">=" -> t >= v; ">" -> t > v; else -> t < v } }) continue
                xml.append("<Blob><Name>$name</Name><Tags><TagSet>")
                for ((k, v) in tags) if (conds.any { it.first == k }) xml.append("<Tag><Key>$k</Key><Value>$v</Value></Tag>")
                xml.append("</TagSet></Tags></Blob>")
            }
            return MockResponse().setBody(xml.append("</Blobs><NextMarker/></EnumerationResults>").toString())
        }
    }

    private fun job(added: String, priority: Int = 0) = mapOf("idx_stage" to "download", "idx_state" to "pending", "idx_added" to added, "idx_due" to added) + (if (priority != 0) mapOf("idx_priority" to priority.toString()) else emptyMap())

    @Test fun findSeesPriorityAndDateEvenThoughAzureReturnsOnlyTheQueriedTags() {
        val d = azureFind(linkedMapOf(
            "index/jobs/download/aaa.json" to job("2026-11-14T00:00:01Z"), "index/jobs/download/bbb.json" to job("2026-11-14T00:00:00Z"),
            "index/jobs/download/ccc.json" to job("2026-11-14T00:00:05Z", -10), "index/jobs/download/zzz.json" to job("2026-11-18T00:00:00Z", 100)))
        server.dispatcher = d
        val jobs = Prio.claimOrder(client.findJobs("download"))
        // priority 100 first (added last, named last), then the oldest at priority 0, then the negative one
        assertEquals(listOf("zzz", "bbb", "aaa", "ccc"), jobs.map { it.id })
        assertEquals(100, Prio.of(jobs[0].tags)); assertEquals(-10, Prio.of(jobs[3].tags)); assertEquals("2026-11-14T00:00:00Z", jobs[1].added)
        assertEquals(3, d.wheres.size)
    }

    @Test fun findByTagsEncodesWhereAndSortsOldestFirst() {
        server.dispatcher = object : okhttp3.mockwebserver.Dispatcher() { override fun dispatch(request: okhttp3.mockwebserver.RecordedRequest) = MockResponse().setBody(findXml) }
        val jobs = client.findJobs("download")
        assertEquals(listOf("aaa", "bbb"), jobs.map { it.id })
        val r = server.takeRequest()
        assertEquals("GET", r.method)
        assertEquals("2021-12-02", r.getHeader("x-ms-version"))
        assertEquals("/media", r.requestUrl!!.encodedPath)
        assertEquals("container", r.requestUrl!!.queryParameter("restype"))
        assertEquals("blobs", r.requestUrl!!.queryParameter("comp"))
        assertEquals("\"idx_stage\"='download' AND \"idx_state\"='pending' AND \"idx_added\" >= ''", r.requestUrl!!.queryParameter("where"))
        assertTrue(r.path!!.contains("where=%22idx_stage%22%3D%27download%27%20AND%20%22idx_state%22%3D%27pending%27%20AND%20%22idx_added%22"))
        assertEquals("racwdlf", r.requestUrl!!.queryParameter("sp"))
    }

    @Test fun withoutFPermissionItListsWithTags() {
        client = BlobClient(sas("racwdl"), 0, 10)
        server.enqueue(MockResponse().setBody(findXml))
        assertEquals(2, client.findJobs("download").size)
        val r = server.takeRequest()
        assertEquals("list", r.requestUrl!!.queryParameter("comp"))
        assertEquals("tags", r.requestUrl!!.queryParameter("include"))
        assertEquals("index/jobs/download/", r.requestUrl!!.queryParameter("prefix"))
    }

    @Test fun aRefusedFindFallsBackToListing() {
        server.dispatcher = (object : okhttp3.mockwebserver.Dispatcher() { override fun dispatch(request: okhttp3.mockwebserver.RecordedRequest) =
            if (request.requestUrl!!.queryParameter("comp") == "blobs") MockResponse().setResponseCode(403).setBody("<Error><Code>AuthorizationPermissionMismatch</Code></Error>") else MockResponse().setBody(findXml) })
        assertEquals(2, client.findJobs("download").size)
        assertEquals("blobs", server.takeRequest().requestUrl!!.queryParameter("comp"))
        assertEquals("list", server.takeRequest().requestUrl!!.queryParameter("comp"))
    }

    @Test fun leaseFlow() {
        server.enqueue(MockResponse().setResponseCode(201).setHeader("x-ms-lease-id", "L1"))
        server.enqueue(MockResponse().setResponseCode(200))
        server.enqueue(MockResponse().setResponseCode(200))
        server.enqueue(MockResponse().setResponseCode(409).setBody("<Error><Code>LeaseAlreadyPresent</Code></Error>"))
        assertEquals("L1", client.acquireLease("index/jobs/download/a.json", 60))
        client.renewLease("index/jobs/download/a.json", "L1")
        client.releaseLease("index/jobs/download/a.json", "L1")
        assertNull(client.acquireLease("index/jobs/download/b.json", 60))
        val a = server.takeRequest(); val n = server.takeRequest(); val rel = server.takeRequest()
        assertEquals("PUT", a.method); assertEquals("lease", a.requestUrl!!.queryParameter("comp"))
        assertEquals("acquire", a.getHeader("x-ms-lease-action")); assertEquals("60", a.getHeader("x-ms-lease-duration"))
        assertEquals("/media/index/jobs/download/a.json", a.requestUrl!!.encodedPath)
        assertEquals("renew", n.getHeader("x-ms-lease-action")); assertEquals("L1", n.getHeader("x-ms-lease-id"))
        assertEquals("release", rel.getHeader("x-ms-lease-action")); assertEquals("L1", rel.getHeader("x-ms-lease-id"))
    }

    @Test fun aLostLeaseThrowsOnRenew() {
        server.enqueue(MockResponse().setResponseCode(409).setHeader("x-ms-error-code", "LeaseIdMismatchWithLeaseOperation"))
        try { client.renewLease("x.json", "L1"); fail() } catch (e: BlobException) { assertEquals(409, e.status) }
    }

    @Test fun putSendsTagsLeaseAndBlobType() {
        server.enqueue(MockResponse().setResponseCode(201))
        client.put("index/jobs/download/a.json", "{}".toByteArray(), "application/json", mapOf("idx_stage" to "download", "idx_state" to "done"), "L1")
        val r = server.takeRequest()
        assertEquals("BlockBlob", r.getHeader("x-ms-blob-type")); assertEquals("L1", r.getHeader("x-ms-lease-id"))
        assertEquals("idx_stage=download&idx_state=done", r.getHeader("x-ms-tags")); assertEquals("{}", r.body.readUtf8())
    }

    @Test fun putIfAbsentReportsAnExistingBlob() {
        server.enqueue(MockResponse().setResponseCode(409).setBody("<Error><Code>BlobAlreadyExists</Code></Error>"))
        server.enqueue(MockResponse().setResponseCode(412))
        server.enqueue(MockResponse().setResponseCode(201))
        assertFalse(client.put("p.json", ByteArray(1), "application/json", ifAbsent = true))
        assertFalse(client.put("p.json", ByteArray(1), "application/json", ifAbsent = true))
        assertTrue(client.put("p.json", ByteArray(1), "application/json", ifAbsent = true))
        assertEquals("*", server.takeRequest().getHeader("If-None-Match"))
    }

    @Test fun getIsConditionalOnTheEtag() {
        server.enqueue(MockResponse().setResponseCode(304))
        val r = client.get("index/control.json", "\"0x1\"")
        assertEquals(304, r.status)
        assertEquals("\"0x1\"", server.takeRequest().getHeader("If-None-Match"))
    }

    @Test fun setMetadataSendsMetaHeaders() {
        server.enqueue(MockResponse().setResponseCode(200))
        client.setMetadata("j.json", "L1", mapOf("node" to "n-1", "progress" to "42%"))
        val r = server.takeRequest()
        assertEquals("metadata", r.requestUrl!!.queryParameter("comp")); assertEquals("n-1", r.getHeader("x-ms-meta-node"))
        assertEquals("42%", r.getHeader("x-ms-meta-progress")); assertEquals("L1", r.getHeader("x-ms-lease-id"))
    }

    @Test fun blocksAreBase64OfAFixedWidthIndexAndTheBlockListIsXml() {
        assertEquals(8 * 1024 * 1024, BlobClient.BLOCK)
        assertEquals("MDAwMDAwMDA=", BlobClient.blockId(0)); assertEquals("MDAwMDAwMDI=", BlobClient.blockId(2))
        val f = File.createTempFile("blk", ".bin").apply { writeBytes(ByteArray(25) { it.toByte() }); deleteOnExit() }
        repeat(3) { server.enqueue(MockResponse().setResponseCode(201)) }
        server.enqueue(MockResponse().setResponseCode(201))
        val progress = ArrayList<Long>()
        client.putBlocks("index/videos/x/video.mp4", f, "video/mp4", mapOf("idx_kind" to "video")) { sent, _ -> progress.add(sent) }
        val sizes = ArrayList<Int>()
        for (i in 0 until 3) {
            val r = server.takeRequest(); sizes.add(r.bodySize.toInt())
            assertEquals("block", r.requestUrl!!.queryParameter("comp")); assertEquals(BlobClient.blockId(i), r.requestUrl!!.queryParameter("blockid"))
            assertTrue(r.path!!.contains("blockid=" + urlEnc(BlobClient.blockId(i))))
        }
        assertEquals(listOf(10, 10, 5), sizes); assertEquals(listOf(10L, 20L, 25L), progress)
        val l = server.takeRequest()
        assertEquals("blocklist", l.requestUrl!!.queryParameter("comp")); assertEquals("video/mp4", l.getHeader("x-ms-blob-content-type"))
        assertEquals("idx_kind=video", l.getHeader("x-ms-tags"))
        assertEquals("<?xml version=\"1.0\" encoding=\"utf-8\"?><BlockList>" + (0..2).joinToString("") { "<Latest>" + BlobClient.blockId(it) + "</Latest>" } + "</BlockList>", l.body.readUtf8())
    }

    @Test fun aBlockIsRetriedThreeTimes() {
        val f = File.createTempFile("blk", ".bin").apply { writeBytes(ByteArray(5)); deleteOnExit() }
        server.enqueue(MockResponse().setResponseCode(500)); server.enqueue(MockResponse().setResponseCode(503))
        server.enqueue(MockResponse().setResponseCode(201)); server.enqueue(MockResponse().setResponseCode(201))
        client.putBlocks("v.mp4", f, "video/mp4")
        assertEquals(4, server.requestCount)
        repeat(3) { server.enqueue(MockResponse().setResponseCode(500)) }
        try { client.putBlocks("w.mp4", f, "video/mp4"); fail() } catch (e: BlobException) { assertEquals(500, e.status) }
        assertEquals(7, server.requestCount)
    }

    @Test fun anUploadCanBeCancelledBetweenBlocks() {
        val f = File.createTempFile("blk", ".bin").apply { writeBytes(ByteArray(25)); deleteOnExit() }
        server.enqueue(MockResponse().setResponseCode(201))
        try { client.putBlocks("v.mp4", f, "video/mp4", emptyMap(), { server.requestCount >= 1 }); fail() } catch (e: BlobCancelled) {}
        assertEquals(1, server.requestCount)
    }

    @Test fun errorsNeverCarryTheSignature() {
        server.enqueue(MockResponse().setResponseCode(403))
        try { client.get("a.json"); fail() } catch (e: BlobException) { assertFalse(e.message!!.contains("SECRETSIG")) }
        assertFalse(sas().toString().contains("SECRETSIG"))
    }
}
