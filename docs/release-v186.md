# HISAB KITAB 1.0.186

After saving a shift drop or cash payout, grid refresh could leave keyboard focus outside the entry panel. The panel now returns focus to Shift / Batch Number for drops, or Payout Amount for cash payouts, after a successful save. Previous entry values clear and the payout date is retained. Guided layout focuses the confirmation button during review.

Existing financial posting, store matching, pending drops, variance results, and duplicate-submit guards remain unchanged. Failed saves preserve the entered values.

Validation: offscreen control checks cover both sections in all three layouts, consecutive keyboard saves, next-field focus, selected payout amount, cleared drafts, guided confirmation, delayed-save duplicate prevention, and failed-save preservation. WinForms Release build succeeded. No client database was accessed and the desktop application was not launched.

Shortage email alerts are being evaluated separately and are not included in this release.
