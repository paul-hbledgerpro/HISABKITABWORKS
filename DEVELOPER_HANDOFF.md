# HISAB KITAB WORKS — DEVELOPER HANDOFF

Last updated: July 21, 2026

Current release: `1.0.123`

Git branch: `agent/payroll-scheduling-account-billing`

Latest completed feature commit: `97775b7` (`Fix price alert visibility and scaling`)

## Latest continuation checkpoint — 1.0.123

- Price Alerts now filters by the alert creation date rather than the source
  invoice date. This restores existing alerts when a user selects Current Month.
- The Galaxy Elgin database was checked read-only and contains 9 price alerts,
  408 product-cost records, 49 invoices, and 456 invoice lines.
- The Price Alerts filter area and grid were resized for Windows display scaling.
  Wrapped headers, larger header/row heights, proportional columns, and minimum
  widths prevent the added invoice/vendor columns from being clipped.
- The client, License Generator, and Account Manager Release builds completed
  with zero errors and zero warnings.
- Release packages for all three applications were rebuilt as version 1.0.123.
- Generated installers and updater ZIPs are not committed to Git; they are
  published in the GitHub release and copied to Google Drive under
  `Important Docs/HB LEDGER PRO/HISAB KITAB WORKS/Release 1.0.123`.

At the work PC, use the branch commands in section 2. The only intentionally
untracked local item at the home PC is `tmp/`; do not add it without reviewing
its contents.

This is the canonical continuation document for moving work between the home PC
and work PC. It replaces the obsolete WPF-era handoff that previously occupied
this file.

## 1. Repository and applications

Repository:

`https://github.com/paul-hbledgerpro/HISABKITABWORKS`

Active projects:

- Client application:
  `src/ManagerPaperworkSystem.WinForms/ManagerPaperworkSystem.WinForms.csproj`
- Reports:
  `src/ManagerPaperworkSystem.Reports/ManagerPaperworkSystem.Reports.csproj`
- Developer License Generator:
  `developer-only/HISAB-KITAB-LICENSE-GENERATOR/HisabKitabWorks.LicenseGenerator.WinForms.csproj`
- Developer Client Account Manager:
  `developer-only/HISAB-KITAB-CLIENT-ACCOUNT-MANAGER/HisabKitabWorks.ClientAccountManager.WinForms.csproj`
- Desktop updater:
  `src/ManagerPaperworkSystem.Updater/ManagerPaperworkSystem.Updater.csproj`
- Secure Plaid/report-email gateway:
  `cloudflare/hisab-kitab-bank-sync`
- Secure incoming-invoice service:
  `cloudflare/hisab-kitab-invoice-inbox`

The active customer application is WinForms on .NET 8 for Windows. The
`src/ManagerPaperworkSystem.UI` WPF project is the old implementation and must
not receive new work unless specifically requested.

## 2. Getting the correct code at the work PC

The active work is on a feature branch, not `main`.

For an existing clone:

```powershell
git fetch origin
git switch agent/payroll-scheduling-account-billing
git pull --ff-only origin agent/payroll-scheduling-account-billing
git status
```

For a fresh clone:

```powershell
cd C:\Dev
git clone https://github.com/paul-hbledgerpro/HISABKITABWORKS.git
cd HISABKITABWORKS
git switch agent/payroll-scheduling-account-billing
```

Do not copy `%LOCALAPPDATA%\Hisab Kitab` from one PC to another. That folder can
contain licenses, encrypted settings, cached tokens, databases, and
machine-specific state.

## 3. Release 1.0.120

GitHub release:

`https://github.com/paul-hbledgerpro/HISABKITABWORKS/releases/tag/v1.0.120`

The release contains installers and automatic-update ZIP packages for all three
applications:

