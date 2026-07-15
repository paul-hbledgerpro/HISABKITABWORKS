# HISAB KITAB WORKS Admin License Generator

This is the WinForms replacement for the legacy WPF license generator. It is an administrator-only tool and must never be bundled with or installed by the client HISAB KITAB installer.

This folder is a standalone developer project with its own `HISAB KITAB WORKS License Generator.sln`. It is intentionally excluded from `ManagerPaperworkSystem.sln`, the client application build, and the client installer.

## Security boundary

- The RSA private signing key is not stored in this repository or compiled into the executable.
- The official V2 private signing key is created once and is never committed to GitHub or compiled into either application.
- On another developer PC, choose **Set Up / Restore Key** and select the encrypted `.hbsigningbackup` created on the first developer PC.
- The validated key is encrypted with Windows DPAPI for the current Windows user and stored under `%LOCALAPPDATA%\HISAB KITAB WORKS\License Generator`.
- Use **Back Up Key** to create a password-encrypted backup for another authorized developer PC. Keep both the backup file and its password away from customers.
- A different Windows user or computer must restore the encrypted signing-key backup separately.

## Build and package

```powershell
dotnet build .\HisabKitabWorks.LicenseGenerator.WinForms.csproj -c Release
.\publish.ps1
& 'C:\Program Files (x86)\Inno Setup 6\ISCC.exe' .\HisabKitabWorks.LicenseGenerator.iss
```

The separate admin installer is created under `installer_output`. Generated publish and installer output are ignored by Git.

## Device-license workflow

1. Connect to the licensing database.
2. Enter the customer information and the purchased **Maximum PC Seats**, then generate the subscription key.
3. Select **Import PC Request** and choose the customer's `.hbrequest` file.
4. Confirm the paid PC seats and subscription expiration.
5. Complete **Set Up / Restore Key** if this developer PC is not configured yet.
6. Select **Issue / Renew License** and save the `.hblicense` file for the customer.

The signing-key setup is required only once per authorized developer Windows account. It is intentionally required: without the private signature, customers could manufacture their own license files.
