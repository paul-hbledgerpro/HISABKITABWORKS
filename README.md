# HISAB KITAB

HISAB KITAB is a Windows desktop bookkeeping and manager-paperwork application. The active application is the .NET 8 WinForms project at `src/ManagerPaperworkSystem.WinForms`.

## Start developing

On Windows, install the .NET 8 SDK and Visual Studio 2022 with the **.NET desktop development** workload, then run:

```powershell
.\setup-workstation.cmd
```

Open `ManagerPaperworkSystem.sln` and select `ManagerPaperworkSystem.WinForms` as the startup project. The setup launcher works with the default Windows PowerShell execution policy, restores packages, and performs a Release build without launching the app.

For cloning, private-repository setup, daily synchronization, and runtime-data guidance, see [WORK_PC_SETUP.md](WORK_PC_SETUP.md).

## Build and package

```powershell
# Build the active WinForms app
dotnet build .\src\ManagerPaperworkSystem.WinForms\ManagerPaperworkSystem.WinForms.csproj -c Release

# Publish a self-contained win-x64 package
.\installer\publish.ps1
```

Published files are written under `installer\publish\win-x64`. To create a Windows installer, install Inno Setup and compile `installer\InnoSetup\ManagerPaperworkSystem.iss` after publishing.

## Local data

Runtime data is stored separately from source code under `%LOCALAPPDATA%\Hisab Kitab`. It may include a SQLite database, invoices, licenses, preferences, and database connection settings. Do not add this data to Git.

## Project map

- `src/ManagerPaperworkSystem.WinForms` — active WinForms application
- `src/ManagerPaperworkSystem.Core` — domain models and shared services
- `src/ManagerPaperworkSystem.Data` — EF Core persistence
- `src/ManagerPaperworkSystem.Reports` — QuestPDF reports
- `src/ManagerPaperworkSystem.Updater` — updater application
- `src/ManagerPaperworkSystem.UI` — legacy WPF implementation retained for reference
- `installer` — publishing and Inno Setup files
