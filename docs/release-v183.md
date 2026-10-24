# HISAB KITAB 1.0.183

## Changes

- Settings > Appearance offers nine choices: Light, Dark, Same as Windows, Classic Blue & Orange, Graphite & Orange, Midnight & Gold, Teal & Ivory, Slate & Blue, and Charcoal & Emerald. The existing stored `Follow Windows` value is retained and displayed as Same as Windows. The selected option is checked; the current Windows account's existing choice is preserved.
- Named palettes cover navigation, primary/secondary buttons, forms, fields, menus, grid headers, alternating rows, selection and login accents. Positive/negative meanings remain green/red. Theme application modifies grid colors without replacing currency formats or changing data, selections, entered amounts or permissions. Light restores the captured original colors. Windows-drawn native controls continue to use their system chrome.
- Shift Cash Drop search, sorting and Sync Health move into an aligned toolbar directly above the grid in all three layouts. Search has a magnifier and clear action (X or Esc). The filter menu offers newest/oldest business date, highest/lowest numeric batch and reset. Header sorting uses the same state and one rebind.
- The developer setup button occupies the toolbar's title area only after the existing Ctrl+D/password unlock. Unlocking does not move ordinary controls. Automatic synchronization and cash-accounting rules are unchanged.
- What's New describes these changes using the existing once-per-version acknowledgment.

## Verification

- Direct WinForms Release build: zero warnings/errors.
- Theme harness: exactly nine options, all preference save/reload paths, legacy Windows alias, missing/invalid fallback, menu callbacks/current checkmark, contrast >= 4.5 for named-palette text/action/header/selection/over-short pairs, both cash-entry layouts, repeated forward/reverse switching, retained drafts and currency format, over/short result dialogs, readable release notice, and original Light restoration.
- Toolbar harness: search/clear and sort callbacks, placement above the grid in all three layouts, 850/1200-width checks for every theme, and unchanged regular-control positions when developer access is unlocked.
- Existing ledger UI harness: Enter binding, over/short/balanced/pending states, Light/Dark restoration, release highlights and monthly cash PDF totals.
- Seven once-per-update acknowledgment tests passed.
- Synthetic forms rendered offscreen for visual review. The production desktop app, client databases and real client accounts were not opened. No live client-machine pilot was performed.

Release through the existing GitHub latest-release feed only after build assets, assembly/installer versions, source revision, package contents and download digests have been verified.