- `HISAB_KITAB_WORKS_Client_Setup_1.0.120.exe`
- `HISAB_KITAB_WORKS_License_Generator_Setup_1.0.120.exe`
- `HISAB_KITAB_WORKS_Account_Manager_Setup_1.0.120.exe`
- `HISAB_KITAB_Update_win-x64_1.0.120.zip`
- `HISAB_KITAB_License_Generator_Update_win-x64_1.0.120.zip`
- `HISAB_KITAB_Account_Manager_Update_win-x64_1.0.120.zip`

Combined installer bundle:

- File: `HISAB_KITAB_WORKS_1.0.120_All_Installers.zip`
- Size: `375,469,638` bytes
- SHA-256:
  `7F47F52DF889D5B863663BECEE91C0779ACD2CC4E2F4E17FD898DEA4B7680B27`
- Google Drive:
  `https://drive.google.com/file/d/1NpO7ph0Z3EJ9uTM6y1EN70PFTjlpR0Sq/view`

The source repository intentionally does not track generated `bin`, `obj`,
publish, updater ZIP, or installer output files.

## 4. Current product structure

### 4.1 Customer WinForms application

The customer application now includes:

- Professional white/blue/orange/green visual theme.
- Dashboard, Cash Sales Summary, Shift Cash Drop, Cash On Hand, Check Payout,
  Operation Hub, vendors, purchases, bank statements, product costs, price
  alerts, payroll, scheduling, profit/loss, reports, stores, and users.
- Multi-store selection under a signed license.
- Store-level feature gating based on developer-issued license data.
- Visible application version in the main status area.
- Automatic-update checking and a separate update manager.
- SQL Server as the business data store.

The former Database Settings screen that exposed server, database, username,
password, and local file paths was treated as a security issue. Customer-facing
screens must never display connection credentials. SQLite references in old
diagnostic text were legacy fallback/diagnostic material and are not the
authoritative production business database when the licensed store is using SQL
Server.

### 4.2 Device-bound licensing

The current licensing flow is intentionally per computer:

1. The customer activation screen generates a stable protected PC identity.
2. The developer copies the Store GUID, PC ID, store name, and ZIP into the
   License Generator.
3. The License Generator checks the store subscription and PC-seat allowance.
4. It issues a signed license bound to that PC and the licensed business data.
5. A copied application folder or license file does not turn another computer
   into a licensed seat because the device identity will not match.

If an existing Store GUID is presented with a different PC ID, the developer
workflow is designed to decide whether to:

- add it as another paid PC seat, or
- replace/revoke the previous PC while retaining the allowed seat count.

Store GUID format follows:

`STATE_STORENAME_BUSINESSTYPE_ZIP`

Example:

`IL_GALAXYELGIN_TBC_60123`

The private signing key must never be committed. Use the License Generator's
Backup Key and Restore/Replace Key functions when moving the developer tool to
another trusted developer computer.

### 4.3 Client Account Manager

The separate developer-only Account Manager supports:

- Creating and editing client accounts.
- Assigning the primary Store GUID and SQL business database.
- Paid PC seats and licensed business/store slots.
- Subscription expiry and active/inactive status.
- Developer-controlled services:
  - Core Accounting
  - Payroll
  - Scheduling
  - Monthly Reports
- Developer-assigned payroll processing state.
- Monthly report email and delivery day.
- Per-service pricing, monthly totals, account payments, and PDF invoices.
- Opening the License Generator.
- Provisioning a private invoice inbox for a licensed store.

Current standard monthly price constants in code are:

- Accounting: see `StandardServicePricing.Accounting`
- Payroll: `$19.99`
- Scheduling: `$12.99`
- Monthly Reports: `$9.99`

The one-time license fee discussed with the product owner is `$200`. Active
employee count is not charged as a separate fee. Per-account prices remain
editable for older accounts and negotiated clients.

### 4.4 Payroll and scheduling

Implemented foundations include:

- Employee directory and onboarding information.
- Personal data handling, employee documents, W-4/ID attachment areas.
- Hourly/annual pay type, pay frequency, pay rate, state, and overtime.
- Payroll-period employee loading and manually editable hours.
- Regular, overtime, holiday, bonus, advance/deduction, tax, gross, and net
  fields.
