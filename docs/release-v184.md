# HISAB KITAB 1.0.184

## Changes

- Recognize explicit printed subtotal, discount, tax and delivery amounts on supported AK Wholesale, Skygate, DemandVape and 1 Oak PDF layouts. Validate the product subtotal separately; unexplained differences still require review. Use AK's invoice total instead of its outstanding balance. Do not treat 1 Oak as AK.
- Discover PDF MIME attachments even without a .pdf filename, including forwarded messages. Finish all Gmail result pages before advancing the checkpoint. After upgrade, recheck the recent 47-day email window once using existing invoice duplicate protection; older invoices remain available through Include month.
- Process independently verified invoices in a combined PDF without rejecting them solely because the file contains multiple invoices. Explain messages checked, PDFs found, imported, already imported and needs-review results.
- Rebuild missing price comparisons once per store from saved purchase lines and maintain chronological comparisons when older invoices arrive later. Preserve read state for unchanged comparisons and do not overwrite current product costs with historical prices.
- Add visible period filters to Product Costs and Price Alerts, using invoice dates. Historical Product Costs show a period snapshot; price metrics reflect actual changes.
- Arm the opening-cash reminder only when Month Close succeeds. Claim it atomically once per store/month and persist dismissal. Existing months are not armed during schema upgrade. Manual Set Carry Forward remains available.

## Validation

- WinForms Release build; the desktop application was not launched.
- 75 regression tests passed, including isolated SQL Server store isolation, transaction rollback, closed-month guards, schema upgrades and once-only reminder claims across sessions.
- End-to-end fixture import of four supported supplier PDFs plus a combined distributor PDF: six verified invoices imported, six unreconciled invoices retained for review; repeated import skipped every existing invoice, populated product costs and left the other store unchanged.
- Private sample PDFs and test databases are not included in the release.
- Offscreen checks verified required period choices, previous-month/year date bounds and non-overlapping controls in light and dark themes.

## Limits

Unrecognized invoice layouts, missing lines, conflicting printed amounts and OCR output still require review. A price alert needs at least two comparable purchases with different costs. No client mailbox was accessed for live verification during this change.
