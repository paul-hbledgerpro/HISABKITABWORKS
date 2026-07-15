# HISAB KITAB WORKS Admin License Generator

This is the WinForms replacement for the legacy WPF license generator. It is an administrator-only tool and must never be bundled with or installed by the client HISAB KITAB installer.

This folder is a standalone developer project with its own `HISAB KITAB WORKS License Generator.sln`. It is intentionally excluded from `ManagerPaperworkSystem.sln`, the client application build, and the client installer.

## Security boundary

- The RSA private signing key is not stored in this repository or compiled into the executable.
- On first use, choose **Import Signing Key** and select either the original `LicenseGen_MainWindow.xaml.cs` file, a raw Base64 key file, or an RSA private-key PEM file.
- The validated key is encrypted with Windows DPAPI for the current Windows user and stored under `%LOCALAPPDATA%\HISAB KITAB WORKS\License Generator`.
- A different Windows user or computer must import the signing key separately.

## Build and package

```powershell
dotnet build .\HisabKitabWorks.LicenseGenerator.WinForms.csproj -c Release
.\publish.ps1
& 'C:\Program Files (x86)\Inno Setup 6\ISCC.exe' .\HisabKitabWorks.LicenseGenerator.iss
```

The separate admin installer is created under `installer_output`. Generated publish and installer output are ignored by Git.

## Device-license workflow

1. Connect to the licensing database.
2. Select **Import PC Request** and choose the customer's `.hbrequest` file.
3. Confirm the paid PC seats and subscription expiration.
4. Import the private signing key if this developer PC is not configured yet.
5. Select **Issue / Renew License** and save the `.hblicense` file for the customer.
