param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64"
)

$ErrorActionPreference = "Stop"

$installerDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$root = (Resolve-Path (Join-Path $installerDir "..")).Path
$publishRoot = Join-Path $installerDir "publish"
$releaseDir = Join-Path $installerDir "release"
$innoDir = Join-Path $installerDir "InnoSetup"

$clientPublish = Join-Path $publishRoot "client-win-x64"
$updaterPublish = Join-Path $publishRoot "updater-win-x64"
$licensePublish = Join-Path $publishRoot "license-generator-win-x64"
$accountPublish = Join-Path $publishRoot "account-manager-win-x64"

$clientProject = Join-Path $root "src\ManagerPaperworkSystem.WinForms\ManagerPaperworkSystem.WinForms.csproj"
$updaterProject = Join-Path $root "src\ManagerPaperworkSystem.Updater\ManagerPaperworkSystem.Updater.csproj"
$licenseProject = Join-Path $root "developer-only\HISAB-KITAB-LICENSE-GENERATOR\HisabKitabWorks.LicenseGenerator.WinForms.csproj"
$accountProject = Join-Path $root "developer-only\HISAB-KITAB-CLIENT-ACCOUNT-MANAGER\HisabKitabWorks.ClientAccountManager.WinForms.csproj"

function Reset-BuildDirectory([string]$Path) {
    $fullPath = [IO.Path]::GetFullPath($Path)
    $allowedRoot = [IO.Path]::GetFullPath($publishRoot) + [IO.Path]::DirectorySeparatorChar
    if (-not $fullPath.StartsWith($allowedRoot, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to clean a folder outside installer\publish: $fullPath"
    }

    if (Test-Path -LiteralPath $fullPath) {
        Remove-Item -LiteralPath $fullPath -Recurse -Force
    }
    New-Item -ItemType Directory -Force -Path $fullPath | Out-Null
}

function Publish-DesktopApp(
    [string]$Project,
    [string]$OutputDirectory,
    [string]$ExpectedExe,
    [bool]$SingleFile = $false) {
    if (-not (Test-Path -LiteralPath $Project)) {
        throw "Project not found: $Project"
    }

    Reset-BuildDirectory $OutputDirectory
    Write-Host "Publishing $ExpectedExe ..." -ForegroundColor Cyan

    $arguments = @(
        "publish", $Project,
        "-c", $Configuration,
        "-r", $Runtime,
        "-o", $OutputDirectory,
        "--self-contained", "true",
        ("/p:PublishSingleFile=" + $SingleFile.ToString().ToLowerInvariant()),
        "/p:UseAppHost=true",
        "/p:PublishTrimmed=false",
        "/p:PublishReadyToRun=false",
        "/p:IncludeNativeLibrariesForSelfExtract=true",
        "/p:EnableCompressionInSingleFile=true",
        "/p:DebugType=None",
        "/p:DebugSymbols=false"
    )

    & dotnet @arguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed for $Project (exit code $LASTEXITCODE)."
    }

    $exePath = Join-Path $OutputDirectory $ExpectedExe
    if (-not (Test-Path -LiteralPath $exePath)) {
        throw "Publish did not create the expected executable: $exePath"
    }
}

$isccCandidates = @(
    (Join-Path $env:LOCALAPPDATA "Programs\Inno Setup 6\ISCC.exe"),
    "C:\Program Files (x86)\Inno Setup 6\ISCC.exe",
    "C:\Program Files\Inno Setup 6\ISCC.exe"
)
$iscc = $isccCandidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
if (-not $iscc) {
    throw "Inno Setup 6 compiler (ISCC.exe) was not found."
}

New-Item -ItemType Directory -Force -Path $publishRoot, $releaseDir | Out-Null

Publish-DesktopApp $clientProject $clientPublish "HISAB KITAB.exe"
Publish-DesktopApp $updaterProject $updaterPublish "Upgrade.exe" $true
$updaterPayloadDirectory = Join-Path $clientPublish "UpdaterPayload"
New-Item -ItemType Directory -Force -Path $updaterPayloadDirectory | Out-Null
foreach ($updaterFile in Get-ChildItem -LiteralPath $updaterPublish -File) {
    Copy-Item -LiteralPath $updaterFile.FullName -Destination (Join-Path $updaterPayloadDirectory $updaterFile.Name) -Force
}
Copy-Item -LiteralPath (Join-Path $updaterPublish "Upgrade.exe") -Destination (Join-Path $clientPublish "Upgrade.exe") -Force
Set-Content -LiteralPath (Join-Path $clientPublish "version.txt") -Value "1.0.88" -Encoding Ascii

Publish-DesktopApp $licenseProject $licensePublish "HISAB KITAB WORKS License Generator.exe"
Publish-DesktopApp $accountProject $accountPublish "HISAB KITAB WORKS Client Account Manager.exe"