- Draft, preview, approval/finalization, check and pay-stub PDF workflow.
- Payroll history and payroll reporting.
- Payroll expense inclusion in profit/loss.
- Scheduling entry, employee/date/start/end selection, publishing, and schedule
  PDF output.
- Unscheduled days rendered as OFF in schedule output.
- Scheduling hours feeding payroll while retaining final admin control.

Important compliance warning: the application has a signed tax-rule package
architecture and developer-assigned payroll state, but it must not be marketed
as automatically compliant for all 50 states until each state's current rules,
effective dates, calculation tests, and update source have been independently
verified. Payroll should block or warn when the selected state's rule package is
not verified for the applicable year.

### 4.5 Reports

Reports were returned to the professional QuestPDF path rather than relying on
the GrapeCity ActiveReports trial. Relevant files include:

- `src/ManagerPaperworkSystem.Reports/Pdf/SelectedOptionReportPdf.cs`
- `src/ManagerPaperworkSystem.WinForms/ReportViewerForm.cs`
- report routing in `src/ManagerPaperworkSystem.WinForms/MainForm.cs`

The report viewer generates the actual PDF first and then lets the user:

- view/preview,
- save as PDF,
- print, or
- email the PDF.

Report sending is routed through the secure server-side endpoint so the Resend
API key is not embedded in the client application. The intended sender is:

`donotreply@hisabkitabworks.com`

Monthly report delivery is a developer-enabled licensed service, with recipient
and delivery day assigned in Account Manager and included in the signed
license.

### 4.6 Bank integration

Live bank connectivity uses Plaid through
`cloudflare/hisab-kitab-bank-sync`. Plaid secrets and access tokens must never
be shipped in the WinForms application.

The gateway includes:

- device-license authentication,
- per-request device signatures,
- expiring requests and nonce replay protection,
- encrypted Plaid access-token storage,
- link-token and connection flow,
- transaction sync,
- webhook endpoint,
- server-side PDF report email through Resend.

Manual bank-statement upload remains available. Multiple stores are expected to
have separate store-scoped connections.

### 4.7 POS web portal automation

The client app contains Google Chrome automation for AdventPOS/POSWebOffice.

Two distinct accounting feeds must remain separate:

1. Cash Sales Summary section:
   - pulls the Cash and Sales Summary report,
   - uses the report business date,
   - prevents a second record for the same report/date,
   - allows the manager to enter cash drop and register payout later.
2. Shift Cash Drop section:
   - pulls individual Z Reports by batch,
   - normally expects two batches per day for a two-register store,
   - creates one shift record per unique batch,
   - lets the manager double-click later to enter the actual drop/payout.

Portal setup stores the site selection and credentials in protected local
settings. The scheduled sync is intended to run after the previous business day
has closed. Report archives are designed to use:

`Documents\<Store Name> Reports\Cash Sales Summary`

and:

`Documents\<Store Name> Reports\Z Reports`

with OneDrive Documents preferred when available and local Documents as the
fallback.

### 4.8 Purchase invoices, product costs, and price alerts

Purchase import includes vendor-specific PDF parsing and review-first behavior.
Reference vendor PDFs supplied during development included American
Distributors, AK Wholesale, Skynet/Skygate, DemandVape, and other vendors.

The safety rule is important: parsed invoices are queued for review. Financial
entries should not be silently posted when the parser is uncertain.

The Purchases grid now includes vendor and PDF attachment information. Invoice
line lengths and schema migrations were widened/updated after truncation and
missing-column errors were observed.

The prior Gmail methods were:

- IMAP with a 16-character Google App Password, or
- Gmail OAuth read-only access.

Those methods are difficult to manage across many unrelated client accounts, so
the new preferred architecture is the private Cloudflare invoice inbox
described next.

## 5. Cloudflare private invoice inbox

