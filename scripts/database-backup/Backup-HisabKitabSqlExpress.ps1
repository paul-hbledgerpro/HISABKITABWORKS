[CmdletBinding()]
param(
    [string]$SqlInstance = ".\SQLEXPRESS",

    [string]$Destination = (Join-Path ([Environment]::GetFolderPath("MyDocuments")) "HISAB KITAB Backups"),

    [string[]]$DatabaseName,

    [switch]$IncludeLicensingDatabase
)

$ErrorActionPreference = "Stop"

function Find-SqlPackage {
    $command = Get-Command SqlPackage.exe -ErrorAction SilentlyContinue
    if ($command) { return $command.Source }

    $roots = @(
        "${env:ProgramFiles}\Microsoft SQL Server",
        "${env:ProgramFiles(x86)}\Microsoft SQL Server",
        "${env:ProgramFiles}\Microsoft Visual Studio"
    ) | Where-Object { $_ -and (Test-Path -LiteralPath $_) }

    foreach ($root in $roots) {
        $candidate = Get-ChildItem -LiteralPath $root -Filter SqlPackage.exe -File -Recurse -ErrorAction SilentlyContinue |
            Select-Object -First 1
        if ($candidate) { return $candidate.FullName }
    }

    throw "SqlPackage.exe was not found. Install Microsoft SqlPackage before creating BACPAC backups."
}

function Get-LocalDatabaseNames([string]$instance) {
    Add-Type -AssemblyName System.Data
    $builder = New-Object System.Data.SqlClient.SqlConnectionStringBuilder
    $builder["Data Source"] = $instance
    $builder["Initial Catalog"] = "master"
    $builder["Integrated Security"] = $true
    $builder["Encrypt"] = $true
    $builder["TrustServerCertificate"] = $true
    $connection = New-Object System.Data.SqlClient.SqlConnection $builder.ConnectionString
    try {
        $connection.Open()
        $command = $connection.CreateCommand()
        $command.CommandText = "SELECT name FROM sys.databases WHERE database_id>4 AND state_desc='ONLINE' ORDER BY name"
        $reader = $command.ExecuteReader()
        $names = [Collections.Generic.List[string]]::new()
        while ($reader.Read()) { $names.Add($reader.GetString(0)) }
        return $names
    }
    finally {
        $connection.Dispose()
    }
}

$sqlPackage = Find-SqlPackage
$timestamp = Get-Date -Format "yyyyMMdd-HHmmss"
$backupFolder = Join-Path $Destination $timestamp
New-Item -ItemType Directory -Path $backupFolder -Force | Out-Null

$databases = if ($DatabaseName) { $DatabaseName } else { Get-LocalDatabaseNames $SqlInstance }
if (-not $IncludeLicensingDatabase) {
    $databases = $databases | Where-Object { $_ -ne "HBLedgerPro_Licensing" }
}
if (-not $databases) { throw "No eligible online user databases were found on '$SqlInstance'." }

$manifest = [Collections.Generic.List[object]]::new()
foreach ($database in $databases) {
    $safeFileName = ($database -replace '[<>:"/\\|?*]', '_')
    $target = Join-Path $backupFolder "$safeFileName-$timestamp.bacpac"
    $connectionString = "Server=$SqlInstance;Initial Catalog=$database;Integrated Security=True;Encrypt=True;TrustServerCertificate=True"

    Write-Host "Exporting [$database]..."
    & $sqlPackage /Action:Export "/SourceConnectionString:$connectionString" "/TargetFile:$target" /p:CommandTimeout=1200
    if ($LASTEXITCODE -ne 0) { throw "SqlPackage export failed for '$database' with exit code $LASTEXITCODE." }

    $hash = Get-FileHash -LiteralPath $target -Algorithm SHA256
    $manifest.Add([pscustomobject]@{
        Database = $database
        File = [IO.Path]::GetFileName($target)
        Bytes = (Get-Item -LiteralPath $target).Length
        SHA256 = $hash.Hash
        CreatedUtc = [DateTime]::UtcNow.ToString("o")
    })
}

$manifestPath = Join-Path $backupFolder "manifest.csv"
$manifest | Export-Csv -LiteralPath $manifestPath -NoTypeInformation
Write-Host "Backup completed: $backupFolder"
Write-Host "No older backups were deleted."
