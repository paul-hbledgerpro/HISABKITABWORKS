param(
  [string]$Configuration = "Release",
  # Windows-only: publish win-x64 self-contained. Snapdragon/ARM runs via x64 emulation.
  [string]$Runtime = "win-x64",
  [bool]$SelfContained = $true,
  # ReadyToRun is fine for win-x64; leave on by default (publish_all.ps1 passes it).
  [switch]$ReadyToRun
)

$ErrorActionPreference = "Stop"

$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$root = Resolve-Path (Join-Path $here "..")

# Final output location inside the repo (used by Inno Setup)
$finalOutDir = Join-Path $here (Join-Path "publish" $Runtime)

# ----------------------------------------------------------------------------
# IMPORTANT (Permanent fix)
#
# Many users extract this project under OneDrive paths with spaces, e.g.
#   ...\GALAXY ELGIN\HB LEDGER PRO CHAT GPT\...
#
# MSBuild sometimes mis-parses response-file arguments when property values
# contain spaces, leading to errors like:
#   "Switches appended by response files: Switch: ELGIN\HB"
#
# To make publishing 100% reliable, we publish from a clean temp folder under
# %TEMP% (no spaces), then copy the published output back into installer\publish.
# This also avoids file-lock problems from Visual Studio/AV/OneDrive.
# ----------------------------------------------------------------------------

$tempRoot = Join-Path $env:TEMP ("HISAB_KITAB_BUILD_" + ([Guid]::NewGuid().ToString('N').Substring(0,8)))
$workRoot = Join-Path $tempRoot "work"
$srcCopy  = Join-Path $workRoot "src"
$outTemp  = Join-Path $tempRoot "out"
## NOTE (permanent reliability):
## We do NOT override BaseOutputPath/BaseIntermediateOutputPath anymore.
## Overriding these can cause NETSDK1005 on some machines/SDK versions where
## restore/publish end up looking at different assets files.

Write-Host "Publishing runtime: $Runtime (Self-Contained=$SelfContained)" -ForegroundColor Cyan
Write-Host "Temp build folder: $tempRoot" -ForegroundColor DarkGray
Write-Host "Final output folder: $finalOutDir" -ForegroundColor DarkGray

try {
  # Clean temp
  if (Test-Path $tempRoot) { Remove-Item -Recurse -Force $tempRoot -ErrorAction SilentlyContinue }
  New-Item -ItemType Directory -Force -Path $srcCopy | Out-Null
  New-Item -ItemType Directory -Force -Path $outTemp | Out-Null

  # Copy source (exclude bin/obj)
  $srcOriginal = Join-Path $root "src"
  if (!(Test-Path $srcOriginal)) { throw "Source folder not found: $srcOriginal" }

  # robocopy is the most reliable copier on Windows (fast + excludes)
  & robocopy $srcOriginal $srcCopy /E /XD bin obj /NFL /NDL | Out-Null

  # Copy shared props/targets if present
  foreach ($f in @("Directory.Build.props", "Directory.Build.targets")) {
    $p = Join-Path $root $f
    if (Test-Path $p) {
      Copy-Item $p (Join-Path $workRoot $f) -Force
    }
  }

  # Locate WinForms project inside the temp copy
  $projToBuild = Join-Path $srcCopy "ManagerPaperworkSystem.WinForms\ManagerPaperworkSystem.WinForms.csproj"
  if (!(Test-Path $projToBuild)) {
    $found = Get-ChildItem -Path $srcCopy -Recurse -Filter "ManagerPaperworkSystem.WinForms.csproj" -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($found) { $projToBuild = $found.FullName }
  }
  if (!(Test-Path $projToBuild)) { throw "WinForms project not found in temp copy." }

  # Kill running app to avoid locks
  $procNames = @("HISAB KITAB", "ManagerPaperworkSystem.UI", "ManagerPaperworkSystem.Core")
  foreach ($n in $procNames) {
    Get-Process -Name $n -ErrorAction SilentlyContinue | ForEach-Object {
      try { Stop-Process -Id $_.Id -Force -ErrorAction SilentlyContinue } catch {}
    }
  }

  # Restore (in temp copy; avoids OneDrive/AV locks and path-space parsing issues)
  $restoreArgs = @(
    "restore", $projToBuild,
    "-r", $Runtime,
    "--no-cache"
  )
  & dotnet @restoreArgs
  if ($LASTEXITCODE -ne 0) { throw "dotnet restore failed (exit code $LASTEXITCODE)." }

  $useR2R = $ReadyToRun.IsPresent

  # Publish (to temp out folder with no spaces)
  $publishArgs = @(
    "publish", $projToBuild,
    "-c", $Configuration,
    "-r", $Runtime,
    "-o", $outTemp,
    "--self-contained", ($SelfContained.ToString().ToLowerInvariant()),
    "/p:PublishSingleFile=true",
    "/p:IncludeNativeLibrariesForSelfExtract=true",
    "/p:UseAppHost=true",
    ("/p:PublishReadyToRun=" + ($useR2R.ToString().ToLowerInvariant())),
    "/p:EnableCompressionInSingleFile=true",
    "/p:DebugType=None",
    "/p:DebugSymbols=false",
    "/p:BuildInParallel=false",
    "/p:UseSharedCompilation=false"
  )

  & dotnet @publishArgs
  if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed (exit code $LASTEXITCODE)." }

  # Ensure an EXE exists; if produced with a different name, copy/rename it.
  $expectedExe = Join-Path $outTemp "HISAB KITAB.exe"
  if (!(Test-Path $expectedExe)) {
    $candidate = Get-ChildItem -Path $outTemp -Filter "*.exe" -File -ErrorAction SilentlyContinue |
      Where-Object { $_.Name -notmatch "Update|vshost" } |
      Select-Object -First 1

    if ($candidate) {
      Copy-Item $candidate.FullName $expectedExe -Force
      Write-Host "Publish produced '$($candidate.Name)'. Copied to '$($expectedExe)'" -ForegroundColor Yellow
    } else {
      $files = Get-ChildItem -Path $outTemp -File -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Name
      throw "Publish did not produce an .exe in '$outTemp'. Files: $($files -join ', ')"
    }
  }

  # Copy publish output back to installer\publish\<rid>
  if (Test-Path $finalOutDir) {
    try { Remove-Item -Recurse -Force $finalOutDir -ErrorAction SilentlyContinue } catch {}
  }
  New-Item -ItemType Directory -Force -Path $finalOutDir | Out-Null
  Copy-Item -Path (Join-Path $outTemp "*") -Destination $finalOutDir -Recurse -Force

  Write-Host "Publish OK: $(Join-Path $finalOutDir 'HISAB KITAB.exe')" -ForegroundColor Green
}
finally {
  # Best effort cleanup (keep if you want to inspect failures)
  try { Remove-Item -Recurse -Force $tempRoot -ErrorAction SilentlyContinue } catch {}
}