Release 1.0.120 introduced:

`cloudflare/hisab-kitab-invoice-inbox`

Security model:

- Each licensed store receives a unique address under
  `invoices.hisabkitabworks.com`.
- Unknown or disabled addresses are rejected.
- PDF attachments are stored privately in R2.
- Metadata and duplicate hashes are stored in D1.
- The WinForms client receives only a store-specific API token.
- The Cloudflare admin secret remains server-side/developer-only.
- Incoming PDFs are queued for review and are not automatically posted as final
  financial transactions.

Cloudflare setup completed during the project:

- `hisabkitabworks.com` activated in Cloudflare.
- `invoices.hisabkitabworks.com` enabled for Email Routing.
- Worker `hisab-kitab-invoice-inbox` deployed.
- D1 and R2 bindings created.
- Catch-all Email Routing rule sends inbound invoice mail to the Worker.
- Root Email Routing DNS records were added/unlocked.

The Worker requires the secret:

`INVOICE_ADMIN_SECRET`

Do not save this value in source control.

Developer provisioning:

1. Open Client Account Manager.
2. Select the licensed client/store.
3. Open Invoice Inbox provisioning.
4. Create or rotate the store inbox token.
5. Reissue/renew the store's signed PC license so the inbox URL/address/token is
   included.
6. In the client Purchases section, open Email Invoices and test the protected
   cloud inbox.
7. Send a real vendor PDF invoice to the generated store address and verify it
   is downloaded once and queued for review.

Worker source and setup notes:

- `cloudflare/hisab-kitab-invoice-inbox/README.md`
- `cloudflare/hisab-kitab-invoice-inbox/wrangler.jsonc`
- `cloudflare/hisab-kitab-invoice-inbox/migrations/0001_initial.sql`

## 6. Items that still require real-world validation or completion

Priority order:

### Priority 1 — Cloud invoice inbox end-to-end test

- Confirm a real message to the generated store address reaches the Worker.
- Confirm the Worker stores the PDF in R2 and metadata in D1.
- Confirm the licensed client authenticates with the store token.
- Confirm Sync New Invoices downloads only unseen attachments.
- Confirm duplicate messages/files do not create duplicate purchase records.
- Confirm a disabled/unknown store address and invalid token are rejected.

### Priority 2 — POS Z-report reliability

- Validate two different batches are pulled for each two-register business day.
- Backfill missing historical batches, especially the July 17 and July 18 cases
  discussed during testing.
- Validate the batch dropdown and report page after real portal layout changes.
- Confirm unique-batch duplicate prevention.
- Confirm saved archive files/screenshots are named by batch and business date.
- Confirm cash drops entered later reconcile to Cash Sales Summary without a
  second manual entry.

### Priority 3 — Invoice parsing accuracy

- Re-test every supplied vendor sample after cloud download.
- Validate vendor, invoice date/number, tax, total, product description,
  quantity, pack/ship quantity, and unit cost.
- Add confidence flags and mandatory review when extracted totals do not
  reconcile.
- Confirm long product descriptions no longer truncate.
- Confirm price changes create alerts only after an approved invoice.

### Priority 4 — Updater compatibility

- Test all three automatic updaters from genuinely older installed versions,
  not only from a development build.
- Confirm Update Now closes the owning app before replacing locked DLLs.
- Confirm a failed or interrupted update rolls back or remains recoverable.
- Confirm the client, License Generator, and Account Manager each use their own
  correct update manifest/package.

### Priority 5 — Accounting and reports

- Revalidate Bank Statement checkboxes and Check Payout clearing checkboxes.
- Confirm bank transaction classification does not double-count sales or
  expenses already entered elsewhere.
- Confirm Profit/Loss includes approved payroll and classified bank data once.
- Visually inspect every report type, multi-page tables, headers, row density,
  print, save, and email behavior.
- Confirm report email uses the client/profile email where required.
- Confirm month-end bank/report automation only runs for opted-in clients.

