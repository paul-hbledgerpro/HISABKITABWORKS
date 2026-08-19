param(
    [string]$OutputDirectory = "deliverables\client-demo-video",
    [switch]$SkipCapture
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$projectRoot = Split-Path -Parent $PSScriptRoot
$outputRoot = Join-Path $projectRoot $OutputDirectory
$captureRoot = Join-Path $projectRoot "tmp\demo-video\captures"
$workRoot = Join-Path $projectRoot "tmp\demo-video\render"
$projectFile = Join-Path $projectRoot "src\ManagerPaperworkSystem.WinForms\ManagerPaperworkSystem.WinForms.csproj"
$demoExe = Join-Path $projectRoot "src\ManagerPaperworkSystem.WinForms\bin\Release\net8.0-windows\win-x64\HISAB KITAB.exe"
$logoPath = Join-Path $projectRoot "src\ManagerPaperworkSystem.UI\Assets\HisabKitab_Logo.png"
$ffmpeg = "C:\ffmpeg\bin\ffmpeg.exe"
$ffprobe = "C:\ffmpeg\bin\ffprobe.exe"

foreach ($requiredFile in @($ffmpeg, $ffprobe, $logoPath)) {
    if (-not (Test-Path -LiteralPath $requiredFile)) {
        throw "Required file was not found: $requiredFile"
    }
}

New-Item -ItemType Directory -Force -Path $outputRoot, $captureRoot, $workRoot | Out-Null

if (-not $SkipCapture) {
    & dotnet build $projectFile -c Release
    if ($LASTEXITCODE -ne 0) { throw "The WinForms Release build failed." }

    $relativeCaptureRoot = [IO.Path]::GetRelativePath($projectRoot, $captureRoot)
    $demoProcess = Start-Process -FilePath $demoExe `
        -ArgumentList @("--demo", "--demo-capture", $relativeCaptureRoot) `
        -WorkingDirectory $projectRoot `
        -PassThru
    $demoProcess.WaitForExit()
    if ($demoProcess.ExitCode -ne 0) {
        throw "The automated demo capture failed. See $captureRoot for details."
    }
}

$scenes = @(
    [pscustomobject]@{
        File = "title.png"; Title = "HISAB KITAB WORKS"; Caption = "A complete retail business management system";
        Narration = "Introducing HISAB KITAB WORKS: a complete Windows business management system designed for independent retailers, multi-store owners, and their management teams. This demonstration uses fictional data and keeps every external connection disabled."
    },
    [pscustomobject]@{
        File = "01-dashboard.png"; Title = "Business Dashboard"; Caption = "See the numbers that matter as soon as you sign in";
        Narration = "The dashboard gives owners an immediate view of sales, cash, payouts, purchases, payroll, and profit. Current totals and visual trends make daily performance easy to understand without building a separate spreadsheet."
    },
    [pscustomobject]@{
        File = "02-multiple-stores.png"; Title = "Multiple Stores, One Login"; Caption = "Keep every business separate and switch in seconds";
        Narration = "Manage multiple businesses from one secure login. Each store keeps its own records, employees, reports, and automation settings, while authorized users can switch stores in seconds."
    },
    [pscustomobject]@{
        File = "03-cash-sales-summary.png"; Title = "Cash & Sales Summary"; Caption = "Reconcile daily sales with cash-drop totals";
        Narration = "Cash and Sales Summary organizes each business day into gross sales, card sales, expected cash, cash drops, and over or short results. Imported reports can be reviewed and reconciled from one screen."
    },
    [pscustomobject]@{
        File = "04-shift-cash-drop.png"; Title = "Shift Cash Drop"; Caption = "Track every register batch and identify over or short shifts";
        Narration = "Shift Cash Drop tracks each register batch independently. Managers can enter the actual cash received, compare it with the Z report, and quickly identify shifts that are over, short, or still waiting for review."
    },
    [pscustomobject]@{
        File = "05-cash-on-hand.png"; Title = "Cash On Hand"; Caption = "Maintain a complete, auditable cash record";
        Narration = "Cash On Hand records money added, cash payouts, running balances, and supporting notes. Corrected entries remain traceable, giving the business a dependable cash history."
    },
    [pscustomobject]@{
        File = "06-check-payout.png"; Title = "Check Payouts"; Caption = "Monitor issued checks and their clearing status";
        Narration = "The Check Payout section records every issued check, its purpose, amount, payee, and clearing status. Managers can see outstanding obligations and confirm cleared payments."
    },
    [pscustomobject]@{
        File = "07-operations-hub.png"; Title = "Operations Hub"; Caption = "A consolidated view of daily operating activity";
        Narration = "The Operations Hub brings together key operating activity so owners can review sales, cash movements, purchases, and business performance from a single workspace."
    },
    [pscustomobject]@{
        File = "08-vendors-purposes.png"; Title = "Vendors & Purposes"; Caption = "Standardize purchasing and payout descriptions";
        Narration = "Maintain reusable vendor and purpose lists for cleaner, more consistent records. Standard descriptions reduce duplicate names and make reporting easier to understand."
    },
    [pscustomobject]@{
        File = "09-purchases.png"; Title = "Purchase Management"; Caption = "Organize invoices, vendors, totals, and supporting files";
        Narration = "Purchase Management stores invoice dates, vendors, categories, totals, and source documents. It creates one organized history for reviewing expenses and supporting product-cost analysis."
    },
    [pscustomobject]@{
        File = "10-bank-statement.png"; Title = "Bank Statement Review"; Caption = "Import, categorize, and reconcile account activity";
        Narration = "Bank Statement tools help import and review transactions, categorize income and expenses, and control which items are included in financial reporting without double counting matched records."
    },
    [pscustomobject]@{
        File = "11-product-costs.png"; Title = "Product Costs"; Caption = "See the latest supplier cost for every tracked product";
        Narration = "Product Costs show the latest unit cost, vendor, invoice date, and product history. Owners gain a clear view of changing supplier prices before those changes reduce profit margins."
    },
    [pscustomobject]@{
        File = "12-price-alerts.png"; Title = "Price Alerts"; Caption = "Spot important supplier cost changes early";
        Narration = "Price Alerts highlight meaningful cost increases and decreases. The business can review unread alerts, compare old and new costs, and decide when selling prices need attention."
    },
    [pscustomobject]@{
        File = "13-scheduling.png"; Title = "Employee Scheduling"; Caption = "Create weekly schedules for the entire team";
        Narration = "Scheduling brings every employee into one weekly plan. Managers can assign shifts, record breaks and statuses, publish the schedule, and export a professional color-coded PDF for employees."
    },
    [pscustomobject]@{
        File = "14-payroll.png"; Title = "Payroll"; Caption = "Move approved hours into an organized payroll workflow";
        Narration = "Payroll connects employee records, approved hours, payroll runs, checks, and pay stubs. Draft and finalized runs stay visible, with year-to-date totals available for management review."
    },
    [pscustomobject]@{
        File = "15-profit-loss.png"; Title = "Profit & Loss"; Caption = "Understand revenue, costs, expenses, and net profit";
        Narration = "Profit and Loss combines business sales with purchases, payouts, payroll, and selected bank activity. Owners can review income, expenses by category, net profit, and margin for the chosen period."
    },
    [pscustomobject]@{
        File = "16-reports.png"; Title = "Reports & Exports"; Caption = "Generate the business records you need";
        Narration = "The Reports center generates sales, shift, cash, check, profit and loss, and payroll reports. Results can be previewed, printed, saved as PDF, or exported for further analysis."
    },
    [pscustomobject]@{
        File = "closing.png"; Title = "RUN YOUR BUSINESS WITH CLARITY"; Caption = "HISAB KITAB WORKS";
        Narration = "HISAB KITAB WORKS gives retail owners one organized place for daily operations, financial visibility, employees, and reporting. Schedule your personal demonstration to see how it can fit your business."
    }
)

Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Speech

function New-Canvas {
    param([string]$Path, [string]$Title, [string]$Caption, [string]$SourceImage = "", [switch]$IsCard)

    $bitmap = [Drawing.Bitmap]::new(1920, 1080)
    $graphics = [Drawing.Graphics]::FromImage($bitmap)
    $graphics.SmoothingMode = [Drawing.Drawing2D.SmoothingMode]::HighQuality
    $graphics.InterpolationMode = [Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $graphics.PixelOffsetMode = [Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    try {
        $navy = [Drawing.Color]::FromArgb(24, 62, 99)
        $blue = [Drawing.Color]::FromArgb(32, 91, 165)
        $orange = [Drawing.Color]::FromArgb(247, 126, 24)
        $white = [Drawing.Color]::White
        $graphics.Clear($white)

        if ($IsCard) {
            $gradient = [Drawing.Drawing2D.LinearGradientBrush]::new(
                [Drawing.Rectangle]::new(0, 0, 1920, 1080), $navy, $blue, 0.0)
            $graphics.FillRectangle($gradient, 0, 0, 1920, 1080)
            $gradient.Dispose()
            $logo = [Drawing.Image]::FromFile($logoPath)
            try {
                $ratio = [Math]::Min(450.0 / $logo.Width, 230.0 / $logo.Height)
                $width = [int]($logo.Width * $ratio)
                $height = [int]($logo.Height * $ratio)
                $graphics.DrawImage($logo, [int]((1920 - $width) / 2), 190, $width, $height)
            }
            finally { $logo.Dispose() }
            $orangeBrush = [Drawing.SolidBrush]::new($orange)
            $whiteBrush = [Drawing.SolidBrush]::new($white)
            $titleFont = [Drawing.Font]::new("Segoe UI", 55, [Drawing.FontStyle]::Bold)
            $captionFont = [Drawing.Font]::new("Segoe UI", 26, [Drawing.FontStyle]::Regular)
            $center = [Drawing.StringFormat]::new()
            $center.Alignment = [Drawing.StringAlignment]::Center
            $graphics.DrawString($Title, $titleFont, $whiteBrush, [Drawing.RectangleF]::new(120, 520, 1680, 100), $center)
            $graphics.FillRectangle($orangeBrush, 710, 650, 500, 8)
            $graphics.DrawString($Caption, $captionFont, $whiteBrush, [Drawing.RectangleF]::new(180, 700, 1560, 130), $center)
            $center.Dispose(); $titleFont.Dispose(); $captionFont.Dispose(); $orangeBrush.Dispose(); $whiteBrush.Dispose()
        }
        else {
            $source = [Drawing.Image]::FromFile($SourceImage)
            try {
                $available = [Drawing.Rectangle]::new(0, 0, 1920, 965)
                $ratio = [Math]::Min($available.Width / $source.Width, $available.Height / $source.Height)
                $width = [int]($source.Width * $ratio)
                $height = [int]($source.Height * $ratio)
                $graphics.DrawImage($source, [int](($available.Width - $width) / 2), [int](($available.Height - $height) / 2), $width, $height)
            }
            finally { $source.Dispose() }
            $graphics.FillRectangle([Drawing.SolidBrush]::new($navy), 0, 965, 1920, 115)
            $orangeBrush = [Drawing.SolidBrush]::new($orange)
            $whiteBrush = [Drawing.SolidBrush]::new($white)
            $titleFont = [Drawing.Font]::new("Segoe UI", 26, [Drawing.FontStyle]::Bold)
            $captionFont = [Drawing.Font]::new("Segoe UI", 19, [Drawing.FontStyle]::Regular)
            $graphics.DrawString($Title, $titleFont, $orangeBrush, 54, 980)
            $graphics.DrawString($Caption, $captionFont, $whiteBrush, 54, 1025)
            $titleFont.Dispose(); $captionFont.Dispose(); $orangeBrush.Dispose(); $whiteBrush.Dispose()
        }
        $bitmap.Save($Path, [Drawing.Imaging.ImageFormat]::Png)
    }
    finally {
        $graphics.Dispose()
        $bitmap.Dispose()
    }
}

function Get-AudioDuration {
    param([string]$Path)
    $value = & $ffprobe -v error -show_entries format=duration -of default=noprint_wrappers=1:nokey=1 $Path
    if ($LASTEXITCODE -ne 0) { throw "Could not read narration duration for $Path" }
    return [double]::Parse(($value | Select-Object -First 1), [Globalization.CultureInfo]::InvariantCulture)
}

function Format-SrtTime {
    param([double]$Seconds)
    $span = [TimeSpan]::FromSeconds($Seconds)
    return "{0:00}:{1:00}:{2:00},{3:000}" -f [int]$span.TotalHours, $span.Minutes, $span.Seconds, $span.Milliseconds
}

$synth = [System.Speech.Synthesis.SpeechSynthesizer]::new()
$preferredVoice = $synth.GetInstalledVoices() |
    Where-Object { $_.Enabled -and $_.VoiceInfo.Name -match "Zira" } |
    Select-Object -First 1
if ($null -eq $preferredVoice) {
    $preferredVoice = $synth.GetInstalledVoices() | Where-Object Enabled | Select-Object -First 1
}
if ($null -eq $preferredVoice) { throw "No Windows narration voice is installed." }
$synth.SelectVoice($preferredVoice.VoiceInfo.Name)
$synth.Rate = -1
$synth.Volume = 100

$sceneFiles = [Collections.Generic.List[string]]::new()
$subtitleBlocks = [Collections.Generic.List[string]]::new()
$timeline = 0.0

try {
    for ($index = 0; $index -lt $scenes.Count; $index++) {
        $scene = $scenes[$index]
        $number = $index + 1
        $baseName = "scene-{0:00}" -f $number
        $canvasPath = Join-Path $workRoot "$baseName.png"
        $audioPath = Join-Path $workRoot "$baseName.wav"
        $videoPath = Join-Path $workRoot "$baseName.mp4"

        if ($index -eq 0 -or $index -eq ($scenes.Count - 1)) {
            New-Canvas -Path $canvasPath -Title $scene.Title -Caption $scene.Caption -IsCard
        }
        else {
            $sourcePath = Join-Path $captureRoot $scene.File
            if (-not (Test-Path -LiteralPath $sourcePath)) { throw "Missing demo capture: $sourcePath" }
            New-Canvas -Path $canvasPath -Title $scene.Title -Caption $scene.Caption -SourceImage $sourcePath
        }

        $synth.SetOutputToWaveFile($audioPath)
        $synth.Speak($scene.Narration)
        $synth.SetOutputToNull()

        $audioDuration = Get-AudioDuration -Path $audioPath
        $sceneDuration = $audioDuration + 1.4
        $fadeOut = [Math]::Max(0.2, $sceneDuration - 0.6)
        $durationText = $sceneDuration.ToString("0.###", [Globalization.CultureInfo]::InvariantCulture)
        $fadeText = $fadeOut.ToString("0.###", [Globalization.CultureInfo]::InvariantCulture)

        & $ffmpeg -hide_banner -loglevel error -y `
            -loop 1 -framerate 30 -i $canvasPath -i $audioPath `
            -filter_complex "[0:v]fade=t=in:st=0:d=0.4,fade=t=out:st=$fadeText`:d=0.6[v];[1:a]adelay=500|500,apad=pad_dur=1,afade=t=out:st=$fadeText`:d=0.6[a]" `
            -map "[v]" -map "[a]" -t $durationText `
            -c:v libx264 -preset veryfast -crf 18 -pix_fmt yuv420p `
            -c:a aac -b:a 160k -ar 48000 -movflags +faststart $videoPath
        if ($LASTEXITCODE -ne 0) { throw "FFmpeg failed while rendering $baseName." }

        $sceneFiles.Add($videoPath)
        $subtitleBlocks.Add("$number`r`n$(Format-SrtTime $timeline) --> $(Format-SrtTime ($timeline + $sceneDuration))`r`n$($scene.Narration)`r`n")
        $timeline += $sceneDuration
    }
}
finally {
    $synth.Dispose()
}

$concatFile = Join-Path $workRoot "concat.txt"
$sceneFiles | ForEach-Object { "file '$($_.Replace("'", "''"))'" } | Set-Content -LiteralPath $concatFile -Encoding utf8
$finalVideo = Join-Path $outputRoot "HISAB_KITAB_WORKS_Full_Client_Demo.mp4"
$subtitles = Join-Path $outputRoot "HISAB_KITAB_WORKS_Full_Client_Demo.srt"
$subtitleBlocks -join "`r`n" | Set-Content -LiteralPath $subtitles -Encoding utf8

& $ffmpeg -hide_banner -loglevel error -y -f concat -safe 0 -i $concatFile -c copy -movflags +faststart $finalVideo
if ($LASTEXITCODE -ne 0) { throw "FFmpeg could not assemble the final demo video." }

Write-Host "Demo video: $finalVideo"
Write-Host "Captions:   $subtitles"
