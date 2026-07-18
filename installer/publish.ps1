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
$updaterOutTemp = Join-Path $tempRoot "updater-out"
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
  New-Item -ItemType Directory -Force -Path $updaterOutTemp | Out-Null

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
      Where-Object { $_.Name -notmatch "Update|Upgrade|vshost" } |
      Select-Object -First 1

    if ($candidate) {
      Copy-Item $candidate.FullName $expectedExe -Force
      Write-Host "Publish produced '$($candidate.Name)'. Copied to '$($expectedExe)'" -ForegroundColor Yellow
    } else {
      $files = Get-ChildItem -Path $outTemp -File -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Name
      throw "Publish did not produce an .exe in '$outTemp'. Files: $($files -join ', ')"
    }
  }

  # Publish the bundled Upgrade.exe as a self-contained, single-file utility.
  $updaterProject = Join-Path $srcCopy "ManagerPaperworkSystem.Updater\ManagerPaperworkSystem.Updater.csproj"
  if (!(Test-Path $updaterProject)) { throw "Updater project not found in temp copy: $updaterProject" }

  & dotnet restore $updaterProject -r $Runtime --no-cache
  if ($LASTEXITCODE -ne 0) { throw "Updater restore failed (exit code $LASTEXITCODE)." }

  $updaterPublishArgs = @(
    "publish", $updaterProject,
    "-c", $Configuration,
    "-r", $Runtime,
    "-o", $updaterOutTemp,
    "--self-contained", "true",
    "--no-restore",
    "/p:PublishSingleFile=true",
    "/p:IncludeNativeLibrariesForSelfExtract=true",
    "/p:EnableCompressionInSingleFile=true",
    "/p:DebugType=None",
    "/p:DebugSymbols=false"
  )
  & dotnet @updaterPublishArgs
  if ($LASTEXITCODE -ne 0) { throw "Updater publish failed (exit code $LASTEXITCODE)." }

  $upgradeExe = Join-Path $updaterOutTemp "Upgrade.exe"
  if (!(Test-Path $upgradeExe)) { throw "Updater publish did not produce Upgrade.exe." }
  Copy-Item $upgradeExe (Join-Path $outTemp "Upgrade.exe") -Force
  $updaterPayloadDirectory = Join-Path $outTemp "UpdaterPayload"
  New-Item -ItemType Directory -Force -Path $updaterPayloadDirectory | Out-Null
  Copy-Item $upgradeExe (Join-Path $updaterPayloadDirectory "Upgrade.exe") -Force

  # The updater reads this file when comparing the installed build to a GitHub release tag.
  $appVersion = (Get-Item $expectedExe).VersionInfo.ProductVersion
  if ([string]::IsNullOrWhiteSpace($appVersion)) { $appVersion = (Get-Item $expectedExe).VersionInfo.FileVersion }
  if ([string]::IsNullOrWhiteSpace($appVersion)) { throw "Unable to determine the published application version." }
  $appVersion = ($appVersion -split '[+-]')[0]
  Set-Content -LiteralPath (Join-Path $outTemp "version.txt") -Value $appVersion -Encoding ascii

  # Copy publish output back to installer\publish\<rid>
  if (Test-Path $finalOutDir) {
    $removed = $false
    for ($attempt = 1; $attempt -le 10 -and !$removed; $attempt++) {
      try {
        Remove-Item -Recurse -Force $finalOutDir -ErrorAction Stop
        $removed = $true
      }
      catch {
        if ($attempt -eq 10) { throw "Unable to replace publish folder after 10 attempts: $($_.Exception.Message)" }
        Start-Sleep -Seconds 1
      }
    }
  }
  New-Item -ItemType Directory -Force -Path $finalOutDir | Out-Null
  & robocopy $outTemp $finalOutDir /E /R:10 /W:1 /NFL /NDL /NJH /NJS | Out-Null
  if ($LASTEXITCODE -ge 8) { throw "Unable to copy published files to $finalOutDir (robocopy exit code $LASTEXITCODE)." }

  # Create the exact ZIP asset that should be attached to the matching GitHub Release.
  $releaseDir = Join-Path $here "release"
  New-Item -ItemType Directory -Force -Path $releaseDir | Out-Null
  $releaseZip = Join-Path $releaseDir ("HISAB_KITAB_Update_win-x64_" + $appVersion + ".zip")
  if (Test-Path $releaseZip) {
    for ($attempt = 1; $attempt -le 10; $attempt++) {
      try {
        Remove-Item -LiteralPath $releaseZip -Force -ErrorAction Stop
        break
      }
      catch {
        if ($attempt -eq 10) { throw "Unable to replace release ZIP after 10 attempts: $($_.Exception.Message)" }
        Start-Sleep -Seconds 1
      }
    }
  }
  Add-Type -AssemblyName System.IO.Compression.FileSystem
  [System.IO.Compression.ZipFile]::CreateFromDirectory(
    $finalOutDir,
    $releaseZip,
    [System.IO.Compression.CompressionLevel]::Optimal,
    $false)

  Write-Host "Publish OK: $(Join-Path $finalOutDir 'HISAB KITAB.exe')" -ForegroundColor Green
  Write-Host "Bundled upgrader: $(Join-Path $finalOutDir 'Upgrade.exe')" -ForegroundColor Green
  Write-Host "GitHub release asset: $releaseZip" -ForegroundColor Green
}
finally {
  # Best effort cleanup (keep if you want to inspect failures)
  try { Remove-Item -Recurse -Force $tempRoot -ErrorAction SilentlyContinue } catch {}
}