$scripts = @(
    (Join-Path $innoDir "HISAB_KITAB_WORKS_Client.iss"),
    (Join-Path $innoDir "HISAB_KITAB_WORKS_Developer_License_Generator.iss"),
    (Join-Path $innoDir "HISAB_KITAB_WORKS_Developer_Account_Manager.iss")
)

foreach ($script in $scripts) {
    Write-Host "Compiling $(Split-Path -Leaf $script) ..." -ForegroundColor Cyan
    & $iscc $script
    if ($LASTEXITCODE -ne 0) {
        throw "Inno Setup compilation failed for $script (exit code $LASTEXITCODE)."
    }
}

function New-ClientUpdatePackage([string]$Version) {
    $updateZip = Join-Path $releaseDir "HISAB_KITAB_Update_win-x64_$Version.zip"
    if (Test-Path -LiteralPath $updateZip) {
        Remove-Item -LiteralPath $updateZip -Force
    }

    $entries = [Collections.Generic.List[object]]::new()
    foreach ($name in @(
        "HISAB KITAB.exe",
        "HISAB KITAB.dll",
        "HISAB KITAB.deps.json",
        "HISAB KITAB.runtimeconfig.json",
        "ManagerPaperworkSystem.Core.dll",
        "ManagerPaperworkSystem.Data.dll",
        "ManagerPaperworkSystem.Reports.dll",
        "version.txt"
    )) {
        $entries.Add([pscustomobject]@{
            Source = Join-Path $clientPublish $name
            Entry = $name
        })
    }

    foreach ($directoryName in @("UpdaterPayload", "Assets", "TaxRules")) {
        $directory = Join-Path $clientPublish $directoryName
        if (-not (Test-Path -LiteralPath $directory)) {
            continue
        }

        foreach ($file in Get-ChildItem -LiteralPath $directory -File -Recurse) {
            $relative = [IO.Path]::GetRelativePath($clientPublish, $file.FullName)
            $entries.Add([pscustomobject]@{
                Source = $file.FullName
                Entry = $relative.Replace('\', '/')
            })
        }
    }

    foreach ($entry in $entries) {
        if (-not (Test-Path -LiteralPath $entry.Source)) {
            throw "Required automatic-update file is missing: $($entry.Source)"
        }
    }

    $versionText = (Get-Content -LiteralPath (Join-Path $clientPublish "version.txt") -Raw).Trim()
    if ($versionText -ne $Version) {
        throw "Automatic-update version mismatch. Expected $Version, found $versionText."
    }

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $stream = [IO.File]::Open($updateZip, [IO.FileMode]::CreateNew)
    try {
        $archive = [IO.Compression.ZipArchive]::new(
            $stream,
            [IO.Compression.ZipArchiveMode]::Create,
            $false)
        try {
            foreach ($entry in $entries) {
                [IO.Compression.ZipFileExtensions]::CreateEntryFromFile(
                    $archive,
                    $entry.Source,
                    $entry.Entry,
                    [IO.Compression.CompressionLevel]::Optimal) | Out-Null
            }
        }
        finally {
            $archive.Dispose()
        }
    }
    finally {
        $stream.Dispose()
    }

    return $updateZip
}

$clientUpdateZip = New-ClientUpdatePackage "1.0.88"

$expectedInstallers = @(
    (Join-Path $releaseDir "HISAB_KITAB_WORKS_Client_Setup_1.0.88.exe"),
    (Join-Path $releaseDir "HISAB_KITAB_WORKS_License_Generator_Setup_1.0.88.exe"),
    (Join-Path $releaseDir "HISAB_KITAB_WORKS_Account_Manager_Setup_1.0.88.exe")
)

Write-Host ""
Write-Host "Installer build completed:" -ForegroundColor Green
foreach ($installer in $expectedInstallers) {
    if (-not (Test-Path -LiteralPath $installer)) {
        throw "Expected installer was not created: $installer"
    }
    $file = Get-Item -LiteralPath $installer
    $stream = [IO.File]::OpenRead($installer)
    try {
        $sha256 = [Security.Cryptography.SHA256]::Create()
        try {
            $hash = ([BitConverter]::ToString($sha256.ComputeHash($stream))).Replace("-", "")
        }
        finally {
            $sha256.Dispose()
        }
    }
    finally {
        $stream.Dispose()
    }
    Write-Host ("  {0}  ({1:N1} MB)" -f $file.FullName, ($file.Length / 1MB)) -ForegroundColor Green
    Write-Host ("    SHA256: {0}" -f $hash) -ForegroundColor DarkGray
}

$updateFile = Get-Item -LiteralPath $clientUpdateZip
$updateHash = (Get-FileHash -LiteralPath $clientUpdateZip -Algorithm SHA256).Hash
Write-Host ("  {0}  ({1:N1} MB)" -f $updateFile.FullName, ($updateFile.Length / 1MB)) -ForegroundColor Green
Write-Host ("    SHA256: {0}" -f $updateHash) -ForegroundColor DarkGray
