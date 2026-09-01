. "$PSScriptRoot/common.ps1"
Initialize-OpsDirectories
Import-OpsSecrets

$mutex = [Threading.Mutex]::new($false, "Local\BitcoinAiAnalystWatchdog")
if (-not $mutex.WaitOne(0)) { exit 0 }
try {
    $logPath = Join-Path $script:LogsDir "watchdog.log"
    Rotate-OpsLog $logPath
    & pwsh.exe -NoProfile -File "$PSScriptRoot/status.ps1" *> $null
    if ($LASTEXITCODE -eq 0) { exit 0 }

    Add-Content -LiteralPath $logPath -Value "$([DateTimeOffset]::Now.ToString('O')) stack unhealthy; restarting"
    try { & "$PSScriptRoot/stop.ps1" *> $null }
    catch { Add-Content -LiteralPath $logPath -Value "$([DateTimeOffset]::Now.ToString('O')) stop warning: $($_.Exception.Message)" }
    # status.ps1 intentionally returns 1 for an unhealthy stack. Clear that
    # native exit code before invoking start.ps1 so a successful PowerShell
    # script is not mistaken for a failed restart.
    $global:LASTEXITCODE = 0
    & "$PSScriptRoot/start.ps1" -SkipBuild *>> $logPath
    if ($LASTEXITCODE -ne 0) { throw "Production-like restart failed." }
}
finally {
    $mutex.ReleaseMutex()
    $mutex.Dispose()
}
