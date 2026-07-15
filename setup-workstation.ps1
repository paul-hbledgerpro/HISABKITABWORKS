[CmdletBinding()]
param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",

    [switch]$SkipBuild,
    [switch]$OpenSolution
)

$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot

$project = Join-Path $PSScriptRoot "src\ManagerPaperworkSystem.WinForms\ManagerPaperworkSystem.WinForms.csproj"
$solution = Join-Path $PSScriptRoot "ManagerPaperworkSystem.sln"

Write-Host "HISAB KITAB workstation setup" -ForegroundColor Cyan
Write-Host "Project: $PSScriptRoot" -ForegroundColor DarkGray

if (-not $IsWindows -and $PSVersionTable.PSEdition -eq "Core") {
    throw "This WinForms project must be built on Windows."
}

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw "The .NET SDK is missing. Install the .NET 8 SDK, then run this script again."
}

$installedSdks = @(& dotnet --list-sdks)
if (-not ($installedSdks | Where-Object { $_ -match '^8\.' })) {
    Write-Host "Install command (if winget is available):" -ForegroundColor Yellow
    Write-Host "  winget install Microsoft.DotNet.SDK.8" -ForegroundColor Yellow
    throw "The .NET 8 SDK is required."
}

if (-not (Test-Path $project)) {
    throw "WinForms project not found: $project"
}

$selectedSdk = & dotnet --version
Write-Host ".NET SDK: $selectedSdk" -ForegroundColor Green

Write-Host "Restoring WinForms dependencies..." -ForegroundColor Cyan
& dotnet restore $project --nologo
if ($LASTEXITCODE -ne 0) {
    throw "Package restore failed with exit code $LASTEXITCODE."
}

if (-not $SkipBuild) {
    Write-Host "Building WinForms app ($Configuration)..." -ForegroundColor Cyan
    & dotnet build $project -c $Configuration --no-restore --nologo
    if ($LASTEXITCODE -ne 0) {
        throw "Build failed with exit code $LASTEXITCODE."
    }
}

Write-Host "Workstation setup completed successfully." -ForegroundColor Green
Write-Host "The app was not launched." -ForegroundColor DarkGray

if ($OpenSolution) {
    Start-Process $solution
}

