[CmdletBinding()]
param(
    [string]$OutputDirectory = (Join-Path $PSScriptRoot "../.ops/backups"),
    [ValidateRange(2, 30)][int]$RetentionCount = 2,
    [ValidateRange(1, 1024)][int]$MinimumFreeGiB = 15,
    [ValidateRange(1, 1024)][int]$MinimumBackupReserveGiB = 6,
    [ValidateRange(1.0, 5.0)][double]$ReserveMultiplier = 1.5,
    [int]$CountTimeoutSeconds = 1800,
    [int]$DumpTimeoutSeconds = 7200,
    [switch]$SkipModels,
    [switch]$PreflightOnly
)

. "$PSScriptRoot/common.ps1"
Initialize-OpsDirectories
Import-OpsSecrets

function Get-CompleteBackupSets([string]$Directory) {
    if (-not (Test-Path -LiteralPath $Directory)) { return @() }

    $sets = foreach ($dump in @(Get-ChildItem -LiteralPath $Directory -File -Filter "*.dump")) {
        if ($dump.Name -notmatch '^bitcoin_analyst_\d{8}T\d{6}Z_[a-f0-9]{8}\.dump$') { continue }

        $baseName = [IO.Path]::GetFileNameWithoutExtension($dump.Name)
        $dumpChecksum = "$($dump.FullName).sha256"
        $manifest = Join-Path $Directory "$baseName.manifest.json"
        $manifestChecksum = "$manifest.sha256"
        if (-not (Test-Path -LiteralPath $dumpChecksum) `
            -or -not (Test-Path -LiteralPath $manifest) `
            -or -not (Test-Path -LiteralPath $manifestChecksum)) { continue }

        $manifestObject = Get-Content -LiteralPath $manifest -Raw | ConvertFrom-Json
        $modelArchive = $null
        if ($manifestObject.models.archive) {
            $modelArchive = Join-Path $Directory ([string]$manifestObject.models.archive)
            if (-not (Test-Path -LiteralPath $modelArchive)) { continue }
        }

        [pscustomobject]@{
            BaseName = $baseName
            Dump = $dump.FullName
            DumpChecksum = $dumpChecksum
            Manifest = $manifest
            ManifestChecksum = $manifestChecksum
            ModelArchive = $modelArchive
            CreatedAtUtc = $dump.LastWriteTimeUtc
            EstimatedSetBytes = [long]$dump.Length + $(if ($modelArchive) { [long](Get-Item -LiteralPath $modelArchive).Length } else { 0L })
        }
    }

    return @($sets | Sort-Object CreatedAtUtc -Descending)
}

function Assert-ChildPath([string]$Parent, [string]$Path) {
    $parentFull = [IO.Path]::GetFullPath($Parent).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    $pathFull = [IO.Path]::GetFullPath($Path)
    if (-not $pathFull.StartsWith($parentFull, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing path outside backup directory: $pathFull"
    }
    return $pathFull
}

$output = Assert-SafeDataDirectory $OutputDirectory
New-Item -ItemType Directory -Force -Path $output | Out-Null
$root = [IO.Path]::GetPathRoot($output)
if ([string]::IsNullOrWhiteSpace($root)) { throw "Could not resolve backup drive for $output" }

$mutex = [Threading.Mutex]::new($false, "Local\BitcoinAnalystGuardedBackup")
$mutexHeld = $false
try {
    try { $mutexHeld = $mutex.WaitOne(0) }
    catch [Threading.AbandonedMutexException] { $mutexHeld = $true }
    if (-not $mutexHeld) { throw "Another guarded Bitcoin Analyst backup is already running." }

    $beforeSets = @(Get-CompleteBackupSets $output)
    $latestBytes = if ($beforeSets.Count -gt 0) { [long]$beforeSets[0].EstimatedSetBytes } else { 0L }
    $minimumReserveBytes = [long]$MinimumBackupReserveGiB * 1GB
    $estimatedReserveBytes = [long][Math]::Ceiling($latestBytes * $ReserveMultiplier)
    $reserveBytes = [Math]::Max($minimumReserveBytes, $estimatedReserveBytes)
    $minimumFreeBytes = [long]$MinimumFreeGiB * 1GB
    $drive = [IO.DriveInfo]::new($root)
    $freeBefore = [long]$drive.AvailableFreeSpace

    Write-Host ("Guarded backup preflight: free={0:N2} GiB reserve={1:N2} GiB floor={2} GiB completeSets={3}" -f `
        ($freeBefore / 1GB), ($reserveBytes / 1GB), $MinimumFreeGiB, $beforeSets.Count)
    if ($freeBefore - $reserveBytes -lt $minimumFreeBytes) {
        throw ("Insufficient backup headroom. Need free space to remain at least {0} GiB after reserving {1:N2} GiB; current free is {2:N2} GiB." -f `
            $MinimumFreeGiB, ($reserveBytes / 1GB), ($freeBefore / 1GB))
    }
    if ($PreflightOnly) { return }

    $knownNames = @{}
    foreach ($set in $beforeSets) { $knownNames[$set.BaseName] = $true }
    $backupParams = @{
        OutputDirectory = $output
        RetentionDays = 0
        CountTimeoutSeconds = $CountTimeoutSeconds
        DumpTimeoutSeconds = $DumpTimeoutSeconds
        SkipModels = $SkipModels
    }
    & "$PSScriptRoot/backup.ps1" @backupParams

    $afterSets = @(Get-CompleteBackupSets $output)
    $newSets = @($afterSets | Where-Object { -not $knownNames.ContainsKey($_.BaseName) })
    if ($newSets.Count -ne 1) {
        throw "Expected exactly one new complete backup set, found $($newSets.Count). No retention cleanup was performed."
    }

    $newSet = $newSets[0]
    & "$PSScriptRoot/restore-verify.ps1" -BackupPath $newSet.Dump -Mode ListOnly

    $freeAfter = [long]([IO.DriveInfo]::new($root).AvailableFreeSpace)
    if ($freeAfter -lt $minimumFreeBytes) {
        throw ("Backup completed but free space fell below the {0} GiB safety floor. No retention cleanup was performed." -f $MinimumFreeGiB)
    }

    $completeSets = @(Get-CompleteBackupSets $output)
    if ($completeSets.Count -lt $RetentionCount) {
        throw "Only $($completeSets.Count) complete backup set(s) exist; at least $RetentionCount are required before rotation. No files were deleted."
    }

    $kept = @($completeSets | Select-Object -First $RetentionCount)
    foreach ($set in $kept) {
        if ($set.BaseName -eq $newSet.BaseName) { continue }
        & "$PSScriptRoot/restore-verify.ps1" -BackupPath $set.Dump -Mode ListOnly
    }

    $expired = @($completeSets | Select-Object -Skip $RetentionCount)
    foreach ($set in $expired) {
        $paths = @($set.Dump, $set.DumpChecksum, $set.Manifest, $set.ManifestChecksum)
        if ($set.ModelArchive) { $paths += $set.ModelArchive }
        foreach ($path in $paths) {
            $safePath = Assert-ChildPath $output $path
            Remove-Item -LiteralPath $safePath -Force
        }
        Write-Host "Removed superseded verified backup set: $($set.BaseName)"
    }

    Write-Host "Guarded backup complete: $($newSet.Dump)"
    Write-Host "Verified backup sets retained: $($kept.Count)"
}
finally {
    if ($mutexHeld) { $mutex.ReleaseMutex() }
    $mutex.Dispose()
}
