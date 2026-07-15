============================================================
  HB LEDGER PRO — COMPLETE DESKTOP APP (V68 + LICENSE)
============================================================

THIS IS YOUR COMPLETE DESKTOP APP PROJECT with ALL changes applied:

  ✅ V68 Patch (SetupWizard, UpdateWindow, UpdateService)
  ✅ License Activation Window (calls API, no DB credentials for customer)
  ✅ DbInitializer fix (creates tables automatically in empty databases)
  ✅ App.xaml.cs with license activation startup flow
  ✅ All existing views, services, models unchanged

CHANGES FROM YOUR ORIGINAL V68:
================================

1. NEW FILE: UI/Views/LicenseActivationWindow.xaml + .xaml.cs
   - Customer enters: Store Name, Address, Zip, License Key
   - Calls API to validate and get database connection info
   - No database credentials needed from customer

2. FIXED: Data/Services/DbInitializer.cs
   - Was: returned without creating tables → crash
   - Now: calls EnsureCreatedAsync() → creates all tables automatically

3. UPDATED: App.xaml.cs (from your upload with license flow already built in)

BEFORE BUILDING:
=================
1. Make sure your API is deployed with the new LicenseController FIRST
2. Open the solution in Visual Studio
3. Build → should compile with no errors
4. Publish/install as usual

CUSTOMER FLOW:
===============
1. Install HB Ledger Pro
2. First launch → License Activation screen
3. Enter store name, address, zip, license key
4. Click ACTIVATE → calls API → gets DB info
5. App restarts → Setup Wizard → create admin → login → done!
