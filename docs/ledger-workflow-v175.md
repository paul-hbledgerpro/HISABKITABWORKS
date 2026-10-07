# HISAB KITAB 1.0.175 — shift cash and monthly workflow

## Using the update

Close HISAB KITAB in both Windows accounts. Install the test installer in every Windows account that runs the application before using the shared database. Existing automatic sync settings remain enabled. This installer has not been published as the global automatic update.

- Shift Cash Drop → **Record Shift Drop**: enter batch, counted drop, optional register payout and its reason. Press Enter or OK. The selected store is fixed for this entry.
- An existing source-verified Z report is updated immediately. The result shows OVER in green, SHORT in red, or BALANCED. Variance is drop + register payout − POS cash.
- A missing batch is saved as a pending entry. A hidden sync process requests that exact batch from the configured store. Closing the entry window does not cancel it. If the portal is unavailable, the entry is retained for automatic retry. Pending Shift Drops shows its status and supports cancelling incorrect entries.
- A report must pass the existing store verification before the pending cash is applied. Retries do not add the money again. Corrections and repeated batch numbers require review through the existing dated shift view.
- Payouts and reasons roll up to the same store's daily Cash & Sales Summary, including when the summary arrives later. A separately entered summary payout is retained and marked for review. An owner can use **Use Shift Payouts** after reviewing it, then record the affected shift again. Register payouts are already taken out of the counted cash drop and are not deducted a second time from Cash On Hand.
- Settings → **Appearance**: Light, Dark, or Follow Windows. The preference is saved for this Windows account. Print/PDF styling stays unchanged.
- Settings → **Month Close / History**: an owner can close a completed month or reopen it for corrections/late imports. Cash ledger records stay in the database. Shift Cash Drop, Cash On Hand, Check Payout, and Cash & Sales Summary use a Period selector with Current Month, Previous Month, and Custom. Closed cash ledger months reject changes and late imports until reopened.
- Cash On Hand prompts once for this store's monthly opening cash. **Set Carry Forward** remains available if the prompt was dismissed. Opening cash replaces prior accumulated cash; current month drops and cash-on-hand payouts then change that balance. Corrections use the existing correction workflow.

## Verification

- WinForms .NET 8 Release build: zero warnings/errors.
- 130 automated tests passed: 55 sync/recovery/isolation, 47 POS import/history/ledger (including an actual SQL Server fixture), 8 cash-entry, 20 portal-settings/results.
- SQL fixture exercised an upgrade from tables absent, repeated schema initialization, verified batch/store matching, retained missing batches, exact-once application, same-date payout totals including corrections, conflict rollback, opening cash uniqueness, closed-month INSERT/UPDATE/DELETE protection (including corrections moved to another date), other-store access, zero-value duplicate protection, completion notifications after earlier waiting messages, and reopening. Only its random test database was created and removed.
- An isolated UI harness rendered the actual entry and result forms offscreen for Light/Dark and OVER/SHORT/BALANCED/pending states; Enter binding and restoring Light were checked. The desktop application was not launched.
- A generated PDF was parsed to confirm a $500 monthly opening plus $90 drop less $20 payout yields $570, without adding the previous $900 opening.
- Live authenticated portal retrieval and end-to-end use on the client PC remain to be confirmed. No production database, client sync settings, or public release was changed during verification.

## Developer checks

Build: `dotnet build src/ManagerPaperworkSystem.WinForms/ManagerPaperworkSystem.WinForms.csproj -c Release`.

SQL integration: set `HK_TEST_SQL_SERVER` to a development SQL Server instance (for example `.\SQLEXPRESS`) before running the POS import tests. With this variable absent, the SQL integration test is explicitly skipped. The test creates and drops only a database named `HK_Ledger_Test_` followed by a random GUID.

UI/PDF check: `dotnet run --project tests/ManagerPaperworkSystem.LedgerUi.Check -c Release -- <output-directory>`. It renders only isolated test dialogs offscreen and never launches the application or changes the user's appearance preference.
