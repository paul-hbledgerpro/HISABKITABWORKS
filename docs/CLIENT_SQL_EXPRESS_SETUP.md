# HISAB KITAB client SQL Express setup

Each SQL Express installation is a separate database server. GitHub updates the
application files; it does not move or synchronize customer database data.

## Choose one database host per store

- One-PC store: that computer is both the HISAB KITAB workstation and database host.
- Multiple PCs in one store: choose one always-on computer as the database host.
  Every other PC must connect to that one host database.
- Different stores: use a separate store database for each store.
- Do not restore the same live store database independently onto multiple PCs.
  The copies will diverge and transactions will be missing from one another.

Every computer still needs its own device license even when multiple computers
share one store database.

## First-time host setup

1. Install SQL Server Express and SQL Server Management Studio.
2. Confirm the instance is named `SQLEXPRESS`.
3. Open PowerShell as Administrator in the HISAB KITAB source folder.
4. To restore a recovered BACPAC:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\client-database\Setup-HisabKitabStoreDatabase.ps1 `
  -Mode Host `
  -DatabaseName "HBStoreLedger_GALAXY ELGIN" `
  -BacpacPath "C:\Recovered Databases\HBStoreLedger_GALAXY ELGIN.bacpac"
```

5. To create a new empty database:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\client-database\Setup-HisabKitabStoreDatabase.ps1 `
  -Mode Host `
  -DatabaseName "HBStoreLedger_NEW STORE"
```

The script refuses to overwrite an existing database. It verifies the result and
writes a non-secret connection handoff file under:

`Documents\HISAB KITAB Client Setup`

## Additional workstation in the same store

SQL Express must first be configured on the host for TCP/IP access, a fixed TCP
port, Windows Firewall access, and an account usable by the workstation. Then run:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\client-database\Setup-HisabKitabStoreDatabase.ps1 `
  -Mode VerifyWorkstation `
  -SqlInstance "STORE-OFFICE-PC\SQLEXPRESS" `
  -DatabaseName "HBStoreLedger_GALAXY ELGIN"
```

Do not use `.\SQLEXPRESS` on an additional workstation: the dot means that
workstation's own computer, not the store's database host.

## Developer tools are different

The License Generator and Account Manager use the developer licensing database,
not a customer's operational store database. If developer tools run on home,
work, and Surface PCs, they must all connect to one authoritative licensing
database. Independent local copies will not stay synchronized.

For a no-cloud arrangement, designate one developer PC as the licensing database
host and reach it only through a trusted private network/VPN. Back it up after
every licensing change. Do not expose SQL Server directly to the public internet.

## Safety checklist

- Take a verified database backup before moving a store.
- Keep the old database read-only until row counts and recent transactions match.
- Test login, dashboard totals, the latest shift, purchases, and reports.
- Never place SQL passwords in GitHub, Google Drive, screenshots, or handoff JSON.
- Client app updates and database backups are separate processes.
