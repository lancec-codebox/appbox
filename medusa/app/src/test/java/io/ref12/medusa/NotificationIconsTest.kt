package io.ref12.medusa

import io.ref12.medusa.NotificationIcons.Kind
import org.junit.Assert.assertEquals
import org.junit.Test

class NotificationIconsTest {
    @Test fun idleServerUsesTheStaticIcon() = assertEquals(Kind.STATIC, NotificationIcons.forServer(0, false))
    @Test fun anActiveLibraryDownloadUsesTheDownloadIconAndThenSwitchesBack() {
        assertEquals(Kind.DOWNLOADING, NotificationIcons.forServer(1, false))
        assertEquals(Kind.DOWNLOADING, NotificationIcons.forServer(3, false))
        assertEquals(Kind.STATIC, NotificationIcons.forServer(0, false))
    }
    @Test fun ocrIsStaticEvenWithADownloadRunning() {
        assertEquals(Kind.STATIC, NotificationIcons.forServer(0, true))
        assertEquals(Kind.STATIC, NotificationIcons.forServer(2, true))
    }
    @Test fun theNodeIsAlwaysStatic() = assertEquals(Kind.STATIC, NotificationIcons.forNode())
}
