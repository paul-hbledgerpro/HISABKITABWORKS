# Portal selection regression check

Run on Windows with .NET 8 and Google Chrome installed:

```powershell
dotnet run --project tests/ManagerPaperworkSystem.PortalBrowser.Check -c Release
```

This console check loads the production WinForms selection method through
reflection and exercises it with an isolated headless browser and in-memory HTML.
It does not launch the desktop application, read settings, use client credentials,
contact AdventPOS, or connect to SQL Server.

The fixture reproduces AdventPOS's separate option value (STOREUSER_ID) and
store database attribute (data-StoreDBID). Multiple stores can share an option
value. Tests verify the database selected for store-user loading and final login,
using both portal callbacks and change events. They also reject a callback that
resets the selection, partial names, and duplicate exact names.

The duplicate-value case reproduces the selection failure in 1.0.169 before the
production method is changed to select the matched DOM option by index.

The browser-launch fixture keeps a setup profile open while launching two automatic
sync browsers through the production launch-options factory. It verifies that
background sync uses independent temporary profiles, cookies do not cross between
setup and sync sessions, and disposing sync browsers leaves setup open.
