param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64"
)

$ErrorActionPreference = "Stop"
$projectDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$projectFile = Join-Path $projectDir "HisabKitabWorks.LicenseGenerator.WinForms.csproj"
$outputDir = Join-Path $projectDir "publish\$Runtime"

if (Test-Path -LiteralPath $outputDir) {
    Remove-Item -LiteralPath $outputDir -Recurse -Force
}

dotnet publish $projectFile `
    -c $Configuration `
    -r $Runtime `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:DebugType=None `
    -p:DebugSymbols=false `
    -o $outputDir

if ($LASTEXITCODE -ne 0) {
    throw "License Generator publish failed with exit code $LASTEXITCODE."
}

Write-Host "Published admin License Generator to: $outputDir"
