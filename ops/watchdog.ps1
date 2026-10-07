. "$PSScriptRoot/common.ps1"
Initialize-OpsDirectories
Import-OpsSecrets

$mutex = [Threading.Mutex]::new($false, "Local\BitcoinAiAnalystWatchdog")
if (-not $mutex.WaitOne(0)) { exit 0 }
try {
    $logPath = Join-Path $script:LogsDir "watchdog.log"
    Rotate-OpsLog $logPath
    $shell = Join-Path $PSHOME $(if ($PSVersionTable.PSEdition -eq "Core") { "pwsh.exe" } else { "powershell.exe" })
    $statusOut = & $shell -NoProfile -File "$PSScriptRoot/status.ps1" 2>&1
    if ($LASTEXITCODE -eq 0) { exit 0 }

    # Grace re-check: the hourly freshness flap (~3 min after each bar close,
    # while the new 1h candle awaits ingestion) self-heals. A real outage
    # persists past the grace window and still restarts.
    $graceSeconds = 180
    $statusDetail = (($statusOut | Out-String).Trim() -replace "`r?`n", " | ")
    if ($statusDetail.Length -gt 500) { $statusDetail = $statusDetail.Substring(0, 500) }
    Start-Sleep -Seconds $graceSeconds
    $global:LASTEXITCODE = 0
    $statusOut2 = & $shell -NoProfile -File "$PSScriptRoot/status.ps1" 2>&1
    if ($LASTEXITCODE -eq 0) {
        Add-Content -LiteralPath $logPath -Value "$([DateTimeOffset]::Now.ToString('O')) unhealthy probe cleared after ${graceSeconds}s grace; no restart. first: $statusDetail"
        exit 0
    }
    $statusDetail2 = (($statusOut2 | Out-String).Trim() -replace "`r?`n", " | ")
    if ($statusDetail2.Length -gt 500) { $statusDetail2 = $statusDetail2.Substring(0, 500) }

    Add-Content -LiteralPath $logPath -Value "$([DateTimeOffset]::Now.ToString('O')) stack unhealthy past ${graceSeconds}s grace; restarting. first: $statusDetail second: $statusDetail2"
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
