# HISAB KITAB Invoice Inbox Worker

Private inbound invoice service for HISAB KITAB WORKS.

## Security model

- Every licensed store receives a unique email address under
  `invoices.hisabkitabworks.com`.
- Unknown or disabled store addresses are rejected.
- PDF attachments are stored in the private R2 bucket.
- Metadata and duplicate hashes are stored in D1.
- The WinForms app uses a store-specific API token. It never receives the
  Cloudflare admin secret.
- A PDF is only queued for review. This service does not automatically post
  financial entries.

## Commands

```powershell
npm.cmd install
npm.cmd run types
npm.cmd run check
npm.cmd run db:migrate:remote
npm.cmd run deploy
```

`INVOICE_ADMIN_SECRET` must be configured as a Worker secret.

## Provision a store

Send an authenticated `POST /admin/stores` request:

```json
{
  "displayName": "GALAXY ELGIN",
  "storeGuid": "IL_GALAXYELGIN_TBC_60123"
}
```

The response contains the store invoice address and a store API token. The
token is shown only when created or rotated.
