# HISAB KITAB Accountant WinForms Application Roadmap

## Recommendation

Build the accountant product as a **separate WinForms application**, not as additional sections inside the HISAB KITAB retail application. The retail client app should remain focused on store operations, while the accountant app gets its own navigation, permissions, client-workspace database model, installer, version, and automatic-update package.

The new application can reuse narrowly scoped shared libraries for themes, authentication, licensing verification, PDF generation, encryption, and approved accounting calculations. It must not directly reuse retail screens or expose retail modules simply because they exist in the same solution.

Do not add a generic **Pay Taxes** button that signs into government websites and submits money immediately after every payroll. Payroll tax deposits and returns have different federal and state schedules, authorizations, signatures, amendments, and confirmation requirements. A wrong or duplicate submission can create penalties even when the payroll calculation itself is correct.

Build the accountant product in stages:

1. Create the separate Accountant WinForms executable, installer, updater, licensing, and isolated client workspaces.
2. Payroll liability calculations, review, exports, and due-date tracking.
3. Integration with an approved payroll tax filing/payment provider.
4. Direct federal or state filing only after HISAB KITAB completes the required agency enrollment, testing, and approvals.
5. License compliance tracking first; automated renewal adapters only for agencies that provide a supported integration.

## Separate application and licensing

Issue a signed accountant-product license that cannot activate the retail executable by accident. Recommended product profiles:

- `HisabKitabRetail`: current retail application and retail services.
- `HisabKitabAccountant`: separate accountant application with client workspaces, accounting, payroll, tax center, documents, and compliance.
- A future bundle may license both products, but each application must still enforce its own product identifier.

The accountant license can include granular signed services such as:

- `GeneralLedger`
- `ClientAccounting`
- `Payroll`
- `PayrollTaxCenter`
- `TaxPreparation`
- `ComplianceLicenses`
- `FirmAdministration`

Navigation visibility is not sufficient security. Every protected command and data service must also verify the accountant-product identifier, licensed service, current firm, client workspace, and signed-in user's role.

The accountant app can retain a Ctrl+D developer configuration area, but that password should unlock technical setup only. It must not authorize tax payments or sign a client's return. Tax submission requires a separate accountant role, client authorization, and a final approval step.

Recommended solution structure:

- `ManagerPaperworkSystem.Accountant.WinForms`: separate executable and UI.
- `ManagerPaperworkSystem.Accountant.Core`: accountant domain rules and workflows.
- `ManagerPaperworkSystem.Accountant.Data`: firm and client-workspace persistence.
- Existing shared projects only for vetted infrastructure that is genuinely common.
- Separate installer, update ZIP, release channel, and application data directory.

## Accountant client workspace

Each accountant login should contain isolated client workspaces. Every workspace should store:

- Legal name, DBA, entity type, EIN (encrypted), state and local registrations.
- Locations and responsible contacts.
- Fiscal year and accounting method.
- Chart of accounts and opening balances.
- General journal, recurring entries, adjusting entries, and closing entries.
- Bank and credit-card accounts with reconciliation status.
- Accounts payable, vendor bills, payments, and 1099 tracking.
- Accounts receivable, invoices, receipts, and aging.
- Payroll, payroll liabilities, deposits, and returns.
- Fixed assets, basis, placed-in-service date, depreciation, and disposal.
- Sales-tax and excise-tax registrations, returns, payments, and confirmations.
- Trial balance, general ledger, balance sheet, profit and loss, cash flow, and comparative reports.
- Month-end close checklist, workpapers, review notes, attachments, and sign-off.
- Tax notices, correspondence, due dates, assignments, and audit trail.

This structure supports the IRS recordkeeping categories of gross receipts, purchases, expenses, assets, and employment taxes while keeping the source documents attached to the related ledger activity.

## Payroll tax center

### Phase 1 - safe calculations and review

Build:

- Federal and state liability ledgers by payroll run and tax period.
- Deposit schedule configuration for each employer.
- Forms 941, 940, W-2/W-3, Illinois IL-941, UI-3/40, and applicable local-tax worksheets.
- Due-date calendar with overdue warnings.
- EFTPS/MyTax-ready export files where an official import format exists.
- A two-person review option and immutable approval audit log.
- Reconciliation of calculated liabilities, submitted payments, confirmations, and bank withdrawals.

No money moves in this phase.

### Phase 2 - approved provider integration

Use a payroll-tax filing/payment provider that supplies:

- A supported API.
- Multi-client accountant authorization.
- Federal, state, and local coverage.
- Idempotency protection against duplicate payments.
- Filing and payment acknowledgements.
- Amendment and rejected-return workflows.
- A contractual security and compliance program.

