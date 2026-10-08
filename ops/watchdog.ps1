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

    # Grace re-checks: the post-close burst (hourly klines ingest + scheduled
    # collectors landing together) can keep probes red for several minutes and
    # still self-heal — a single fixed grace was observed too short. A real
    # outage persists past the window and still restarts.
    $graceWindowSeconds = 600
    $retrySeconds = 90
    $firstSeen = Get-Date
    $statusDetail = (($statusOut | Out-String).Trim() -replace "`r?`n", " | ")
    if ($statusDetail.Length -gt 500) { $statusDetail = $statusDetail.Substring(0, 500) }
    $statusDetail2 = $statusDetail
    $probes = 1
    while ($true) {
        $remaining = $graceWindowSeconds - ((Get-Date) - $firstSeen).TotalSeconds
        if ($remaining -le 0) { break }
        Start-Sleep -Seconds ([Math]::Min($retrySeconds, $remaining))
        $global:LASTEXITCODE = 0
        $statusOut2 = & $shell -NoProfile -File "$PSScriptRoot/status.ps1" 2>&1
        $probes++
        if ($LASTEXITCODE -eq 0) {
            $elapsed = [int]((Get-Date) - $firstSeen).TotalSeconds
            Add-Content -LiteralPath $logPath -Value "$([DateTimeOffset]::Now.ToString('O')) unhealthy probe cleared after ~${elapsed}s ($probes probes); no restart. first: $statusDetail"
            exit 0
        }
        $statusDetail2 = (($statusOut2 | Out-String).Trim() -replace "`r?`n", " | ")
        if ($statusDetail2.Length -gt 500) { $statusDetail2 = $statusDetail2.Substring(0, 500) }
    }

    Add-Content -LiteralPath $logPath -Value "$([DateTimeOffset]::Now.ToString('O')) stack unhealthy past ${graceWindowSeconds}s grace ($probes probes); restarting. first: $statusDetail last: $statusDetail2"
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
