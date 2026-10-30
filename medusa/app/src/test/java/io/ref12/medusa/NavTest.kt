package io.ref12.medusa

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

class NavTest {
    @Test fun library_is_home_and_back_on_it_exits() {
        val n = NavStack()
        assertEquals(Dest.LIBRARY, n.current.dest)
        assertTrue(n.isTopLevel)
        assertFalse(n.back())
    }
    @Test fun back_from_a_drawer_destination_returns_to_library() {
        val n = NavStack()
        n.goTop(Dest.SETTINGS)
        assertEquals(Dest.SETTINGS, n.highlighted)
        assertTrue(n.back())
        assertEquals(Dest.LIBRARY, n.current.dest)
        assertFalse(n.back())
    }
    @Test fun switching_destinations_does_not_pile_up() {
        val n = NavStack()
        n.goTop(Dest.CAPTURE); n.goTop(Dest.ABOUT); n.goTop(Dest.SETTINGS)
        assertEquals(2, n.depth)
        assertTrue(n.back()); assertEquals(Dest.LIBRARY, n.current.dest)
        n.goTop(Dest.CAPTURE); n.goTop(Dest.LIBRARY)
        assertEquals(1, n.depth)
    }
    @Test fun an_item_is_a_sub_screen_with_up_and_keeps_the_drawer_highlight() {
        val n = NavStack()
        n.open(Screen(Dest.ITEM, "m1"))
        assertFalse(n.isTopLevel)
        assertEquals(Dest.LIBRARY, n.highlighted)
        assertTrue(n.back()); assertEquals(Dest.LIBRARY, n.current.dest)
        n.goTop(Dest.CAPTURE); n.open(Screen(Dest.ITEM, "m2"))
        assertEquals(Dest.CAPTURE, n.highlighted)
        assertTrue(n.back()); assertEquals(Dest.CAPTURE, n.current.dest)
        assertTrue(n.back()); assertEquals(Dest.LIBRARY, n.current.dest)
    }
    @Test fun opening_the_same_screen_twice_is_one_step() {
        val n = NavStack()
        n.open(Screen(Dest.ITEM, "m1")); n.open(Screen(Dest.ITEM, "m1"))
        assertEquals(2, n.depth)
    }
    @Test fun the_stack_survives_a_save_and_restore() {
        val n = NavStack()
        n.goTop(Dest.CAPTURE); n.open(Screen(Dest.ITEM, "m9"))
        val r = NavStack.restore(n.save())
        assertEquals(Screen(Dest.ITEM, "m9"), r.current)
        assertEquals(Dest.CAPTURE, r.highlighted)
        assertEquals(Dest.LIBRARY, NavStack.restore(null).current.dest)
        assertEquals(Dest.LIBRARY, NavStack.restore(arrayOf("junk|x")).current.dest)
    }
}