### Priority 6 — Payroll tax packages

- Establish an authoritative, maintainable federal and state source.
- Implement and test current federal withholding and every supported state.
- Add effective-date/version tests and signed package publishing.
- Have calculations reviewed before selling payroll processing in a new state.

## 7. Security and privacy rules

Never commit or upload to the repository:

- SQL Server usernames or passwords.
- Connection strings containing credentials.
- Plaid client secret, access tokens, or encryption key.
- Resend API keys.
- Cloudflare admin secrets or store API tokens.
- Gmail OAuth secrets, refresh tokens, or App Passwords.
- License signing private keys.
- Customer license files or activation requests.
- SSNs, W-4s, IDs, bank data, invoices, or customer databases.
- Anything under `%LOCALAPPDATA%\Hisab Kitab`.

Use Cloudflare Worker secrets for server credentials. Use Windows-protected
storage for developer/client local secrets. Customer screens must never display
raw connection configuration or secrets.

Database migrations for active clients should be additive and backward
compatible so the existing WPF installations continue to operate until they are
intentionally upgraded.

## 8. Building and packaging

Client build:

```powershell
dotnet build src\ManagerPaperworkSystem.WinForms\ManagerPaperworkSystem.WinForms.csproj -c Release
```

License Generator build:

```powershell
dotnet build developer-only\HISAB-KITAB-LICENSE-GENERATOR\HisabKitabWorks.LicenseGenerator.WinForms.csproj -c Release
```

Account Manager build:

```powershell
dotnet build developer-only\HISAB-KITAB-CLIENT-ACCOUNT-MANAGER\HisabKitabWorks.ClientAccountManager.WinForms.csproj -c Release
```

Build all three installers/update packages:

```powershell
.\installer\build_three_installers.ps1 -Version 1.0.120
```

For a new release, change the version argument and verify the generated Inno
Setup files and update metadata before publishing. Do not commit generated
installer, publish, `bin`, or `obj` folders.

Cloudflare Workers:

```powershell
cd cloudflare\hisab-kitab-bank-sync
npm.cmd install
npm.cmd run check

cd ..\hisab-kitab-invoice-inbox
npm.cmd install
npm.cmd run check
```

Deploy only after verifying the correct Cloudflare account, D1 database, R2
bucket, bindings, and secrets.

## 9. Setting up the Microsoft Surface Pro 11

The developer requested three-PC capacity. That does not mean one reusable
license key for three computers.

- The License Generator and Client Account Manager are developer-only tools and
  do not consume a customer's client-app seat.
- The test copy of the customer app on the Surface is a separate device and
  needs its own PC-bound license.
- A client account may allow three paid PC seats, but each PC receives a
  separately signed device license.

Surface procedure:

1. Install the 1.0.120 developer tools from the combined installer bundle.
2. On the current trusted developer PC, use Backup Key in License Generator.
3. Transfer that encrypted backup securely and use Restore/Replace Key on the
   Surface.
4. Configure the licensing SQL connection in each developer tool without
   committing credentials.
5. Install the customer app on the Surface.
6. Let its activation screen generate the Surface PC ID/request.
7. In License Generator, select/create the developer test subscription with
   enough PC seats and issue a license specifically for the Surface request.
8. Import/paste that license on the Surface and verify the PC registration in
   the License Generator.

The Surface cannot be pre-licensed before its protected PC identity is
generated.

## 10. Suggested continuation prompt

Use this at the work PC:

> Continue the HISAB KITAB WORKS WinForms project from `DEVELOPER_HANDOFF.md`.
> Work on branch `agent/payroll-scheduling-account-billing` and first verify the
> 1.0.120 Cloudflare invoice inbox end to end with a real store email/PDF. Then
> fix any observed sync issue without exposing secrets or automatically posting
> unreviewed financial entries. After that, validate two Z-report batches per
> day and historical backfill. Preserve existing WPF clients and use additive
> SQL migrations.
