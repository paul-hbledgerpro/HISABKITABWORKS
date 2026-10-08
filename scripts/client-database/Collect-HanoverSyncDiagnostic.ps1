#requires -Version 5.1
[CmdletBinding()]
param(
    [string]$AppDataDirectory,
    [string]$DocumentsDirectory,
    [string]$AutomationDirectory,
    [string]$OutputDirectory,
    [string]$SqlInstance = '.\SQLEXPRESS',
    [switch]$SkipRuntime
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Security
Add-Type -AssemblyName System.Data
if (!$AppDataDirectory) { $AppDataDirectory = Join-Path $env:LOCALAPPDATA 'Hisab Kitab' }
if (!$DocumentsDirectory) { $DocumentsDirectory = [Environment]::GetFolderPath('MyDocuments') }
if (!$AutomationDirectory) { $AutomationDirectory = Join-Path $env:LOCALAPPDATA 'HisabKitabPOS' }
if (!$OutputDirectory) { $OutputDirectory = [Environment]::GetFolderPath('Desktop') }
if (!$OutputDirectory) { $OutputDirectory = $PSScriptRoot }
$runId = [DateTime]::Now.ToString('yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N').Substring(0,8)
$run = Join-Path $OutputDirectory ('HisabKitab-Hanover-Diagnostic-' + $runId)
[void][IO.Directory]::CreateDirectory($run)
$sourceOut = Join-Path $run 'Reports'
[void][IO.Directory]::CreateDirectory($sourceOut)
$issues = [Collections.Generic.List[object]]::new()
$secrets = [Collections.Generic.List[string]]::new()
function Issue($stage, $status) { $issues.Add([pscustomobject]@{Stage=$stage;Status=$status}) }
function Safe([string]$text) {
    foreach ($secret in ($secrets | Sort-Object Length -Descending)) { if ($secret) { $text = $text.Replace($secret,'[REDACTED]') } }
    $text = [regex]::Replace($text,'(?i)((?:password|pwd|token|secret)\s*[=:]\s*)[^\s;]+','$1[REDACTED]')
    return [regex]::Replace($text,'(?i)\b[A-Z0-9._%+-]+@[A-Z0-9.-]+\.[A-Z]{2,}\b','[EMAIL REDACTED]')
}
function ReadProtected($file, $entropy, $scope) {
    $bytes = $null
    try {
        $path = Join-Path $AppDataDirectory $file
        if (!(Test-Path -LiteralPath $path -PathType Leaf)) { Issue $file 'Missing'; return $null }
        $bytes = [Security.Cryptography.ProtectedData]::Unprotect([IO.File]::ReadAllBytes($path),[Text.Encoding]::UTF8.GetBytes($entropy),[Security.Cryptography.DataProtectionScope]::$scope)
        $value = [Text.Encoding]::UTF8.GetString($bytes) | ConvertFrom-Json
        Issue $file 'Readable'
        return $value
    } catch { Issue $file ('Unreadable: '+$_.Exception.GetType().Name); return $null }
    finally { if ($null -ne $bytes) { [Array]::Clear($bytes,0,$bytes.Length) } }
}
function SaveJson($name, $value) { ConvertTo-Json -InputObject $value -Depth 15 | Set-Content -LiteralPath (Join-Path $run $name) -Encoding UTF8 }
Write-Host 'Collecting Hanover sync evidence. This does not import, repair, or change settings.'
$portal = ReadProtected 'pos-portal-sync.protected' 'HISAB-KITAB-WORKS-POS-PORTAL-SYNC-V1' 'CurrentUser'
$licensed = ReadProtected 'licensed-businesses.protected' 'HISAB-KITAB-WORKS-LICENSED-BUSINESSES-V1' 'LocalMachine'
foreach ($store in @($portal.Stores)) {
    foreach ($field in @('PortalPassword','StorePassword','PortalEmail','StoreUserName')) { if ($store.$field) { $secrets.Add([string]$store.$field) } }
}
foreach ($business in @($licensed)) {
    foreach ($field in @('Password','Username','ConnectionString')) { if ($business.Connection.$field) { $secrets.Add([string]$business.Connection.$field) } }
}
$targets = @($portal.Stores | Where-Object { $_.BusinessName -match 'HANOVER' -or $_.PortalStoreName -match 'HANOVER' })
$businesses = @($licensed | Where-Object { $_.BusinessName -match 'HANOVER' })
if ($targets.Count -eq 0) { Issue 'Hanover setup' 'No Hanover sync setup found in this Windows account. Run as the account used by the client.' }
$safeSettings = @($targets | ForEach-Object {
    [ordered]@{Id=$_.Id;BusinessId=$_.BusinessId;BusinessName=$_.BusinessName;DatabaseName=$_.DatabaseName;StoreGuid=$_.StoreGuid;PortalStoreName=$_.PortalStoreName;Enabled=$_.Enabled;ZEnabled=$_.ZReportsEnabled;ZHour=$_.ZReportsDailyHour;ZMinute=$_.ZReportsDailyMinute;LastAttemptUtc=$_.LastZReportAttemptUtc;LastSuccessUtc=$_.LastZReportSuccessUtc;LastReportDate=$_.LastZReportDate;LastStatus=(Safe $_.LastZReportStatus);CashEnabled=$_.CashSalesSummaryEnabled;LastCashStatus=(Safe $_.LastCashSummaryStatus)}
})
SaveJson 'Setup.json' ([ordered]@{Computer=$env:COMPUTERNAME;WindowsUser=$env:USERNAME;CollectedUtc=[DateTime]::UtcNow.ToString('o');Configurations=$safeSettings;LicensedStores=@($businesses | Select-Object BusinessId,BusinessName,DatabaseName,StoreGuid)})
# No protected settings, passwords, connection strings or browser profile files are exported.
$licensed = $portal = $null
Write-Host 'Reading sync logs and previous backfill results...'
if ($secrets.Count -gt 0 -and $targets.Count -gt 0) {
    $log = Join-Path $AppDataDirectory 'Logs\pos-portal-sync.log'
    if (Test-Path -LiteralPath $log) { try { @(Get-Content -LiteralPath $log -Tail 5000 | Where-Object { $_ -match 'HANOVER|3428|3429|3451' } | ForEach-Object { Safe $_ }) | Set-Content -LiteralPath (Join-Path $run 'Sync.log') -Encoding UTF8 } catch { Issue 'Sync log' $_.Exception.GetType().Name } }
} else { Issue 'Sync log' 'Skipped: saved credentials unavailable for redaction.' }
$backfills = [Collections.Generic.List[object]]::new()
$backfillDir = Join-Path $AppDataDirectory 'Backfill Results'
if (Test-Path -LiteralPath $backfillDir) {
    foreach ($file in @(Get-ChildItem -LiteralPath $backfillDir -Filter '*.request.json' -File | Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 100)) {
        try {
            $request = Get-Content -LiteralPath $file.FullName -Raw | ConvertFrom-Json
            if ($request.BusinessName -notmatch 'HANOVER') { continue }
            $resultPath = $file.FullName -replace '\.request\.json$','.result.json'
            $result = $null
            if (Test-Path -LiteralPath $resultPath) { $result = Get-Content -LiteralPath $resultPath -Raw | ConvertFrom-Json }
            $backfills.Add([pscustomobject]@{Id=$request.Id;BusinessName=$request.BusinessName;Report=$request.ReportName;From=$request.From;Through=$request.Through;StartedUtc=$request.StartedUtc;CompletedUtc=$result.CompletedUtc;Success=$result.Success;Message=(Safe $result.Message)})
        } catch { Issue 'Backfill result' $_.Exception.GetType().Name }
    }
}
SaveJson 'Backfills.json' @($backfills.ToArray())
function Query($connection, [string]$sql) {
    $command=$connection.CreateCommand();$command.CommandText=$sql;$command.CommandTimeout=12;$reader=$null
    try {
        $reader=$command.ExecuteReader()
        while($reader.Read()) {
            $row=[ordered]@{}
            for($i=0;$i -lt $reader.FieldCount;$i++) {
                $value=if($reader.IsDBNull($i)){$null}else{$reader.GetValue($i)}
                if($value -is [DateTime]){$value=$value.ToString('yyyy-MM-ddTHH:mm:ss.fff')}
                if($value -is [string]){$value=Safe $value}
                $row[$reader.GetName($i)]=$value
            }
            [pscustomobject]$row
        }
    } finally {if($reader){$reader.Dispose()};$command.Dispose()}
}
$databases=@(@($targets.DatabaseName)+@($businesses.DatabaseName) | Where-Object {$_} | Sort-Object -Unique)
$dbResults=[Collections.Generic.List[object]]::new()
$reportPaths=[Collections.Generic.List[string]]::new()
Write-Host 'Reading recent shifts, pending drops and linked cash (SELECT only)...'
foreach($database in $databases) {
    $builder=[Data.SqlClient.SqlConnectionStringBuilder]::new();$builder['Data Source']=$SqlInstance;$builder['Initial Catalog']=[string]$database;$builder['Integrated Security']=$true;$builder['TrustServerCertificate']=$true;$builder['ApplicationIntent']='ReadOnly';$builder['Connect Timeout']=6
    $connection=[Data.SqlClient.SqlConnection]::new($builder.ConnectionString)
    $evidence=[ordered]@{Database=$database;Tables=[ordered]@{}}
    try {
        $connection.Open()
        # Small projections, bounded row counts, and short lock timeout. No schema initialization.
        $specs=@(
            @{Table='Stores';Fields='Id,Name,IsActive';Where='1=1';Order='Id'},
            @{Table='ShiftLogs';Fields='Id,StoreId,Date,ShiftNo,CashTotal,CashDropReceived,RegisterPayout,PayoutReason,PosReportKey,PosReportPath,PosReportStoreIdentity,PosSalesSummaryId,IsCorrection,CorrectsId,CreatedUtc,CreatedByName';Where="([Date]>='20260925' OR TRY_CONVERT(bigint,ShiftNo) BETWEEN 3428 AND 3455)";Order='Date DESC,Id DESC'},
            @{Table='PendingShiftDrops';Fields='Id,StoreId,ConfigurationId,PortalStoreName,Batch,Drop,Payout,Reason,Status,Message,ShiftId,Variance,CreatedUtc,LastAttemptUtc,NotifiedUtc';Where='1=1';Order='CreatedUtc DESC,Id DESC'},
            @{Table='CashOnHand';Fields='Id,StoreId,Date,Reference,CashAdded,IsPayout,PayoutAmount,IsCorrection,CorrectsId,CreatedUtc';Where="[Date]>='20260925' AND Reference LIKE 'SHIFTLOG:%'";Order='Date DESC,Id DESC'},
            @{Table='LedgerMonths';Fields='Id,StoreId,Month,IsClosed,ClosedUtc';Where='1=1';Order='Month DESC'},
            @{Table='PosSalesSummaries';Fields='Id,StoreId,ReportFrom,ReportTo,ReportedStoreName,SourceFileName,ImportedUtc';Where="ReportTo>='20260925'";Order='ReportTo DESC,Id DESC'}
        )
        foreach($spec in $specs) {
            try {
                $columns=@(Query $connection ("SELECT name FROM sys.columns WHERE object_id=OBJECT_ID(N'dbo."+$spec.Table+"')"))
                $available=@($columns | ForEach-Object {$_.name})
                if($available.Count -eq 0){$evidence.Tables[$spec.Table]=@{Status='Table missing'};continue}
                $projection=@($spec.Fields.Split(',') | Where-Object {$_ -in $available} | ForEach-Object {'['+$_+']'}) -join ','
                $rows=@(Query $connection ("SET LOCK_TIMEOUT 3000; SELECT TOP (500) "+$projection+" FROM dbo.["+$spec.Table+"] WHERE "+$spec.Where+" ORDER BY "+$spec.Order))
                $evidence.Tables[$spec.Table]=$rows
                if($spec.Table -eq 'ShiftLogs'){foreach($row in $rows){if($row.PosReportPath){$reportPaths.Add([string]$row.PosReportPath)}}}
            } catch { $evidence.Tables[$spec.Table]=@{Status='Query unavailable';ErrorType=$_.Exception.GetType().Name};Issue ($database+'/'+$spec.Table) $_.Exception.GetType().Name }
        }
    } catch { $evidence.Status='Database unavailable to this Windows account';Issue $database $_.Exception.GetType().Name }
    finally { $connection.Dispose() }
    $dbResults.Add([pscustomobject]$evidence)
}
SaveJson 'Database.json' @($dbResults.ToArray())
Write-Host 'Locating saved reports, including rejected batches...'
$roots=[Collections.Generic.List[string]]::new()
foreach($target in $targets) {
    $name=([string]$target.BusinessName).Trim()
    foreach($c in [IO.Path]::GetInvalidFileNameChars()){$name=$name.Replace([string]$c,'')}
    if($name -and $DocumentsDirectory){$roots.Add((Join-Path $DocumentsDirectory ($name+' Reports\Z Reports')))}
    $guid=[Guid]::Empty
    if([Guid]::TryParse([string]$target.Id,[ref]$guid)) {
        $id=$guid.ToString('N')
        $roots.Add((Join-Path $AutomationDirectory ('Downloads\'+$id)))
        $roots.Add((Join-Path $AutomationDirectory ('ReportFeeds\'+$id+'\Z Reports')))
        $roots.Add((Join-Path $AppDataDirectory ('POS Portal Sync\Downloads\'+$id)))
    }
}
if($DocumentsDirectory -and (Test-Path -LiteralPath $DocumentsDirectory)) {
    foreach($dir in @(Get-ChildItem -LiteralPath $DocumentsDirectory -Directory | Where-Object {$_.Name -match 'HANOVER.*Reports$'})){$roots.Add((Join-Path $dir.FullName 'Z Reports'))}
}
$sharedReports = Join-Path $AppDataDirectory 'POS Z Reports'
$roots.Add($sharedReports)
$manifest=[Collections.Generic.List[object]]::new();$seen=@{};$copied=0L;$fileCount=0
function CopyEvidence([IO.FileInfo]$file, [string]$origin) {
    if($seen.ContainsKey($file.FullName)){return};$seen[$file.FullName]=$true
    $entry=[ordered]@{OriginalPath=$file.FullName;Length=$file.Length;ModifiedUtc=$file.LastWriteTimeUtc.ToString('o');Source=$origin;Status='Skipped';File=$null;Sha256=$null}
    if(($file.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0){$entry.Status='Linked or cloud-placeholder file skipped'}
    elseif($file.Length -gt 12MB -or $script:copied+$file.Length -gt 70MB -or $script:fileCount -ge 80){$entry.Status='Collection size limit'}
    else {
        try {
            $name=('{0:D3}' -f $script:fileCount)+'-'+$file.Name
            $dest=Join-Path $sourceOut $name
            Copy-Item -LiteralPath $file.FullName -Destination $dest
            $entry.File='Reports/'+$name;$entry.Sha256=(Get-FileHash -LiteralPath $dest -Algorithm SHA256).Hash;$entry.Status='Copied'
            $script:copied+=$file.Length;$script:fileCount++
        }catch{$entry.Status=$_.Exception.GetType().Name}
    }
    $manifest.Add([pscustomobject]$entry)
}
# Traverse only designated report/download directories; never Chrome profiles or arbitrary DB paths.
$candidates=[Collections.Generic.List[object]]::new()
foreach($root in @($roots | Sort-Object -Unique)) {
    if(!(Test-Path -LiteralPath $root -PathType Container)){continue}
    $queue=[Collections.Generic.Queue[string]]::new();$queue.Enqueue($root);$visited=0
    while($queue.Count -gt 0 -and $visited -lt 1500) {
        $dir=$queue.Dequeue();$visited++
        try {
            $item=Get-Item -LiteralPath $dir
            if(($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0){Issue 'Report directory' ('Linked directory skipped: '+$dir);continue}
            foreach($child in @(Get-ChildItem -LiteralPath $dir)) {
                if($child.PSIsContainer){if(($child.Attributes -band [IO.FileAttributes]::ReparsePoint) -eq 0){$queue.Enqueue($child.FullName)};continue}
                if($child.Extension.ToLowerInvariant() -notin @('.pdf','.png')){continue}
                $match=[regex]::Match($child.BaseName,'(?:^|[^0-9])(34[0-9]{2})(?:[^0-9]|$)')
                $batch=if($match.Success){[int]$match.Groups[1].Value}else{0}
                if(($root -ne $sharedReports -and $batch -ge 3428 -and $batch -le 3455) -or $reportPaths.Contains($child.FullName)) {
                    $priority=if($batch -eq 3451){0}elseif($batch -eq 3428 -or $batch -eq 3429){1}else{2}
                    $candidates.Add([pscustomobject]@{File=$child;Root=$root;Priority=$priority})
                }
            }
        }catch{Issue 'Report enumeration' $_.Exception.GetType().Name}
    }
    if($queue.Count -gt 0){Issue 'Report enumeration' 'Directory scan limit reached'}
}
foreach($candidate in @($candidates | Sort-Object Priority,@{Expression={$_.File.LastWriteTimeUtc};Descending=$true})){CopyEvidence $candidate.File $candidate.Root}
SaveJson 'Report-Manifest.json' @($manifest.ToArray())
Write-Host 'Checking running versions and scheduled sync tasks...'
$runtime=[ordered]@{Processes=@();Tasks=@();InstalledVersion=$null}
$dll=Join-Path $env:LOCALAPPDATA 'Programs\HISAB KITAB WORKS\HISAB KITAB.dll'
if(Test-Path -LiteralPath $dll){$runtime.InstalledVersion=[Diagnostics.FileVersionInfo]::GetVersionInfo($dll).FileVersion}
function BinaryVersion([string]$exe) {try{if(!$exe){return $null};$dll=Join-Path ([IO.Path]::GetDirectoryName($exe)) 'HISAB KITAB.dll';if(Test-Path -LiteralPath $dll){return [Diagnostics.FileVersionInfo]::GetVersionInfo($dll).FileVersion}}catch{};return $null}
if(!$SkipRuntime) {
    try {$runtime.Processes=@(Get-CimInstance Win32_Process -Filter "Name='HISAB KITAB.exe'" -OperationTimeoutSec 10 | ForEach-Object {[ordered]@{Id=$_.ProcessId;SessionId=$_.SessionId;Executable=$_.ExecutablePath;Version=(BinaryVersion $_.ExecutablePath);Started=$_.CreationDate}})}catch{Issue 'Processes' $_.Exception.GetType().Name}
    try {$runtime.Tasks=@(Get-ScheduledTask -ErrorAction Stop | Where-Object {$_.TaskName -like 'HISAB KITAB*Sync*'} | ForEach-Object {
        $task=$_;$info=$null;try{$info=$task | Get-ScheduledTaskInfo}catch{}
        [ordered]@{Name=$task.TaskName;State=[string]$task.State;User=$task.Principal.UserId;LastRun=$info.LastRunTime;LastResult=$info.LastTaskResult;NextRun=$info.NextRunTime;Actions=@($task.Actions | ForEach-Object {@{Executable=$_.Execute;Version=(BinaryVersion $_.Execute)}})}
    })}catch{Issue 'Tasks' $_.Exception.GetType().Name}
}
SaveJson 'Runtime.json' $runtime
SaveJson 'Collection-Status.json' @($issues.ToArray())
$shifts=@($dbResults | ForEach-Object {$_.Tables.ShiftLogs} | Where-Object {$null -ne $_.Id})
$pending=@($dbResults | ForEach-Object {$_.Tables.PendingShiftDrops} | Where-Object {$_.Status -in @('Pending','Attention')})
$summary=@('HANOVER SYNC DIAGNOSTIC - READ ONLY',('Collected: '+[DateTime]::Now.ToString('s')),('Computer: '+$env:COMPUTERNAME),('Windows account: '+$env:USERNAME),('Installed version: '+$runtime.InstalledVersion),('Hanover sync configurations: '+$targets.Count),('Database connections examined: '+$databases.Count),('Recent shift rows collected (up to 500 per database): '+$shifts.Count),('Pending/attention drop rows collected: '+$pending.Count),('Report files copied: '+$fileCount),('Batch 3451 report files copied: '+@($manifest | Where-Object {$_.Status -eq 'Copied' -and $_.OriginalPath -match '(?<!\d)3451(?!\d)'}).Count),'','This package contains report images/PDFs and financial amounts needed to diagnose the failure.','Passwords, protected settings, connection strings and browser profiles are not included.','The collector did not import reports or change database rows, settings, schedules or app files.','Collection-Status.json lists missing/inaccessible evidence. This summary is not a root-cause conclusion.')
$summary | Set-Content -LiteralPath (Join-Path $run 'START-HERE.txt') -Encoding UTF8
$secrets.Clear();$targets=$businesses=$null
$zip=$run+'.zip'
Compress-Archive -Path (Join-Path $run '*') -DestinationPath $zip -CompressionLevel Optimal
Write-Host ''
Write-Host ('COMPLETE: '+$zip) -ForegroundColor Green
Write-Host 'Send this one ZIP file back in the support chat. No upload was performed.'
