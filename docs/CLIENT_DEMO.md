# HISAB KITAB WORKS client demo

The client demo is a separate, safe presentation mode of the current WinForms
application. It does not use the normal client database, installed license,
saved email settings, POS portal configuration, bank connection, or cloud
backup schedule.

## What it demonstrates

- Two fictional stores and store switching
- Dashboard KPIs and recent activity
- 60 rolling days of Cash & Sales Summary data
- Two Z-report / Shift Cash Drop rows per day
- Cash On Hand additions, deposits, and payouts
- Vendor check payouts
- Vendors, purposes, purchase invoices, and line items
- Product costs and price alerts
- Reconciled demo bank activity
- Six employees per store
- Current and upcoming weekly schedules
- Two completed payroll periods per store
- Profit & Loss and PDF reports
- Owner/Admin and Manager user accounts

All names, addresses, email addresses, employees, documents, transactions and
amounts are fictional. External synchronization buttons explain that the real
feature is included but intentionally disconnected in the demo.

## Run from a developer build

```powershell
dotnet build src/ManagerPaperworkSystem.WinForms/ManagerPaperworkSystem.WinForms.csproj -c Release
& "src/ManagerPaperworkSystem.WinForms/bin/Release/net8.0-windows/win-x64/HISAB KITAB.exe" --demo
```

The isolated database is created under:

```text
%LOCALAPPDATA%\Hisab Kitab Demo\hisab_kitab_demo.db
```

The File menu contains **Reset Demo Data**. The installed Start menu also has a
**Reset Client Demo Data** shortcut.

## Build the standalone installer

```powershell
./installer/build_demo_installer.ps1 -Version 1.0.154
```

Output:

```text
installer/release/HISAB_KITAB_WORKS_Client_Demo_Setup_1.0.154.exe
```

The demo installer has a different application ID and installation directory,
so it can be installed beside the normal HISAB KITAB WORKS client application.
