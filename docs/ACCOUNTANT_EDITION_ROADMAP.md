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

## Recommended firm, employee, and client architecture

Use a **central firm directory plus a separate database for every accounting client**.

The accounting firm is the top-level tenant. Each employee receives one personal login inside that firm. After login, the employee sees a client selector containing only the clients the firm administrator assigned to that employee. Selecting a client opens that client's isolated workspace and database.

Example:

- Firm: `Patel Accounting Services`
- Users: owner, administrators, accountants, bookkeepers, payroll staff, and auditors
- Clients: `Client A`, `Client B`, `Client C`, and all other businesses served by the firm
- Access: each user can be assigned to all clients or only selected clients
- Data: every client has its own accounting database and document-storage namespace

This allows one login per employee without giving every employee access to every client. All authorized employees work against the same live client database, so saved changes become available to the other authorized users after refresh or real-time notification.

### Central firm directory

The central directory stores identity and access information, not client accounting transactions:

- `Firms`: accounting firm identity, subscription, status, and security policy.
- `Users`: one personal identity per employee; never shared usernames.
- `FirmMemberships`: user membership, firm role, status, and invitation history.
- `Clients`: client name, status, workspace identifier, and database routing identifier.
- `ClientAssignments`: which users can access which clients and with which role.
- `RolePermissions`: allowed modules and actions.
- `UserSessions`: active devices, expiration, revocation, and MFA state.
- `AuditEvents`: firm, user, client, action, device, timestamp, and outcome.

The desktop application must never receive a list of database passwords or decide which database to open from a typed database name. It sends the selected workspace identifier to the server. The server verifies the user's firm membership and client assignment, then performs the operation in the authorized client workspace.

### Separate database per client

Each client database contains only that client's:

- Chart of accounts and general ledger.
- Bank, credit-card, reconciliation, AP, and AR records.
- Payroll, payroll liabilities, returns, and confirmations.
- Tax, license, compliance, document, and workpaper records.
- Client-specific users, contacts, approvals, and audit history where applicable.

The recommended database name should use an internal random workspace identifier, not the client's legal name, EIN, or another sensitive value.

Benefits:

- A missing application filter cannot accidentally return a different client's ledger rows.
- One client can be backed up or restored without overwriting another client.
- A damaged or unusually large client database does not require restoring the entire firm.
- Database access, storage, retention, and exports can be audited per client.
- A client can be disconnected or transferred with a clearly defined data boundary.

The tradeoff is that database creation and schema migrations must run across every active client database. The developer administration service should track a schema version for each workspace and automatically apply tested migrations in controlled batches.

### Employee roles and client assignments

Recommended firm roles:

- `FirmOwner`: billing, firm policy, administrators, every client, and final authority.
- `FirmAdministrator`: users, client setup, assignments, and security settings.
- `PartnerOrManager`: assigned clients, review, approval, and work assignment.
- `Accountant`: accounting work for assigned clients.
- `Bookkeeper`: daily bookkeeping for assigned clients, without tax-payment authority.
- `PayrollSpecialist`: payroll and payroll-tax preparation for assigned clients.
- `ReadOnlyAuditor`: view and export assigned clients without posting changes.

Firm role and client assignment are both required. For example, being an `Accountant` in the firm does not grant access to every client; the user must also have an active assignment to the selected client.

The firm administrator should be able to:

- Invite, deactivate, suspend, and revoke employee sessions.
- Require password reset or MFA.
- Assign one user to one, several, or all clients.
- Give the same employee different permissions for different clients.
- Set a default client and personal client display order without changing other users' order.
- Review who accessed or changed each client's data.

Ctrl+D developer access must never replace a firm login, client assignment, or accountant approval. It is only for technical configuration and support.

### Login and client-selection workflow

1. The employee signs in once with their personal firm account.
2. The server authenticates the employee and identifies the firm.
3. The server returns only active clients assigned to that employee.
4. The employee selects a client from **My Clients**.
5. The server authorizes the selected workspace and opens that client's data context.
6. The application shows the client name, legal entity, and workspace color prominently in the title/header.
7. Every read, edit, report, export, approval, and submission includes the authenticated firm, user, and selected workspace.
8. Switching clients closes the current data context, clears client-specific screens and caches, then opens a newly authorized context.

For the first version, allow only one active client workspace per application window. This reduces the chance that an employee posts an entry while looking at another client's tab. A second client can be opened in a separate, clearly labeled window later if users need side-by-side work.

### Keeping every employee current

The authoritative client databases must be live on a central server or behind a central API. A separate local database on each employee PC would create conflicting copies and would not reliably show coworkers' changes.

Use:

- Server-side transactions for multi-row accounting changes.
- Optimistic concurrency using row-version values so one employee cannot silently overwrite another employee's edit.
- Automatic screen refresh or change notifications after another user posts work.
- Record locks only for sensitive finalization steps such as closing a period or finalizing payroll.
- An immutable audit trail showing who created, changed, approved, reversed, exported, or submitted each record.

An encrypted local cache can improve startup and temporary read access, but it should not become a second authoritative accounting database. Offline editing should be postponed until a complete conflict-resolution and sync design exists.

### Controls that prevent cross-client data mixing

These rules are mandatory:

- No client accounting table is stored in the central firm-directory database.
- No client database connection is accepted directly from the desktop UI.
- No server request trusts a `ClientId` supplied by the UI without checking the authenticated assignment.
- Client switching disposes the previous database context and clears all client-specific cached objects.
- Background jobs, imports, email inboxes, portal credentials, scheduled tasks, exports, and document paths are keyed to one workspace.
- Every generated report displays the client name and workspace identifier.
- Every write and audit event records the authenticated user and workspace.
- Backups and restores operate on one client workspace at a time.
- Automated tests attempt cross-firm and cross-client access and must fail before release.
- Support tools require explicit firm and client selection and record the developer's reason for access.

Never depend only on a dropdown selection or a hidden navigation button for isolation. Authorization must be enforced again by the server for every request.

### Minimum first release

The first accountant-app release should include:

1. Firm creation and signed accountant-product licensing.
2. Firm owner/admin login with MFA-ready authentication.
3. Employee invitations, roles, deactivation, and session revocation.
4. Client creation with an automatically provisioned isolated database.
5. Client assignment and **My Clients** selection.
6. A persistent current-client header and safe client switching.
7. Shared live data, optimistic concurrency, and audit history.
8. Per-client backup, restore, export, and removal/disconnection workflow.
9. Automated tenant-isolation tests.

Do not begin payroll-tax payments or license-renewal automation until this identity, assignment, and isolation foundation is complete.

## Accountant client workspace

Each accounting firm should manage isolated client workspaces. A user can open a workspace only when an active client assignment authorizes it. Every workspace should store:

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
