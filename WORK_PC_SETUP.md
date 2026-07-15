# HISAB KITAB work-PC setup

This repository is prepared so the unfinished WinForms project can be developed on more than one Windows PC. Source code belongs in a private Git repository. Generated EXEs, installer packages, Visual Studio settings, databases, licenses, and connection settings are deliberately excluded.

## 1. Put this copy in a private remote repository

After creating an empty private repository on GitHub, Azure DevOps, or your company's approved Git server, run these commands once on the home PC:

```powershell
git remote add origin <PRIVATE_REPOSITORY_URL>
git push -u origin main
```

Do not use a public repository. This is a business application and its history may contain implementation or infrastructure details.

## 2. Prepare the work PC

Install:

- Git for Windows
- Visual Studio 2022 with the **.NET desktop development** workload
- .NET 8 SDK (the Visual Studio workload may already install it)

Clone to a short local path to avoid Windows and OneDrive path-length/file-lock problems:

```powershell
mkdir C:\Dev
cd C:\Dev
git clone <PRIVATE_REPOSITORY_URL> HisabKitab
cd HisabKitab
.\setup-workstation.cmd
```

The CMD launcher works with the default Windows PowerShell execution policy. It verifies .NET 8, restores packages, and builds the active WinForms project in Release mode. It does not launch the app. To open Visual Studio after a successful build:

```powershell
.\setup-workstation.cmd -OpenSolution
```

You can also invoke the PowerShell script directly when execution policy permits it:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\setup-workstation.ps1
```

Use `ManagerPaperworkSystem.WinForms` as the startup project. `ManagerPaperworkSystem.UI` is the older WPF implementation and is not the active conversion target.

## 3. Move work safely between PCs

Before starting on either PC:

```powershell
git pull --rebase
```

After a completed, tested piece of work:

```powershell
git status
git add <files-you-intended-to-change>
git commit -m "Describe the completed change"
git push
```

Avoid editing the same files on both computers before syncing. Commit only source/configuration files you understand; do not force-add ignored databases, credentials, `bin`, `obj`, published EXEs, or installer output.

## Runtime data is separate

The source repository does not synchronize live HISAB KITAB data. The WinForms app keeps local runtime data under:

```text
%LOCALAPPDATA%\Hisab Kitab
```

That folder can contain the SQLite database, invoices, licenses, preferences, and SQL Server connection credentials. Never commit it. If both PCs must use the same business data, use the app's supported backup/restore path or a shared SQL Server database and transfer backups through an encrypted, company-approved location.

## Useful commands

```powershell
# Verify the active app without launching it
dotnet build .\src\ManagerPaperworkSystem.WinForms\ManagerPaperworkSystem.WinForms.csproj -c Release

# Create the self-contained win-x64 application package
.\installer\publish.ps1
```

Inno Setup is optional and only needed when producing `HISAB_KITAB_Setup.exe` from `installer\InnoSetup\ManagerPaperworkSystem.iss`.
