param([switch]$KeepArtifacts)
. "$PSScriptRoot/common.ps1"
Initialize-OpsDirectories

$sourceDatabase = $env:PGDATABASE
if ([string]::IsNullOrWhiteSpace($sourceDatabase)) { throw "PGDATABASE is required." }
Assert-NativePg17 | Out-Null
$createdb = Resolve-PgTool "createdb"
$dropdb = Resolve-PgTool "dropdb"
$psql = Resolve-PgTool "psql"
$runId = [Guid]::NewGuid().ToString("N").Substring(0, 8)
$database = "btc_ops_drill_$([DateTime]::UtcNow.ToString('yyyyMMddHHmmss'))_$runId"
if ($database -notmatch '^btc_ops_drill_\d{14}_[a-f0-9]{8}$') { throw "Unsafe drill database name." }
$drillDirectory = [IO.Path]::GetFullPath((Join-Path $script:RuntimeDir "drill-$runId"))
if (-not $drillDirectory.StartsWith($script:RuntimeDir + [IO.Path]::DirectorySeparatorChar)) {
    throw "Unsafe drill output directory."
}

$created = $false
$primaryError = $null
try {
    $create = Invoke-BoundedProcess $createdb (@(Get-PgServerArgs) + @($database)) 30
    if ($create.ExitCode -ne 0) { throw "Could not create ops drill database. $($create.Error)" }
    $created = $true
    $env:PGDATABASE = $database
    $seedSql = 'CREATE TABLE "DrillRows" ("Id" integer PRIMARY KEY, "Value" text NOT NULL); INSERT INTO "DrillRows" VALUES (1, ''ok'');'
    $seed = Invoke-BoundedProcess $psql (@(Get-PgConnectionArgs) + @("--no-psqlrc", "--set", "ON_ERROR_STOP=1", "--command", $seedSql)) 30
    if ($seed.ExitCode -ne 0) { throw "Could not seed ops drill database. $($seed.Error)" }

    & "$PSScriptRoot/backup.ps1" -OutputDirectory $drillDirectory -RetentionDays 1 -SkipModels -CountTimeoutSeconds 30 -DumpTimeoutSeconds 60
    $dumps = @(Get-ChildItem -LiteralPath $drillDirectory -Filter "*.dump")
    if ($dumps.Count -ne 1) { throw "Ops drill expected exactly one generated dump, found $($dumps.Count)." }
    & "$PSScriptRoot/restore-verify.ps1" -BackupPath $dumps[0].FullName -Mode ListOnly
    & "$PSScriptRoot/restore-verify.ps1" -BackupPath $dumps[0].FullName -Mode Split
    & "$PSScriptRoot/restore-verify.ps1" -BackupPath $dumps[0].FullName -Mode Full
}
catch { $primaryError = $_ }
finally {
    $env:PGDATABASE = $sourceDatabase
    if ($created) {
        try {
            $drop = Invoke-BoundedProcess $dropdb (@(Get-PgServerArgs) + @("--if-exists", "--force", $database)) 30
            if ($drop.ExitCode -ne 0) { throw "Could not drop ops drill database: $database. $($drop.Error)" }
        }
        catch {
            if ($primaryError) { Write-Warning "Drill cleanup also failed: $($_.Exception.Message)" }
            else { $primaryError = $_ }
        }
    }
    if (-not $KeepArtifacts -and (Test-Path -LiteralPath $drillDirectory)) {
        try {
            Get-ChildItem -LiteralPath $drillDirectory -File | Remove-Item -Force
            Remove-Item -LiteralPath $drillDirectory -Force
        }
        catch {
            if ($primaryError) { Write-Warning "Artifact cleanup also failed: $($_.Exception.Message)" }
            else { $primaryError = $_ }
        }
    }
}
if ($primaryError) { throw $primaryError }
Write-Host "Ops backup/restore self-test passed."
