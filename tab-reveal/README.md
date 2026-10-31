# Reveal current tab

A tiny Chromium extension for Brave's vertical tabs: with many tabs open, the current tab can be scrolled out of sight in the tab strip. Press **Alt+Shift+R**, or click the toolbar button, and the strip scrolls back to it.

**How it works.** Extensions can't scroll the browser's own tab strip. The strip does scroll the selected tab into view whenever the selection changes, though. So the extension selects the nearest already-loaded tab for a moment (60 ms) and then selects the current tab again. You may see a brief flicker. A discarded (sleeping) tab is never chosen, so nothing gets reloaded.

## Install (unpacked)

1. Open `brave://extensions` and turn on **Developer mode** (top right).
2. Click **Load unpacked** and pick this `tab-reveal` folder.
3. Optional: pin the toolbar button, or change the shortcut at `brave://extensions/shortcuts`.

## Tuning

- If the strip doesn't follow, raise `HOP_MS` in `background.js` (try 150), then click the reload icon on the extension's card in `brave://extensions`.
- If the flicker bothers you, lower `HOP_MS`.

If Brave's strip doesn't scroll on selection changes at all, this approach can't work. The fallback is a side-panel tab list that the extension draws itself.
