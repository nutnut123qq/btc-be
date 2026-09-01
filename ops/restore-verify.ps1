param(
    [Parameter(Mandatory)][string]$BackupPath,
    [ValidateSet("Split", "Full", "ListOnly")][string]$Mode = "Split",
    [ValidateRange(1, 3600)][int]$ListTimeoutSeconds = 300,
    [ValidateRange(1, 3600)][int]$CreateTimeoutSeconds = 120,
    [ValidateRange(1, 86400)][int]$RestoreTimeoutSeconds = 7200,
    [ValidateRange(1, 86400)][int]$CountTimeoutSeconds = 1800,
    [ValidateRange(1, 3600)][int]$DropTimeoutSeconds = 120
)
. "$PSScriptRoot/common.ps1"
Initialize-OpsDirectories
$postgres = Assert-NativePg17

$dumpPath = [IO.Path]::GetFullPath($BackupPath)
if (-not (Test-Path -LiteralPath $dumpPath)) { throw "Backup not found: $dumpPath" }
$manifestPath = Join-Path (Split-Path $dumpPath -Parent) "$([IO.Path]::GetFileNameWithoutExtension($dumpPath)).manifest.json"
if (-not (Test-Path -LiteralPath $manifestPath)) { throw "Backup manifest not found: $manifestPath" }
$manifestChecksumPath = "$manifestPath.sha256"
if (-not (Test-Path -LiteralPath $manifestChecksumPath)) { throw "Backup manifest checksum not found: $manifestChecksumPath" }
$expectedManifestHash = ((Get-Content -LiteralPath $manifestChecksumPath -Raw).Trim() -split '\s+')[0].ToLowerInvariant()
if ((Get-FileHash -LiteralPath $manifestPath -Algorithm SHA256).Hash.ToLowerInvariant() -ne $expectedManifestHash) {
    throw "Backup manifest checksum mismatch."
}
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
$dumpChecksumPath = "$dumpPath.sha256"
if (-not (Test-Path -LiteralPath $dumpChecksumPath)) { throw "Database dump checksum file not found: $dumpChecksumPath" }
$checksumFileHash = ((Get-Content -LiteralPath $dumpChecksumPath -Raw).Trim() -split '\s+')[0].ToLowerInvariant()
$actualHash = (Get-FileHash -LiteralPath $dumpPath -Algorithm SHA256).Hash.ToLowerInvariant()
if ($actualHash -ne $manifest.dump.sha256 -or $actualHash -ne $checksumFileHash) { throw "Database dump checksum mismatch." }

$pgRestore = Resolve-PgTool "pg_restore"
$listResult = Invoke-BoundedProcess -FilePath $pgRestore -ArgumentList @("--list", $dumpPath) -TimeoutSeconds $ListTimeoutSeconds
if ($listResult.ExitCode -ne 0) { throw "pg_restore could not list the backup. $($listResult.Error)" }

if ($manifest.models.archive) {
    $archive = Join-Path (Split-Path $dumpPath -Parent) $manifest.models.archive
    if (-not (Test-Path -LiteralPath $archive)) { throw "Model archive missing: $archive" }
    if ((Get-FileHash $archive -Algorithm SHA256).Hash.ToLowerInvariant() -ne $manifest.models.sha256) {
        throw "Model archive checksum mismatch."
    }
    Add-Type -AssemblyName System.IO.Compression
    $zip = [IO.Compression.ZipFile]::OpenRead($archive)
    try {
        $entries = @($zip.Entries | ForEach-Object Name)
        foreach ($model in $manifest.models.files) {
            $entry = $zip.GetEntry($model.name)
            if ($null -eq $entry) { throw "Model artifact missing from archive: $($model.name)" }
            $stream = $entry.Open()
            $sha = [Security.Cryptography.SHA256]::Create()
            try { $entryHash = [Convert]::ToHexString($sha.ComputeHash($stream)).ToLowerInvariant() }
            finally { $sha.Dispose(); $stream.Dispose() }
            if ($entryHash -ne $model.sha256) { throw "Model artifact checksum mismatch: $($model.name)" }
        }
        foreach ($artifact in @($entries | Where-Object { $_ -like "*.joblib" })) {
            if ([IO.Path]::ChangeExtension($artifact, ".json") -notin $entries) {
                throw "Model manifest missing for artifact: $artifact"
            }
        }
    }
    finally { $zip.Dispose() }
}
if ($Mode -eq "ListOnly") { Write-Host "Backup and model archive checksums/listing are valid."; return }

