# HISAB KITAB WORKS Client Account Manager

This is a developer-only WinForms application. Do not include it in the customer installer.

## Correct workflow

1. Open **Client Account Manager** and connect to `HBLedgerPro_Licensing`.
2. Create or select the client account.
3. Set paid PC seats, business slots, monthly charge, expiry date, and purchased services.
4. Save the account. Copy the generated `HBL-...` subscription key if needed.
5. Open the separate **License Generator**.
6. Paste the customer's protected PC request and generate the signed license.
7. The customer activates that license. Payroll and Scheduling appear only when those services were selected in the account and signed into the new PC license.

Changing services in Client Account Manager does not alter a license already installed at a customer. Generate and activate an updated license after every service change.

## Account payments and invoices

1. Connect to the licensing database and select a client account.
2. Open **Account Payments & Invoices**.
3. Enter the monthly price for every enabled service and save the prices.
4. Select the billing month, invoice date, and due date, then generate the monthly invoice.
5. Export the selected invoice as a one-page PDF.
6. Record partial or full payments against the invoice. The invoice status and balance update automatically.

Pricing, invoices, invoice line items, and payment history are stored only in the developer licensing database. Customer business databases are not modified.

Older accounts that were created before service-level pricing was added can be selected normally. Enter their Accounting, Payroll, and Scheduling monthly prices in **Account Payments & Invoices**, then click **Save Monthly Service Prices** before creating the first invoice.

## Release build

```powershell
dotnet build .\HisabKitabWorks.ClientAccountManager.WinForms.csproj -c Release
```
