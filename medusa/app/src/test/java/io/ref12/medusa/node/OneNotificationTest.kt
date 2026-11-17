package io.ref12.medusa.node

import org.junit.Assert.*
import org.junit.Test

class OneNotificationTest {
    @Test fun theTwoIdsDiffer_soTheStatusMustNeverBePostedDuringARun() = assertNotEquals(NodeWorker.NOTIF_ID, NodeWorker.STATUS_ID)
    @Test fun theStatusIsNotPostedWhileTheNodeRuns() {
        assertFalse(StatusPolicy.mayPost(running = true, force = false)) // network change, Run now, store worker, status refresh
        assertTrue(StatusPolicy.mayPost(running = false, force = false))
        assertTrue(StatusPolicy.mayPost(running = true, force = true))
    }
}
