param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [string]$Version = "1.0.157"
)

$ErrorActionPreference = "Stop"
$installerDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$root = (Resolve-Path (Join-Path $installerDir "..")).Path
$publishRoot = Join-Path $installerDir "publish"
$publishDirectory = Join-Path $publishRoot "demo-win-x64"
$project = Join-Path $root "src\ManagerPaperworkSystem.WinForms\ManagerPaperworkSystem.WinForms.csproj"
$script = Join-Path $installerDir "InnoSetup\HISAB_KITAB_WORKS_Demo.iss"

$fullPublish = [IO.Path]::GetFullPath($publishDirectory)
$allowedRoot = [IO.Path]::GetFullPath($publishRoot) + [IO.Path]::DirectorySeparatorChar
if (-not $fullPublish.StartsWith($allowedRoot, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Refusing to clean a folder outside installer\publish: $fullPublish"
}
if (Test-Path -LiteralPath $fullPublish) {
    Remove-Item -LiteralPath $fullPublish -Recurse -Force
}
New-Item -ItemType Directory -Force -Path $fullPublish | Out-Null

& dotnet publish $project `
    -c $Configuration `
    -r $Runtime `
    -o $fullPublish `
    --self-contained true `
    /p:PublishSingleFile=false `
    /p:UseAppHost=true `
    /p:PublishTrimmed=false `
    /p:DebugType=None `
    /p:DebugSymbols=false
if ($LASTEXITCODE -ne 0) {
    throw "The demo publish failed with exit code $LASTEXITCODE."
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

& $iscc "/DMyAppVersion=$Version" $script
if ($LASTEXITCODE -ne 0) {
    throw "The demo installer compile failed with exit code $LASTEXITCODE."
}

$installer = Join-Path $installerDir "release\HISAB_KITAB_WORKS_Client_Demo_Setup_$Version.exe"
if (-not (Test-Path -LiteralPath $installer)) {
    throw "The expected demo installer was not created: $installer"
}

$file = Get-Item -LiteralPath $installer
$hash = (Get-FileHash -LiteralPath $installer -Algorithm SHA256).Hash
Write-Host "Demo installer created:" -ForegroundColor Green
Write-Host ("  {0}  ({1:N1} MB)" -f $file.FullName, ($file.Length / 1MB)) -ForegroundColor Green
Write-Host ("  SHA256: {0}" -f $hash) -ForegroundColor DarkGray
