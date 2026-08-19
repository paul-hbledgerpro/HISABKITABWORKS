# HISAB KITAB private SQL database backups

## What is deployed

HISAB KITAB uses Cloudflare R2 as private backup storage. It does not convert
the client databases to D1 or another database format. Each client database is
backed up as a native SQL Server `.bak` file with `CHECKSUM`, and SQL Server
runs `RESTORE VERIFYONLY ... WITH CHECKSUM` before the file is uploaded.

- Worker: `hisab-kitab-database-backups`
- Worker URL: `https://hisab-kitab-database-backups.hbcommercesolutions.workers.dev`
- Private R2 bucket: `hisab-kitab-sql-backups`
- Retention: newest 30 successful backups for each store database
- Schedule: 3:00 AM local time, with a catch-up attempt the next time HISAB
  KITAB opens if the scheduled run was missed
- Client-side staging: the temporary `.bak` is deleted after the upload attempt
- Client credentials: none are stored. Every request is authorized by the
  existing signed PC license and that PC's non-exportable Windows device key.

The Worker has no object-listing or download route. A client PC can upload only
for the store identity in its signed license. Only a developer signed in to the
Cloudflare account can see or retrieve backup objects.

Cloudflare encrypts every R2 object and its metadata at rest with AES-256 and
protects uploads and downloads in transit with TLS.

## Object layout

Objects are grouped for recovery without exposing a public index:

```text
<STORE-GUID>/<DATABASE>/<YEAR>/<MONTH>/<UTC-TIMESTAMP>_<ID>.bak
```

Object metadata records the store, database, licensed customer, license, PC
device, original size, creation time, and full-file SHA-256 digest.

## Developer recovery procedure

1. Sign in to the Cloudflare dashboard.
2. Open **R2 Object Storage**, then `hisab-kitab-sql-backups`.
3. Select the required Store GUID, database, date, and `.bak` object.
4. Download it in the dashboard, or copy its complete object key and run:

```powershell
Set-Location ".\cloudflare\database-backup-worker"
npx wrangler r2 object get `
  "hisab-kitab-sql-backups/<complete-object-key>" `
  --file "C:\HisabKitabRestore\store-restore.bak" `
  --remote
```

5. Run `RESTORE VERIFYONLY ... WITH CHECKSUM` on the downloaded file again
   before restoring it.
6. Restore into a new test database first. Never automatically overwrite a
   client's active database.

The client application writes local operational results to:

```text
%LOCALAPPDATA%\Hisab Kitab\Logs\database-cloud-backup.log
```

## Expected monthly cost

R2 Standard storage currently includes 10 GB-month, 1 million Class A
operations, 10 million Class B operations, and free Internet egress each
month. Storage above the allowance is `$0.015 per GB-month`. The Worker Free
plan allows 100,000 requests per day. At the current number of clients, backup
operations and Worker requests are expected to remain inside those free
allowances, so stored backup size is the meaningful cost.

Steady-state estimates with 30 daily backups retained:

| Databases | Average `.bak` | Stored | Approximate monthly R2 cost |
|---:|---:|---:|---:|
| 7 | 250 MB | 52.5 GB | $0.65 |
| 7 | 500 MB | 105 GB | $1.43 |
| 7 | 1 GB | 210 GB | $3.00 |
| 28 | 250 MB | 210 GB | $3.00 |
| 28 | 500 MB | 420 GB | $6.15 |
| 28 | 1 GB | 840 GB | $12.45 |

Formula:

```text
monthly storage = database count × 30 retained copies × average backup size
monthly R2 cost ≈ max(0, stored GB - 10 GB free) × $0.015
```

Cloudflare rounds billable usage to the next whole billing unit, so an invoice
can differ by a few cents. Actual `.bak` sizes should be checked after the first
week. Even the 28-database, 1-GB-per-backup example remains below the stated
`$15–$20` monthly budget.

## Why Cloudflare R2 instead of a DigitalOcean database

This requirement is disaster-recovery storage for existing local SQL Server
databases, not a live shared production database. R2 preserves proper native
SQL Server backup files with almost no client-side architecture change and no
always-running server. A DigitalOcean server would add operating-system,
database-engine, patching, firewall, and SQL Server licensing/administration
responsibilities and would cost more continuously.

If live central reporting is required later, it should be a separate,
read-only synchronization project. It should not replace these backups.
