// Extensions cannot scroll the browser's own tab strip, but the strip scrolls the selected tab into view whenever the
// selection changes. So: select a neighbouring tab for a moment, then select the current tab again.

// How long the neighbour stays selected. Raise it if the strip doesn't follow; lower it if the flicker bothers you.
const HOP_MS = 60

async function reveal() {
  const [current] = await chrome.tabs.query({ active: true, currentWindow: true })
  if (!current) return
  const tabs = await chrome.tabs.query({ windowId: current.windowId })
  const neighbour = pickNeighbour(tabs, current)
  if (!neighbour) return
  await chrome.tabs.update(neighbour.id, { active: true })
  await new Promise(resolve => setTimeout(resolve, HOP_MS))
  await chrome.tabs.update(current.id, { active: true })
}

// The nearest tab that is already loaded, so the hop never wakes a discarded tab.
function pickNeighbour(tabs, current) {
  const others = tabs.filter(tab => tab.id !== current.id)
  const loaded = others.filter(tab => !tab.discarded && tab.status !== 'unloaded')
  const pool = loaded.length ? loaded : others
  return pool.sort((a, b) => Math.abs(a.index - current.index) - Math.abs(b.index - current.index))[0]
}

chrome.action.onClicked.addListener(() => { void reveal() })
chrome.commands.onCommand.addListener(command => { if (command === 'reveal-tab') void reveal() })
