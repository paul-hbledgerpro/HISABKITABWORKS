# HISAB KITAB WORKS 1.0.181

Shift Cash Drop and Cash On Hand now default to Side Entry Grid, preserving the live transaction grids and existing actions. View offers Classic Grid, Side Entry Grid and Guided Grid separately for each section. Layout preferences are saved per Windows profile, application user, database and section; changing layout preserves the current draft.

Paid To / Vendor and Purpose / Reason accept typed values and saved selections. Successful saves remember names in the existing store-scoped Vendors and Purposes tables within the financial transaction. Pending shift payouts retain their payee until the matching verified batch is available. New payee columns are added idempotently during database initialization. Existing drop accounting, payout rollups, corrections, closed-month checks and store verification remain in use.

Auto Sync Setup screens and their developer-only unlock remain unchanged. The View menu only selects layouts. The startup updater keeps its existing update prompt and deferral behavior. Embedded release highlights describe this release using the existing once-per-version notification.

Validation: WinForms Release build passed with zero warnings/errors. Data tests: 12 passed. Import/workflow tests: 49 passed including an isolated SQL Server database upgrade, repeated initialization, saved payee propagation, store isolation and accounting checks. Sync regressions: 68 passed. Layout controls passed default/preference, editable-choice, draft preservation, guided confirmation and light/dark rendering checks. No live client application, portal or database was used for verification.
