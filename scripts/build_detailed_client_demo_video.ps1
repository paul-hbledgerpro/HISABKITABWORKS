param(
    [string]$OutputDirectory = "deliverables\detailed-client-demo-video"
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$projectRoot = Split-Path -Parent $PSScriptRoot
$outputRoot = Join-Path $projectRoot $OutputDirectory
$workRoot = Join-Path $projectRoot "tmp\detailed-demo-video"
$presentationRoot = Join-Path $workRoot "presentation"
$narrationRoot = Join-Path $workRoot "narration"
$projectFile = Join-Path $projectRoot "src\ManagerPaperworkSystem.WinForms\ManagerPaperworkSystem.WinForms.csproj"
$demoExe = Join-Path $projectRoot "src\ManagerPaperworkSystem.WinForms\bin\Release\net8.0-windows\win-x64\HISAB KITAB.exe"
$ffmpeg = "C:\ffmpeg\bin\ffmpeg.exe"
$ffprobe = "C:\ffmpeg\bin\ffprobe.exe"
$narrationScript = Join-Path $PSScriptRoot "generate_detailed_demo_narration.py"

foreach ($required in @($ffmpeg, $ffprobe, $narrationScript)) {
    if (-not (Test-Path -LiteralPath $required)) { throw "Required file was not found: $required" }
}

New-Item -ItemType Directory -Force -Path $outputRoot, $workRoot, $presentationRoot, $narrationRoot | Out-Null
foreach ($name in @("presentation-ready.txt", "presentation-go.txt", "presentation-complete.txt", "presentation-error.txt", "presentation-thread-errors.txt", "presentation-cues.txt")) {
    $path = Join-Path $presentationRoot $name
    if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path -Force }
}

& dotnet build $projectFile -c Release
if ($LASTEXITCODE -ne 0) { throw "The WinForms Release build failed." }

$relativePresentationRoot = [IO.Path]::GetRelativePath($projectRoot, $presentationRoot)
$app = Start-Process -FilePath $demoExe `
    -ArgumentList @("--demo-presentation", $relativePresentationRoot) `
    -WorkingDirectory $projectRoot `
    -PassThru

$readyPath = Join-Path $presentationRoot "presentation-ready.txt"
$goPath = Join-Path $presentationRoot "presentation-go.txt"
$completePath = Join-Path $presentationRoot "presentation-complete.txt"
$errorPath = Join-Path $presentationRoot "presentation-error.txt"
$cuesPath = Join-Path $presentationRoot "presentation-cues.txt"
$deadline = (Get-Date).AddMinutes(2)
while (-not (Test-Path -LiteralPath $readyPath) -and -not $app.HasExited -and (Get-Date) -lt $deadline) {
    Start-Sleep -Milliseconds 200
    $app.Refresh()
}
if (-not (Test-Path -LiteralPath $readyPath)) {
    throw "The detailed demo client did not become ready."
}

$rawVideo = Join-Path $presentationRoot "detailed-demo-raw.mkv"
Set-Content -LiteralPath $goPath -Value (Get-Date).ToUniversalTime().ToString("O") -Encoding utf8

while (-not $app.HasExited) {
    Start-Sleep -Seconds 2
    $app.Refresh()
}

if (Test-Path -LiteralPath $errorPath) {
    throw "The detailed presentation failed:`n$(Get-Content -LiteralPath $errorPath -Raw)"
}
$threadErrorPath = Join-Path $presentationRoot "presentation-thread-errors.txt"
if (Test-Path -LiteralPath $threadErrorPath) {
    throw "The detailed presentation logged a UI error:`n$(Get-Content -LiteralPath $threadErrorPath -Raw)"
}
if (-not (Test-Path -LiteralPath $completePath) -or -not (Test-Path -LiteralPath $cuesPath)) {
    throw "The detailed presentation did not complete."
}
if (-not (Test-Path -LiteralPath $rawVideo)) { throw "The presentation recording was not created." }

$narrationAudio = Join-Path $workRoot "professional-narration.m4a"
$captions = Join-Path $outputRoot "HISAB_KITAB_WORKS_Detailed_Client_Demo.srt"
& python $narrationScript $cuesPath $narrationRoot $narrationAudio $captions $ffprobe
if ($LASTEXITCODE -ne 0) { throw "Professional narration generation failed." }

$finalVideo = Join-Path $outputRoot "HISAB_KITAB_WORKS_Detailed_Client_Demo.mp4"
& $ffmpeg -hide_banner -loglevel error -y `
    -i $rawVideo -i $narrationAudio -i $captions `
    -filter_complex "[0:v]scale=1920:1080:flags=lanczos,setsar=1[v];[1:a]loudnorm=I=-16:TP=-1.5:LRA=11[a]" `
    -map "[v]" -map "[a]" -map 2:0 `
    -c:v libx264 -preset medium -crf 18 -pix_fmt yuv420p `
    -c:a aac -b:a 192k -ar 48000 `
    -c:s mov_text -metadata:s:s:0 language=eng -metadata:s:s:0 title="English Captions" `
    -shortest -movflags +faststart $finalVideo
if ($LASTEXITCODE -ne 0) { throw "The final detailed video render failed." }

Write-Host "Detailed demo video: $finalVideo"
Write-Host "Captions:           $captions"
