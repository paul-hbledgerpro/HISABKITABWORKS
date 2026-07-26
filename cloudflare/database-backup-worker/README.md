# HISAB KITAB database backup Worker

This private Cloudflare Worker accepts multipart native SQL Server backups from
licensed HISAB KITAB PCs and stores them in a non-public R2 bucket.

Security properties:

- Every client request contains the existing signed PC license.
- Every request is signed by that PC's non-exportable Windows device key.
- New multi-business licenses restrict each upload to a database explicitly
  assigned to that Store GUID. Older single-store licenses are restricted to
  their signed Store GUID and can upload only; they cannot read cloud data.
- R2 has no public bucket URL and the client API has no download route.
- The Worker exposes no list or download route; developer access uses the
  authenticated Cloudflare dashboard or Wrangler.
- The Worker retains the newest 30 successful backups per store database.

Deployment:

1. Create the R2 bucket and KV namespace shown in `wrangler.jsonc`.
2. Replace the KV namespace ID.
3. Run `npm run check`, then `npm run deploy`.

Restore downloads remain developer-only and should use Wrangler:

```powershell
npx wrangler r2 object get `
  "hisab-kitab-sql-backups/<object-key>" `
  --file ".\restore.bak" `
  --remote
```
