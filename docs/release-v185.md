# HISAB KITAB 1.0.185

Month Close previously prevented the next startup because two startup migration paths executed an unconditional UPDATE on every ShiftLogs row. SQL Server fires update triggers even for unchanged values, so the closed-month guard rejected startup with error 51120. The generic startup failure dialog then incorrectly offered license reactivation and cleared saved connection settings when accepted.

The SQL Server schema and initializer paths now share a targeted legacy-NULL normalization command. It excludes closed periods and corrections referring to an original in a closed period, respects StoreId, and does not update already-normalized rows. SQLite uses the same targeted policy. Closed-month triggers are unchanged; no data is deleted, no month is reopened, and no license files are modified by the fix.

The startup error handler recognizes SQL error 51120 through wrapped/aggregate exceptions and preserves saved activation/connection settings instead of offering reactivation. Genuine existing connection-repair behavior is otherwise unchanged.

Validation: 77 tests passed, including SQL Server startup after Month Close, repeated startup with byte-for-byte record snapshots, legacy NULL cleanup limited to editable rows, correction guards, store isolation, and continued writes in the following month. The WinForms Release build completed with no errors or warnings. Testing used disposable fixture databases; the desktop application was not launched and no client database was changed.

Clients can install through the existing startup update check, which runs before database initialization. A direct client installer is also provided for installations unable to reach that check.
