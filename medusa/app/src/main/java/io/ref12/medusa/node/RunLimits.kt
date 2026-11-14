package io.ref12.medusa.node

import java.time.ZoneId

/** The local settings of the three run limits (null / blank = unset). */
class LocalLimits(val budget: Int?, val schedule: String?, val window: String?)

/** What the node obeys now: the remote override per field wins over the local value. */
class EffectiveLimits(
    val budget: Int?, val budgetKey: String?, val scheduleText: String?, val schedule: Schedule?, val windowText: String?,
)

enum class Gate { OPEN, BUDGET_SPENT, WAITING }

object RunLimits {
    /** [control]'s nodes.<nodeId> overrides the local values; the budget's key changes with the override's value or changedAt. */
    fun resolve(local: LocalLimits, control: Control, nodeId: String): EffectiveLimits {
        val o = control.nodes[nodeId]
        val budget = o?.budget ?: local.budget
        val key = when { budget == null -> null; o?.budget != null -> "remote:" + budget + ":" + (control.changedAt ?: ""); else -> "local:$budget" }
        val cron = o?.schedule?.takeIf { it.isNotBlank() } ?: local.schedule?.takeIf { it.isNotBlank() }
        val window = o?.window?.takeIf { it.isNotBlank() } ?: local.window?.takeIf { it.isNotBlank() }
        return EffectiveLimits(budget, key, cron, Schedule.of(cron, window), window)
    }

    /** The budget left, restarted at [EffectiveLimits.budget] whenever the key (value or changedAt) differs from the stored one. Null: no budget. */
    fun remaining(eff: EffectiveLimits, state: NodeState): Int? {
        val b = eff.budget ?: return null
        if (state.budgetKey() != eff.budgetKey) state.setBudget(eff.budgetKey, b)
        return state.budgetLeft()
    }

    fun gate(eff: EffectiveLimits, left: Int?, now: Long, zone: ZoneId): Gate = when {
        left != null && left <= 0 -> Gate.BUDGET_SPENT
        eff.schedule != null && !eff.schedule.isOpen(now, zone) -> Gate.WAITING
        else -> Gate.OPEN
    }

    fun parseBudget(s: String?): Int? = s?.trim()?.toIntOrNull()?.takeIf { it >= 0 }
}
