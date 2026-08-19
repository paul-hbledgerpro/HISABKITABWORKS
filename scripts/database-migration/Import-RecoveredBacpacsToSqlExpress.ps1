[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$Source,

    [string]$SqlInstance = ".\SQLEXPRESS",

    [string]$OutputFolder = (Join-Path ([Environment]::GetFolderPath("MyDocuments")) "HISAB KITAB Migration"),

    [switch]$Import,

    [switch]$SelectLatestDuplicate,

    [switch]$SkipExisting
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

    throw "SqlPackage.exe was not found. Install Microsoft SqlPackage before importing BACPAC files."
}

function Get-CanonicalDatabaseName([string]$bacpacPath) {
    $name = [IO.Path]::GetFileNameWithoutExtension($bacpacPath)
    # Azure recovery exports append timestamps such as _2026-07-23T23-49Z.
    return ($name -replace "_\d{4}-\d{2}-\d{2}T\d{2}[-:]\d{2}.*$", "").Trim()
}

function Get-ExistingDatabaseNames([string]$instance) {
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
        $command.CommandText = "SELECT name FROM sys.databases"
        $reader = $command.ExecuteReader()
        $names = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        while ($reader.Read()) { [void]$names.Add($reader.GetString(0)) }
        # Prevent PowerShell from enumerating the HashSet into a fixed-size
        # Object[] when returning it to the caller.
        return ,$names
    }
    finally {
        $connection.Dispose()
    }
}

function Get-DatabaseVerification([string]$instance, [string]$databaseName) {
    Add-Type -AssemblyName System.Data
    $builder = New-Object System.Data.SqlClient.SqlConnectionStringBuilder
    $builder["Data Source"] = $instance
    $builder["Initial Catalog"] = $databaseName
    $builder["Integrated Security"] = $true
    $builder["Encrypt"] = $true
    $builder["TrustServerCertificate"] = $true
    $connection = New-Object System.Data.SqlClient.SqlConnection $builder.ConnectionString
    try {
        $connection.Open()
        $command = $connection.CreateCommand()
        $command.CommandText = @"
SELECT COUNT_BIG(*) AS TableCount,
       COALESCE(SUM(CONVERT(bigint, p.rows)), 0) AS ApproximateRowCount
FROM sys.tables t
LEFT JOIN sys.partitions p
  ON p.object_id=t.object_id AND p.index_id IN (0,1);
"@
        $reader = $command.ExecuteReader()
        [void]$reader.Read()
        return [pscustomobject]@{
            Database = $databaseName
            TableCount = $reader.GetInt64(0)
            ApproximateRowCount = $reader.GetInt64(1)
            VerifiedUtc = [DateTime]::UtcNow.ToString("o")
        }
    }
    finally {
        $connection.Dispose()
    }
}

$resolvedSource = (Resolve-Path -LiteralPath $Source).Path
New-Item -ItemType Directory -Path $OutputFolder -Force | Out-Null

$workingFolder = $resolvedSource
$temporaryFolder = $null
if ([IO.Path]::GetExtension($resolvedSource) -ieq ".zip") {
    $temporaryFolder = Join-Path ([IO.Path]::GetTempPath()) ("HisabKitab-Bacpac-" + [Guid]::NewGuid().ToString("N"))
    New-Item -ItemType Directory -Path $temporaryFolder | Out-Null
    Expand-Archive -LiteralPath $resolvedSource -DestinationPath $temporaryFolder
    $workingFolder = $temporaryFolder
}

try {
    $packages = Get-ChildItem -LiteralPath $workingFolder -Filter *.bacpac -File -Recurse |
        ForEach-Object {
            [pscustomobject]@{
                Path = $_.FullName
                FileName = $_.Name
                Database = Get-CanonicalDatabaseName $_.FullName
                LastWriteTimeUtc = $_.LastWriteTimeUtc
            }
        }

    if (-not $packages) { throw "No BACPAC files were found in '$Source'." }

    $duplicates = $packages | Group-Object Database | Where-Object Count -gt 1
    if ($duplicates -and -not $SelectLatestDuplicate) {
        $names = ($duplicates.Name -join ", ")
        throw "Multiple BACPAC files map to the same database: $names. Review them, then rerun with -SelectLatestDuplicate to use the newest file."
    }

    if ($SelectLatestDuplicate) {
        $packages = $packages | Group-Object Database | ForEach-Object {
            $_.Group | Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1
        }
    }

    $inventoryPath = Join-Path $OutputFolder ("bacpac-inventory-{0}.csv" -f (Get-Date -Format "yyyyMMdd-HHmmss"))
    $packages | Sort-Object Database | Export-Csv -LiteralPath $inventoryPath -NoTypeInformation
    $packages | Sort-Object Database | Format-Table Database, FileName, LastWriteTimeUtc -AutoSize
    Write-Host "Inventory saved to $inventoryPath"

    if (-not $Import) {
        Write-Host "Inventory only. No database was changed. Rerun with -Import after reviewing the names."
        return
    }

    $sqlPackage = Find-SqlPackage
    $existing = Get-ExistingDatabaseNames $SqlInstance
    $verification = [Collections.Generic.List[object]]::new()

    foreach ($package in ($packages | Sort-Object Database)) {
        if ($existing.Contains($package.Database)) {
            if ($SkipExisting) {
                Write-Warning "Skipping existing database '$($package.Database)'. No data was overwritten."
                continue
            }

            throw "Refusing to overwrite existing database '$($package.Database)'. Rename or remove it manually only after verifying a backup."
        }

        Write-Host "Importing $($package.FileName) as [$($package.Database)]..."
        $connectionString = "Server=$SqlInstance;Initial Catalog=$($package.Database);Integrated Security=True;Encrypt=True;TrustServerCertificate=True"
        & $sqlPackage /Action:Import "/SourceFile:$($package.Path)" "/TargetConnectionString:$connectionString" /p:CommandTimeout=1200
        if ($LASTEXITCODE -ne 0) { throw "SqlPackage import failed for '$($package.FileName)' with exit code $LASTEXITCODE." }

        [void]$existing.Add($package.Database)
        $verification.Add((Get-DatabaseVerification $SqlInstance $package.Database))
    }

    $verificationPath = Join-Path $OutputFolder ("import-verification-{0}.csv" -f (Get-Date -Format "yyyyMMdd-HHmmss"))
    $verification | Export-Csv -LiteralPath $verificationPath -NoTypeInformation
    Write-Host "Imports completed. Verification summary: $verificationPath"
    Write-Warning "Do not delete Azure recovery sources until business totals and critical table counts have been compared."
}
finally {
    if ($temporaryFolder -and (Test-Path -LiteralPath $temporaryFolder)) {
        Remove-Item -LiteralPath $temporaryFolder -Recurse -Force
    }
}