$createdb = Resolve-PgTool "createdb"
$dropdb = Resolve-PgTool "dropdb"
$serverArgs = @(Get-PgServerArgs)
$suffix = "$([DateTime]::UtcNow.ToString('yyyyMMddHHmmss'))_$([Guid]::NewGuid().ToString('N').Substring(0,8))".ToLowerInvariant()
$schemaDb = "btc_restore_verify_schema_$suffix"
$dataDb = "btc_restore_verify_data_$suffix"
$databases = if ($Mode -eq "Full") { @($dataDb) } else { @($schemaDb, $dataDb) }
if ($databases | Where-Object { $_ -notmatch '^btc_restore_verify_(schema|data)_\d{14}_[a-f0-9]{8}$' -or $_ -eq $postgres.database }) {
    throw "Unsafe verification database name."
}

$createdDatabases = [Collections.Generic.List[string]]::new()
$restoreError = $null
$cleanupErrors = [Collections.Generic.List[string]]::new()
try {
    foreach ($database in $databases) {
        $createResult = Invoke-BoundedProcess -FilePath $createdb -ArgumentList ($serverArgs + @($database)) -TimeoutSeconds $CreateTimeoutSeconds
        if ($createResult.ExitCode -ne 0) { throw "Could not create verification database $database. $($createResult.Error)" }
        $createdDatabases.Add($database)
    }
    if ($Mode -eq "Split") {
        $schemaResult = Invoke-BoundedProcess -FilePath $pgRestore -ArgumentList ($serverArgs + @("--dbname", $schemaDb, "--schema-only", "--no-owner", "--no-privileges", $dumpPath)) -TimeoutSeconds $RestoreTimeoutSeconds
        if ($schemaResult.ExitCode -ne 0) { throw "Schema-only restore failed. $($schemaResult.Error)" }
        $preDataResult = Invoke-BoundedProcess -FilePath $pgRestore -ArgumentList ($serverArgs + @("--dbname", $dataDb, "--section", "pre-data", "--no-owner", "--no-privileges", $dumpPath)) -TimeoutSeconds $RestoreTimeoutSeconds
        if ($preDataResult.ExitCode -ne 0) { throw "Pre-data restore failed. $($preDataResult.Error)" }
        $dataResult = Invoke-BoundedProcess -FilePath $pgRestore -ArgumentList ($serverArgs + @("--dbname", $dataDb, "--section", "data", "--no-owner", "--no-privileges", $dumpPath)) -TimeoutSeconds $RestoreTimeoutSeconds
        if ($dataResult.ExitCode -ne 0) { throw "Data restore failed. $($dataResult.Error)" }
    }
    else {
        $fullResult = Invoke-BoundedProcess -FilePath $pgRestore -ArgumentList ($serverArgs + @("--dbname", $dataDb, "--no-owner", "--no-privileges", $dumpPath)) -TimeoutSeconds $RestoreTimeoutSeconds
        if ($fullResult.ExitCode -ne 0) { throw "Full restore failed. $($fullResult.Error)" }
    }

    $actualCounts = Get-PgTableCounts -Database $dataDb -TimeoutSeconds $CountTimeoutSeconds
    foreach ($property in $manifest.rowCounts.psobject.Properties) {
        if (-not $actualCounts.Contains($property.Name) -or $actualCounts[$property.Name] -ne [long]$property.Value) {
            throw "Row-count mismatch for $($property.Name): expected $($property.Value), actual $($actualCounts[$property.Name])."
        }
    }
    if ($actualCounts.Count -ne @($manifest.rowCounts.psobject.Properties).Count) {
        throw "Restored table count differs from the backup manifest."
    }
    if ($Mode -eq "Split") {
        Write-Host "Split logical restore passed: schema objects and $($actualCounts.Count) data-table counts reconciled separately. Post-data indexes/FKs were not built against restored data."
    }
    else { Write-Host "Full restore verification passed: $($actualCounts.Count) tables reconciled." }
}
catch { $restoreError = $_ }
finally {
    $cleanupTargets = $createdDatabases.ToArray()
    [Array]::Reverse($cleanupTargets)
    foreach ($database in $cleanupTargets) {
        try {
            $dropResult = Invoke-BoundedProcess -FilePath $dropdb -ArgumentList ($serverArgs + @("--force", $database)) -TimeoutSeconds $DropTimeoutSeconds
            if ($dropResult.ExitCode -ne 0) { $cleanupErrors.Add("Could not drop verification database $database. $($dropResult.Error)") }
        }
        catch { $cleanupErrors.Add("Could not drop verification database $database. $($_.Exception.Message)") }
    }
}
if ($restoreError) {
    foreach ($cleanupError in $cleanupErrors) { Write-Warning $cleanupError }
    throw $restoreError
}
if ($cleanupErrors.Count -gt 0) { throw ($cleanupErrors -join " ") }