This is the safest first production route. Illinois lists approved third-party withholding vendors, and federal employment tax filing software must complete IRS approval and assurance testing.

### Phase 3 - direct agency filing

Only consider direct filing after completing:

- Reporting Agent enrollment and a signed Form 8655 for every employer.
- IRS e-file provider/transmitter enrollment and suitability review.
- IRS Employment Tax Modernized e-File XML implementation and Assurance Testing.
- EFTPS Batch Provider enrollment for multi-client payments.
- Illinois FSET developer/transmitter/reporting-agent enrollment and testing.
- Annual tax-year schema and business-rule updates.

Required workflow:

1. Calculate liabilities from a finalized payroll.
2. Determine the employer's actual deposit schedule and due date.
3. Show the exact return/payment, period, agency, bank account, and amount.
4. Require accountant approval and, when configured, client-owner approval.
5. Submit once with a unique idempotency key.
6. Save the agency acknowledgement and confirmation number.
7. Reconcile the withdrawal and surface rejects or variances.

Credentials must be encrypted, access must use MFA where supported, and the system must maintain a written security program, audit logs, retention controls, backups, and incident response.

## Business and professional license center

Start with a **Compliance & License Center**, not unattended browser automation.

Track:

- Client, business, and location.
- Jurisdiction level: federal, state, county, city, or village.
- License type and business activity.
- License number, issuing agency, portal, issue date, expiration date, and renewal window.
- Fee and payment method.
- Responsible accountant and client approver.
- Required documents, insurance, bonds, training certificates, and local prerequisites.
- Renewal status: not open, preparing, awaiting client, ready, submitted, accepted, rejected, or expired.
- Submission date, confirmation number, renewed certificate, and audit history.

Provide:

- 120/90/60/30-day reminders.
- A client document request checklist.
- Pre-filled renewal worksheets or agency-supported upload files.
- A portal launch button to the correct client and jurisdiction.
- A final approval checklist.
- Confirmation capture and automatic next-expiration calculation.

Do not promise universal one-click renewal. Illinois tobacco renewals currently use MyTax Illinois, while state liquor licensing moved to a separate ILCC portal using ILogin. State liquor applications also depend on local liquor-license information. Municipalities use their own forms and may require different documents, attestations, fees, MFA, or manual review. Automation adapters should be added one agency at a time only where the agency permits it and offers a stable supported interface.

## Security baseline

Before selling an accountant edition:

- Encrypt SSNs, EINs, bank details, portal secrets, and tax documents.
- Use per-user accounts, least privilege, MFA, session timeout, and device revocation.
- Separate developer configuration access from accountant filing authority.
- Record who viewed, changed, exported, approved, filed, or paid.
- Add immutable submission and confirmation records.
- Add tested encrypted backups and recovery procedures.
- Add retention and secure-disposal policies.
- Prepare a Written Information Security Plan.
- Review FTC Safeguards Rule applicability and state privacy/breach requirements with counsel.

## Primary references

- IRS, 2026 Publication 15: https://www.irs.gov/publications/p15
- IRS, third-party payroll providers and reporting agents: https://www.irs.gov/government-entities/third-party-payer-arrangements-payroll-service-providers-and-reporting-agents
- IRS, EFTPS and Batch Provider Software: https://www.irs.gov/payments/eftps-the-electronic-federal-tax-payment-system
- IRS, Employment Tax Modernized e-File: https://www.irs.gov/e-file-providers/modernized-e-file-mef-for-employment-taxes
- IRS, Employment Tax MeF schemas and business rules: https://www.irs.gov/e-file-providers/schemas-and-business-rules-for-employment-tax-modernized-e-file-mef-forms
- IRS, becoming an authorized e-file provider: https://www.irs.gov/e-file-providers/become-an-authorized-e-file-provider
- IRS, taxpayer-data security resources: https://www.irs.gov/tax-professionals/guidance-and-resources
- FTC Safeguards Rule: https://www.ftc.gov/business-guidance/resources/ftc-safeguards-rule-what-your-business-needs-know
- IRS, business recordkeeping: https://www.irs.gov/businesses/small-businesses-self-employed/what-kind-of-records-should-i-keep
- Illinois electronic withholding/UI filing: https://tax.illinois.gov/businesses/webfilewithholding.html
- Illinois FSET enrollment overview: https://tax.illinois.gov/questionsandanswers/answer.757.html
- Illinois approved withholding vendors: https://tax.illinois.gov/businesses/approvedthirdpartywitsoftwarevendors.html
- Illinois tobacco license renewal: https://tax.illinois.gov/programs/mytax/help/mytax-illinois-functions.html
- Illinois Liquor Control Commission licensing portal: https://ilcc.illinois.gov/divisions/licensing.html
