# HISAB KITAB hybrid database migration

HISAB KITAB uses two deliberately separate database locations:

1. **Store operations are local and free.** Each customer/store database runs
   on SQL Server Express at `.\SQLEXPRESS` using Windows authentication.
2. **Developer licensing is shared.** The License Generator and Client Account
   Manager connect to one small SQL Server-compatible database named
   `HBLedgerPro_Licensing`. This lets the developer use Home, Work, and Surface
   PCs without copying licensing records between computers.

The shared licensing server is entered only in the two developer-only apps.
Its credentials are encrypted for the current Windows user with Windows DPAPI.
They are never included in a customer license file.

## Important recovery rule

Do not delete the restored Azure databases or recovery exports until every
local import has been verified. Installing an application update does not copy
historical records.

Keep each operational database name unchanged. For example,
`HBStoreLedger_GALAXY ELGIN` must be imported locally with that exact name.
Signed customer licenses keep this database identity but always connect to the
customer PC's local `.\SQLEXPRESS` instance.

## Store-database migration checklist

1. Let Microsoft finish restoring every Azure SQL database.
2. Export a verified BACPAC for every store and the licensing database.
3. Keep two independent copies of the exports.
4. Install SQL Server 2022 Express with instance name `SQLEXPRESS` on the PC
   that will hold the store data.
5. Run `scripts/database-migration/Import-RecoveredBacpacsToSqlExpress.ps1`
   without `-Import` first. Review the inventory and proposed database names.
6. Run the same command with `-Import` only after the inventory is correct.
   The script refuses to overwrite an existing local database.
7. Review its verification CSV and compare important table counts and business
   totals against the recovered source.
8. Open the store and verify dashboard, cash drops, purchases, bank statements,
   reports, users, payroll, and attachments.
9. Keep the Azure recovery source until the store owner confirms the data.

## Shared licensing setup

On each developer PC:

1. Open either the License Generator or Client Account Manager.
2. Enter the shared SQL Server-compatible host, SQL username, and password.
3. Click **Connect**.
4. A successful connection saves the profile encrypted for that Windows user.

The provider must support Microsoft SQL Server connections. PostgreSQL,
MySQL, and SQLite hosting cannot hold this existing licensing schema without a
separate conversion project.

If no shared profile has been saved, the developer tools fall back to
`.\SQLEXPRESS`. This is useful for offline testing but does not synchronize
developer PCs.

## Multiple customer computers

A local database is not automatically shared between PCs. A multi-PC store
should use one protected store computer as the SQL Express host and permit the
other licensed PCs on that store's private network to connect to it. Internet
exposure of SQL Express is not recommended.

## Backups

Run `scripts/database-backup/Backup-HisabKitabSqlExpress.ps1` from Windows Task
Scheduler each night. The script exports timestamped BACPAC files and SHA-256
checksums without deleting earlier backups. Copy the resulting folder to a
separate protected location or object-storage provider.

## Security

Never commit or share connection profiles, database files, license files,
passwords, private signing keys, BACPAC exports, or `%LOCALAPPDATA%` application
data through GitHub.
