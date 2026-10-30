package io.ref12.medusa

/** The places of the main app shell. [top] ones are in the navigation drawer. */
enum class Dest(val title: String, val top: Boolean) {
    LIBRARY("Library", true), CAPTURE("Capture", true), SETTINGS("Settings", true), ABOUT("About & updates", true), ITEM("Item", false)
}

data class Screen(val dest: Dest, val arg: String? = null) {
    fun encode() = dest.name + "|" + (arg ?: "")
    companion object {
        fun decode(s: String): Screen? {
            val d = Dest.values().firstOrNull { it.name == s.substringBefore('|') } ?: return null
            return Screen(d, s.substringAfter('|', "").ifEmpty { null })
        }
    }
}

/**
 * Back rules of the shell: Library is the home and the bottom of the stack. A drawer destination replaces whatever
 * is above Library; an item (or any sub-screen) is pushed on top; Back pops one step; Back on Library exits.
 */
class NavStack(initial: List<Screen> = listOf(Screen(Dest.LIBRARY))) {
    private val stack = ArrayList(initial.ifEmpty { listOf(Screen(Dest.LIBRARY)) })

    val current: Screen get() = stack.last()
    val depth: Int get() = stack.size
    /** True for a screen opened by the drawer or the home (shows the menu button); false for a sub-screen (shows Up). */
    val isTopLevel: Boolean get() = current.dest.top
    /** The drawer entry to highlight: the drawer destination this screen belongs to. */
    val highlighted: Dest get() = stack.lastOrNull { it.dest.top }?.dest ?: Dest.LIBRARY

    fun goTop(d: Dest) {
        require(d.top)
        while (stack.size > 1) stack.removeAt(stack.size - 1)
        if (d != Dest.LIBRARY) stack.add(Screen(d))
    }

    fun open(s: Screen) {
        if (s == current) return
        stack.add(s)
    }

    /** Pops one screen; false when there is nothing left to go back to (the caller exits). */
    fun back(): Boolean {
        if (stack.size <= 1) return false
        stack.removeAt(stack.size - 1)
        return true
    }

    fun save(): Array<String> = stack.map { it.encode() }.toTypedArray()

    companion object {
        fun restore(a: Array<String>?): NavStack = NavStack(a?.mapNotNull { Screen.decode(it) } ?: emptyList())
    }
}
