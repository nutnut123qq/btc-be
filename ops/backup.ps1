param(
    [string]$OutputDirectory = (Join-Path $PSScriptRoot "../.ops/backups"),
    [int]$RetentionDays = 30,
    [int]$CountTimeoutSeconds = 1800,
    [int]$DumpTimeoutSeconds = 7200,
    [switch]$SkipModels
)
. "$PSScriptRoot/common.ps1"
Initialize-OpsDirectories
Import-OpsSecrets

if ([string]::IsNullOrWhiteSpace($env:PGDATABASE)) { throw "PGDATABASE is required." }
$postgres = Assert-NativePg17
$output = Assert-SafeDataDirectory $OutputDirectory
New-Item -ItemType Directory -Force -Path $output | Out-Null
$pgDump = Resolve-PgTool "pg_dump"
$psql = Resolve-PgTool "psql"
$stamp = [DateTime]::UtcNow.ToString("yyyyMMddTHHmmssZ")
$runId = [Guid]::NewGuid().ToString("N").Substring(0, 8)
$baseName = "bitcoin_analyst_${stamp}_$runId"
$dumpPath = Join-Path $output "$baseName.dump"
$dumpChecksumPath = "$dumpPath.sha256"
$manifestPath = Join-Path $output "$baseName.manifest.json"
$manifestChecksumPath = "$manifestPath.sha256"
$modelArchive = Join-Path $output "$baseName.models.zip"
$generatedPaths = @($dumpPath, $dumpChecksumPath, $manifestPath, $manifestChecksumPath, $modelArchive)

function Remove-PartialBackup {
    foreach ($path in $generatedPaths) {
        Remove-Item -LiteralPath $path -Force -ErrorAction SilentlyContinue
    }
}

try {
    $keeperInfo = [Diagnostics.ProcessStartInfo]::new()
    $keeperInfo.FileName = $psql
    $keeperInfo.UseShellExecute = $false
    $keeperInfo.RedirectStandardInput = $true
    $keeperInfo.RedirectStandardOutput = $true
    $keeperInfo.RedirectStandardError = $true
    foreach ($argument in @((Get-PgConnectionArgs) + @("--no-psqlrc", "--quiet", "--tuples-only", "--no-align"))) {
        $keeperInfo.ArgumentList.Add($argument)
    }

    $keeper = [Diagnostics.Process]::Start($keeperInfo)
    $snapshotError = $null
    try {
        $keeper.StandardInput.WriteLine("BEGIN ISOLATION LEVEL REPEATABLE READ;")
        $keeper.StandardInput.WriteLine("SELECT pg_export_snapshot();")
        $keeper.StandardInput.Flush()
        $snapshotTask = $keeper.StandardOutput.ReadLineAsync()
        if (-not $snapshotTask.Wait([TimeSpan]::FromSeconds(15))) { throw "Timed out exporting PostgreSQL snapshot." }
        $snapshot = $snapshotTask.Result
        if ($null -eq $snapshot) { throw "PostgreSQL snapshot keeper exited before returning a snapshot." }
        $snapshot = $snapshot.Trim()
        if ($snapshot -notmatch '^[0-9A-Fa-f]+-[0-9A-Fa-f]+-\d+$') {
            throw "Could not export a PostgreSQL snapshot."
        }

        $rowCounts = Get-PgTableCounts $env:PGDATABASE $snapshot $CountTimeoutSeconds
        $dumpArgs = @(Get-PgConnectionArgs) + @(
            "--snapshot", $snapshot,
            "--format", "custom",
            "--compress", "6",
            "--file", $dumpPath
        )
        $dumpResult = Invoke-BoundedProcess $pgDump $dumpArgs $DumpTimeoutSeconds
        if ($dumpResult.ExitCode -ne 0) { throw "pg_dump failed. $($dumpResult.Error)" }
    }
    catch { $snapshotError = $_ }
    finally {
        try {
            if ($keeper -and -not $keeper.HasExited) {
                $keeper.StandardInput.WriteLine("ROLLBACK;")
                $keeper.StandardInput.WriteLine("\q")
                $keeper.StandardInput.Flush()
                if (-not $keeper.WaitForExit(10000)) {
                    $keeper.Kill($true)
                    $keeper.WaitForExit(5000) | Out-Null
                }
            }
        }
        catch {
            if ($snapshotError) { Write-Warning "Snapshot keeper cleanup also failed: $($_.Exception.Message)" }
            else { $snapshotError = $_ }
        }
        if ($keeper) { $keeper.Dispose() }
    }
    if ($snapshotError) { throw $snapshotError }

    $dumpHash = (Get-FileHash -LiteralPath $dumpPath -Algorithm SHA256).Hash.ToLowerInvariant()
    "$dumpHash  $([IO.Path]::GetFileName($dumpPath))" | Set-Content -LiteralPath $dumpChecksumPath -Encoding ascii

    $modelFiles = @()
    $modelArchiveHash = $null
    if (-not $SkipModels) {
        $modelsDir = Join-Path $script:WorkspaceDir "ai/models"
        if (Test-Path -LiteralPath $modelsDir) {
            $files = @(Get-ChildItem -LiteralPath $modelsDir -File | Where-Object Extension -in ".joblib", ".json")
            if ($files.Count -gt 0) {
                Compress-Archive -LiteralPath $files.FullName -DestinationPath $modelArchive -CompressionLevel Optimal
                $modelArchiveHash = (Get-FileHash -LiteralPath $modelArchive -Algorithm SHA256).Hash.ToLowerInvariant()
                $modelFiles = @($files | Sort-Object Name | ForEach-Object {
                    [ordered]@{
                        name = $_.Name
                        size = $_.Length
                        sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
                    }
                })
            }
        }
    }

    $manifest = [ordered]@{
        createdAtUtc = [DateTime]::UtcNow.ToString("O")
        database = $env:PGDATABASE
        source = [ordered]@{
            host = $postgres.host
            port = $postgres.port
            database = $postgres.database
            serverVersion = $postgres.serverVersion
            dataDirectory = $postgres.dataDirectory
        }
        dump = [ordered]@{
            file = [IO.Path]::GetFileName($dumpPath)
            size = (Get-Item $dumpPath).Length
            sha256 = $dumpHash
        }
        rowCounts = $rowCounts
        models = [ordered]@{
            archive = $(if ($modelArchiveHash) { [IO.Path]::GetFileName($modelArchive) } else { $null })
            sha256 = $modelArchiveHash
            files = $modelFiles
        }
    }
    $manifest | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $manifestPath -Encoding utf8
    $manifestHash = (Get-FileHash -LiteralPath $manifestPath -Algorithm SHA256).Hash.ToLowerInvariant()
    "$manifestHash  $([IO.Path]::GetFileName($manifestPath))" | Set-Content -LiteralPath $manifestChecksumPath -Encoding ascii

    if ($RetentionDays -gt 0) {
        $cutoff = [DateTime]::UtcNow.AddDays(-$RetentionDays)
        Get-ChildItem -LiteralPath $output -File | Where-Object {
            $_.LastWriteTimeUtc -lt $cutoff -and $_.Name -match '^bitcoin_analyst_\d{8}T\d{6}Z_[a-f0-9]{8}\.(dump|dump\.sha256|manifest\.json|manifest\.json\.sha256|models\.zip)$'
        } | Remove-Item -Force
    }
}
catch {
    Remove-PartialBackup
    throw
}

Write-Host "Backup: $dumpPath"
Write-Host "Manifest: $manifestPath"
