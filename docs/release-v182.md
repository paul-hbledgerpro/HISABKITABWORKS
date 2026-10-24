# HISAB KITAB 1.0.182

## Changes

- Cash ledgers use content-sized columns with minimum widths and horizontal scrolling. Regular actions remain on one row when space permits and wrap visibly on narrower screens. Amount inputs select existing contents after Tab or mouse focus.
- The editable shift dropdown lists synced batches for the captured store/database. Shift display defaults to newest business date, then highest numeric batch. Newest/Oldest, batch search and numeric Batch-header sorting affect display only.
- Developer Z Auto Sync Setup occupies a separate reserved header slot. Its existing Ctrl+D/password authorization remains required; client Sync Health is read-only.
- Purchases adds a simple Invoice Inbox, Gmail sign-in, store invoice address, selective-forwarding guidance, historical month retrieval and Imported / Already Imported / Needs Review results. Uncertain invoices can be corrected in a review editor. The existing technical setup stays developer-only.
- Local OCR handles supported single-image English scan pages (up to 25 pages and 30 million pixels per image). OCR always requires review. Unsupported scans/native-runtime failures retain their PDF for manual review. No invoice is sent to an AI service.
- Store/database context is captured before invoice jobs await. Invoice, cost and price-alert changes commit atomically. Duplicate identities are checked against the full store history, including manual uploads. Conflicting date/total values go to review. Shared mailboxes connected to multiple stores are blocked to prevent cross-store posting.
- Product comparison distinguishes explicit packaging and stable UPCs; ambiguous descriptions remain separate. Costs are per invoice unit/package; no carton-to-single-item conversion is guessed. Older invoices do not overwrite a newer price baseline. Existing alerts include percentage changes and source invoices.
- Owner/Admin users can edit names/email/roles/permitted stores, reset passwords separately, deactivate/reactivate, and delete unused accounts only. Service checks protect the last active administrator, prohibit non-admin changes and preserve used identities. Account edits are audited; existing accounts retain their previous all-store access until explicitly restricted.
- Sync Health shows latest imported shift, pending drops, configured sync results and latest successful cloud backup. Seven verified local backups are retained per database alongside existing off-device backups. Failed cloud uploads no longer remove the only local verified copy.
- The existing once-per-update What's New notice includes these changes. No change to automatic report sync scheduling or cash accounting formulas.

## Verification

- WinForms Release build, zero warnings/errors.
- Data tests: 21 passed, including legacy permission-schema upgrade, account authorization, last-admin protection, history preservation and audit.
- POS/import tests: 58 passed with local SQL Express enabled. Covers same-number/different-store shifts, pending-drop application, retry/idempotency, closed periods, invoice duplicates/conflicts, captured database during store switch, transaction failure/rollback, packaging and historical prices.
- Sync tests: 68 passed.
- Independent SQL sessions tested exclusive same-store coordination and independent different-store locks.
- A native CHECKSUM backup was restored into a separate random test database; both stores' seeded records were verified. Test databases and backup were removed afterward.
- Offscreen WinForms checks cover all three layouts in light/dark, readable grid overflow, visible actions at 1200/850 widths, Tab/mouse selection, batch dropdown/order, retained drafts, OCR of a synthetic scanned invoice, per-store review history, shared-mailbox refusal and local backup retention. The production application was not launched.
- Separate ledger UI checks cover Enter-to-save, over/short results, theme restoration, release notice and PDF monthly balances.

## External validation limits

Google public OAuth approval is unconfirmed (owner is unsure). The build contains the existing private OAuth configuration through the release workflow; this is not evidence of Google verification. Google may restrict sign-in. The dedicated store inbox and manual invoice import remain alternatives. Live Gmail/portal accounts and client databases were not accessed. Coordination was tested through independent SQL sessions, not a live two-Windows-user pilot. Client-machine behavior still needs observation after deployment; these checks do not guarantee every external portal or scanned invoice format.

OCR model: tessdata_fast 4.1.0, commit 65727574dfcd264acbb0c3e07860e4e9e9b22185; SHA256 7D4322BD2A7749724879683FC3912CB542F19906C83BCC1A52132556427170B2. Apache-2.0 license/notice included.
