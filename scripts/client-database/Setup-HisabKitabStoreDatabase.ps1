[CmdletBinding()]
param(
    [ValidateSet("Host", "VerifyWorkstation")]
    [string]$Mode = "Host",

    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[A-Za-z0-9 _-]+$')]
    [string]$DatabaseName,

    [string]$SqlInstance = ".\SQLEXPRESS",

    [string]$BacpacPath,

    [System.Management.Automation.PSCredential]$SqlCredential,

    [string]$OutputFolder = (Join-Path ([Environment]::GetFolderPath("MyDocuments")) "HISAB KITAB Client Setup")
)

$ErrorActionPreference = "Stop"

function New-SqlConnectionString {
    param(
        [string]$Server,
        [string]$Database = "master"
    )

    $builder = New-Object System.Data.SqlClient.SqlConnectionStringBuilder
    $builder.DataSource = $Server
    $builder.InitialCatalog = $Database
    $builder.Encrypt = $true
    $builder.TrustServerCertificate = $true
    $builder.ConnectTimeout = 15

    if ($null -eq $SqlCredential) {
        $builder.IntegratedSecurity = $true
    }
    else {
        $builder.IntegratedSecurity = $false
        $builder.UserID = $SqlCredential.UserName
        $builder.Password = $SqlCredential.GetNetworkCredential().Password
    }

    return $builder.ConnectionString
}

function Invoke-SqlScalar {
    param(
        [string]$ConnectionString,
        [string]$CommandText,
        [hashtable]$Parameters = @{}
    )

    $connection = New-Object System.Data.SqlClient.SqlConnection($ConnectionString)
    try {
        $connection.Open()
        $command = $connection.CreateCommand()
        $command.CommandText = $CommandText
        foreach ($name in $Parameters.Keys) {
            [void]$command.Parameters.AddWithValue($name, $Parameters[$name])
        }
        return $command.ExecuteScalar()
    }
    finally {
        $connection.Dispose()
    }
}

function Find-SqlPackage {
    $command = Get-Command SqlPackage.exe -ErrorAction SilentlyContinue
    if ($null -ne $command) {
        return $command.Source
    }

    $candidates = @(
        "$env:ProgramFiles\Microsoft SQL Server\170\DAC\bin\SqlPackage.exe",
        "$env:ProgramFiles\Microsoft SQL Server\160\DAC\bin\SqlPackage.exe",
        "$env:ProgramFiles\Microsoft SQL Server\150\DAC\bin\SqlPackage.exe",
        "$env:ProgramFiles\Microsoft SQL Server Management Studio 20\Common7\IDE\Extensions\Microsoft\SQLDB\DAC\SqlPackage.exe"
    )

    return $candidates |
        Where-Object { Test-Path -LiteralPath $_ } |
        Select-Object -First 1
}

Write-Host "Checking SQL Server: $SqlInstance" -ForegroundColor Cyan
$masterConnection = New-SqlConnectionString -Server $SqlInstance
[void](Invoke-SqlScalar -ConnectionString $masterConnection -CommandText "SELECT 1;")

$databaseExists = [int](Invoke-SqlScalar `
    -ConnectionString $masterConnection `
    -CommandText "SELECT COUNT(*) FROM sys.databases WHERE name = @name;" `
    -Parameters @{ "@name" = $DatabaseName }) -gt 0

if ($Mode -eq "Host" -and -not $databaseExists) {
    if (-not [string]::IsNullOrWhiteSpace($BacpacPath)) {
        $resolvedBacpac = (Resolve-Path -LiteralPath $BacpacPath).Path
        $sqlPackage = Find-SqlPackage
        if ([string]::IsNullOrWhiteSpace($sqlPackage)) {
            throw "SqlPackage.exe was not found. Install SQL Server Management Studio or the Microsoft SqlPackage tool."
        }

        Write-Host "Importing $resolvedBacpac into $DatabaseName..." -ForegroundColor Yellow
        $targetConnection = New-SqlConnectionString -Server $SqlInstance -Database $DatabaseName
        & $sqlPackage `
            /Action:Import `
            "/SourceFile:$resolvedBacpac" `
            "/TargetConnectionString:$targetConnection" `
            /p:CommandTimeout=1200
        if ($LASTEXITCODE -ne 0) {
            throw "SqlPackage import failed with exit code $LASTEXITCODE."
        }
    }
    else {
        $escapedDatabaseName = $DatabaseName.Replace("]", "]]")
        Write-Host "Creating empty database $DatabaseName..." -ForegroundColor Yellow
        [void](Invoke-SqlScalar `
            -ConnectionString $masterConnection `
            -CommandText "CREATE DATABASE [$escapedDatabaseName]; SELECT 1;")
    }
}
elseif ($Mode -eq "Host" -and $databaseExists) {
    Write-Host "Database already exists. It was not overwritten." -ForegroundColor Yellow
}
elseif ($Mode -eq "VerifyWorkstation" -and -not $databaseExists) {
    throw "Database '$DatabaseName' was not found on '$SqlInstance'. Check the host name, SQL Express network settings, firewall, and database name."
}

$storeConnection = New-SqlConnectionString -Server $SqlInstance -Database $DatabaseName
$tableCount = [int](Invoke-SqlScalar `
    -ConnectionString $storeConnection `
    -CommandText "SELECT COUNT(*) FROM sys.tables;")

$rowCount = [long](Invoke-SqlScalar `
    -ConnectionString $storeConnection `
    -CommandText @"
SELECT COALESCE(SUM(row_count), 0)
FROM sys.dm_db_partition_stats
WHERE index_id IN (0, 1);
"@)

New-Item -ItemType Directory -Path $OutputFolder -Force | Out-Null
$handoffPath = Join-Path $OutputFolder "$($DatabaseName)_connection.json"
[ordered]@{
    Server = $SqlInstance
    Database = $DatabaseName
    Authentication = if ($null -eq $SqlCredential) { "Windows" } else { "SQL" }
    HostComputer = $env:COMPUTERNAME
    SetupMode = $Mode
    VerifiedUtc = [DateTime]::UtcNow.ToString("O")
    TableCount = $tableCount
    ApproximateRowCount = $rowCount
} | ConvertTo-Json | Set-Content -LiteralPath $handoffPath -Encoding UTF8

Write-Host ""
Write-Host "HISAB KITAB database setup verified." -ForegroundColor Green
Write-Host "Server:   $SqlInstance"
Write-Host "Database: $DatabaseName"
Write-Host "Tables:   $tableCount"
Write-Host "Rows:     $rowCount"
Write-Host "Handoff:  $handoffPath"
Write-Host ""
Write-Host "No password was written to the handoff file." -ForegroundColor DarkGreen
