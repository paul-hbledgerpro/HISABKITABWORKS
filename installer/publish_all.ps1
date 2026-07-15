param(
  [string]$Configuration = "Release",
  [bool]$SelfContained = $true
)

$ErrorActionPreference = "Stop"

$here = Split-Path -Parent $MyInvocation.MyCommand.Path

Write-Host "Publishing Windows-only runtime: win-x64 (Self-Contained)" -ForegroundColor Cyan
Write-Host "Snapdragon/Windows-on-ARM will run this build via x64 emulation." -ForegroundColor DarkGray

& (Join-Path $here "publish.ps1") -Configuration $Configuration -Runtime "win-x64" -SelfContained:$SelfContained -ReadyToRun

Write-Host "\nPublish output is ready under installer\\publish\\win-x64" -ForegroundColor Green
