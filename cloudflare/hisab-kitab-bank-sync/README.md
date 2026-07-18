# HISAB KITAB secure bank-sync gateway

This Cloudflare Worker is the server-side boundary between licensed HISAB KITAB
desktop installations and Plaid. Plaid credentials and access tokens must never
be embedded in the WinForms application.

## Security model

- Every desktop request includes the developer-signed device license.
- Every request is signed by the PC's non-exportable Windows device key.
- Requests expire after five minutes and D1 rejects reused nonces.
- Plaid webhooks are verified using the `Plaid-Verification` JWT.
- Plaid access tokens are encrypted with AES-256-GCM before D1 storage.
- No bank username, password, Plaid secret, or clear access token is returned to
  the desktop application.

## Required Cloudflare secrets

Set these interactively with Wrangler. Never put their values in source control.

```powershell
npx.cmd wrangler secret put PLAID_CLIENT_ID
npx.cmd wrangler secret put PLAID_SECRET
npx.cmd wrangler secret put TOKEN_ENCRYPTION_KEY
```

`TOKEN_ENCRYPTION_KEY` must be a base64-encoded random 32-byte value.

## Plaid URLs

After deployment, add these URLs to the Plaid Dashboard, replacing the hostname
with the deployed Worker hostname:

- OAuth redirect: `https://HOST/plaid/oauth-return`
- Webhook receiver: `https://HOST/api/bank/webhooks/plaid`

The Worker requests the Transactions product in the United States and begins in
Plaid Sandbox.
