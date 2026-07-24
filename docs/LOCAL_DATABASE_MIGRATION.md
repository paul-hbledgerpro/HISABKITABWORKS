# HISAB KITAB local database migration

Version 1.0.126 stops the three HISAB KITAB WinForms applications from
connecting to Azure SQL. They use the local SQL Server Express instance
`.\SQLEXPRESS` with Windows authentication.

The applications covered by this migration are:

- HISAB KITAB WORKS
- HISAB KITAB WORKS License Generator
- HISAB KITAB WORKS Client Account Manager

## Important data rule

Installing the update does not copy a deleted or restoring Azure database.
Each restored store database must be exported and imported into the local SQL
Server before that store's historical records are available locally.

The database name must remain unchanged. For example, an Azure database named
`HBStoreLedger_GALAXY ELGIN` must be imported locally with exactly that name.
Existing signed licenses retain this database identity while their Azure
server address and SQL password are ignored.

## Migration checklist

1. Allow Microsoft to finish restoring the Azure SQL databases.
2. Export a full BACPAC or other verified backup for every store and for the
   licensing database.
3. Install SQL Server 2022 Express with the named instance `SQLEXPRESS` on the
   computer that will hold the local data.
4. Import each backup into `.\SQLEXPRESS`, preserving its original database
   name.
5. Compare critical record counts and totals between the recovered source and
   local database before accepting the migration.
6. Install or update all three applications to version 1.0.126.
7. Open each store and verify its dashboard, cash drops, purchases, bank
   statements, reports, users, payroll, and licensing records.
8. Keep the recovery exports in two separate backup locations.

## Multiple computers

A database installed locally on one PC is not automatically shared with other
PCs. A multi-PC store needs either:

- one protected store computer hosting SQL Server for the other computers on
  the same local network; or
- a separately approved remote SQL Server-compatible provider.

The current 1.0.126 policy intentionally rejects cloud and remote SQL
endpoints to prevent accidental Azure charges. Do not configure a provider
such as Orventa until its database engine, encryption, backup retention,
connection limits, and recovery process have been verified.

## Security

Do not commit or share connection-setting files, database files, license
files, passwords, private signing keys, BACPAC exports, or `%LOCALAPPDATA%`
application data through GitHub.
