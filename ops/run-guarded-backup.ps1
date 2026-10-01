[CmdletBinding()]
param(
    [string]$OutputDirectory = "",
    [ValidateRange(2, 30)][int]$RetentionCount = 2,
    [ValidateRange(1, 1024)][int]$MinimumFreeGiB = 15,
    [ValidateRange(60, 86400)][int]$MaximumRuntimeSeconds = 14400,
    [switch]$SkipModels,
    [string]$GuardScriptPath = "",
    [string]$StatusDirectory = "",
    [string]$LogDirectory = ""
)

$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
. (Join-Path $scriptRoot "common.ps1")
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $scriptRoot "../.ops/backups"
}
if ([string]::IsNullOrWhiteSpace($GuardScriptPath)) {
    $GuardScriptPath = Join-Path $scriptRoot "guarded-backup.ps1"
}
if ([string]::IsNullOrWhiteSpace($StatusDirectory)) {
    $StatusDirectory = Join-Path $scriptRoot "../.ops/status"
}
if ([string]::IsNullOrWhiteSpace($LogDirectory)) {
    $LogDirectory = Join-Path $scriptRoot "../.ops/logs"
}
$started = [DateTimeOffset]::UtcNow
$runId = [Guid]::NewGuid().ToString("N")
$exitCode = 1
$failureType = "wrapper_error"
$succeeded = $false
$output = [IO.Path]::GetFullPath($OutputDirectory)
$guard = [IO.Path]::GetFullPath($GuardScriptPath)
$statusDir = [IO.Path]::GetFullPath($StatusDirectory)
$logDir = [IO.Path]::GetFullPath($LogDirectory)
$logPath = Join-Path $logDir "backup-scheduler.log"
$statusPath = Join-Path $statusDir "backup-scheduler-status.json"

try {
    New-Item -ItemType Directory -Force -Path $statusDir, $logDir | Out-Null
    Rotate-OpsLog -Path $logPath
    if (-not (Test-Path -LiteralPath $guard -PathType Leaf)) {
        throw "Guarded backup script not found: $guard"
    }

    $inboxPowerShell = Join-Path $env:SystemRoot "System32\WindowsPowerShell\v1.0\powershell.exe"
    if (-not (Test-Path -LiteralPath $inboxPowerShell -PathType Leaf)) {
        throw "Windows PowerShell inbox executable not found: $inboxPowerShell"
    }
    $arguments = @(
        "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", $guard,
        "-OutputDirectory", $output,
        "-RetentionCount", [string]$RetentionCount,
        "-MinimumFreeGiB", [string]$MinimumFreeGiB
    )
    if ($SkipModels) { $arguments += "-SkipModels" }

    try {
        $result = Invoke-BoundedProcess -FilePath $inboxPowerShell -ArgumentList $arguments `
            -TimeoutSeconds $MaximumRuntimeSeconds -WorkingDirectory $script:BackendDir
        if ($result.Output) { $result.Output.TrimEnd() | Add-Content -LiteralPath $logPath -Encoding utf8 }
        if ($result.Error) { $result.Error.TrimEnd() | Add-Content -LiteralPath $logPath -Encoding utf8 }
        $exitCode = [int]$result.ExitCode
        $succeeded = $exitCode -eq 0
        $failureType = if ($succeeded) { $null } else { "guarded_backup_failed" }
    }
    catch {
        $_.Exception.Message | Add-Content -LiteralPath $logPath -Encoding utf8
        if ($_.Exception.Message -match 'timed out') {
            $exitCode = 124
            $failureType = "timeout"
        }
        else {
            $exitCode = 1
            $failureType = "wrapper_error"
        }
    }
}
catch {
    New-Item -ItemType Directory -Force -Path $statusDir, $logDir | Out-Null
    $_.Exception.Message | Add-Content -LiteralPath $logPath -Encoding utf8
    $exitCode = 1
    $failureType = "wrapper_error"
}
finally {
    $completed = [DateTimeOffset]::UtcNow
    $status = [ordered]@{
        schema = "btc-backup-scheduler-run/v1"
        runId = $runId
        succeeded = $succeeded
        exitCode = $exitCode
        failureType = $failureType
        startedAtUtc = $started.ToString("O")
        completedAtUtc = $completed.ToString("O")
        durationSeconds = [Math]::Round(($completed - $started).TotalSeconds, 3)
        logFileName = [IO.Path]::GetFileName($logPath)
        outputDirectory = $output
    }
    $temporary = "$statusPath.$runId.tmp"
    [IO.File]::WriteAllText($temporary, ($status | ConvertTo-Json -Compress) + [Environment]::NewLine,
        [Text.UTF8Encoding]::new($false))
    if (Test-Path -LiteralPath $statusPath) {
        $statusBackup = "$statusPath.previous"
        if (Test-Path -LiteralPath $statusBackup) { [IO.File]::Delete($statusBackup) }
        [IO.File]::Replace($temporary, $statusPath, $statusBackup)
        [IO.File]::Delete($statusBackup)
    }
    else {
        [IO.File]::Move($temporary, $statusPath)
    }
}

exit $exitCode
