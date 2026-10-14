. "$PSScriptRoot/common.ps1"
Initialize-OpsDirectories
Import-OpsSecrets

$mutex = [Threading.Mutex]::new($false, "Local\BitcoinAiAnalystWatchdog")
if (-not $mutex.WaitOne(0)) { exit 0 }
try {
    $logPath = Join-Path $script:LogsDir "watchdog.log"
    Rotate-OpsLog $logPath
    $shell = Join-Path $PSHOME $(if ($PSVersionTable.PSEdition -eq "Core") { "pwsh.exe" } else { "powershell.exe" })

    # status.ps1 -Json emits one component object per check with a `restartable`
    # flag. Process/HTTP/PostgreSQL failures are restartable; data-plane checks
    # (backend-freshness) are not — a stale probe right after a candle close is
    # normal ingest lag and restarting the stack only delays the next ingest.
    # An unparseable probe keeps the previous semantics: cannot prove health,
    # treated as restartable-unhealthy so a truly dead stack still restarts.
    function Get-StackProbe {
        $out = & $shell -NoProfile -File "$PSScriptRoot/status.ps1" -Json 2>&1
        $jsonText = ($out | Where-Object { $_ -isnot [System.Management.Automation.ErrorRecord] } | Out-String)
        $components = $null
        try { $components = @($jsonText | ConvertFrom-Json) } catch { }
        return @{ components = $components; raw = (($out | Out-String).Trim()) }
    }

    function Test-RestartableFailure($Probe) {
        if ($null -eq $Probe.components) { return $true }
        return @($Probe.components | Where-Object { -not $_.ready -and $_.restartable -ne $false }).Count -gt 0
    }

    function Get-ProbeDetail($Probe) {
        if ($null -eq $Probe.components) {
            $detail = $Probe.raw
        }
        else {
            $detail = ((@($Probe.components | Where-Object { -not $_.ready }) | ForEach-Object { "$($_.component): $($_.detail)" }) -join " | ")
        }
        if ([string]::IsNullOrWhiteSpace($detail)) { $detail = "all components ready" }
        if ($detail.Length -gt 500) { $detail = $detail.Substring(0, 500) }
        return $detail
    }

    $firstProbe = Get-StackProbe
    if (-not (Test-RestartableFailure $firstProbe)) {
        $dataFailures = @($firstProbe.components | Where-Object { -not $_.ready -and $_.restartable -eq $false })
        if ($dataFailures.Count -gt 0) {
            $dataDetail = Get-ProbeDetail $firstProbe
            Add-Content -LiteralPath $logPath -Value "$([DateTimeOffset]::Now.ToString('O')) data-plane degraded; not restarting (restart does not fix ingest lag). $dataDetail"
        }
        exit 0
    }

    # Grace re-checks: a restartable failure must persist past the window before
    # restarting. A real outage persists; a transient one clears and is logged.
    $graceWindowSeconds = 600
    $retrySeconds = 90
    $firstSeen = Get-Date
    $statusDetail = Get-ProbeDetail $firstProbe
    $statusDetail2 = $statusDetail
    $probes = 1
    while ($true) {
        $remaining = $graceWindowSeconds - ((Get-Date) - $firstSeen).TotalSeconds
        if ($remaining -le 0) { break }
        Start-Sleep -Seconds ([Math]::Min($retrySeconds, $remaining))
        $global:LASTEXITCODE = 0
        $probe = Get-StackProbe
        $probes++
        if (-not (Test-RestartableFailure $probe)) {
            $elapsed = [int]((Get-Date) - $firstSeen).TotalSeconds
            Add-Content -LiteralPath $logPath -Value "$([DateTimeOffset]::Now.ToString('O')) unhealthy probe cleared after ~${elapsed}s ($probes probes); no restart. first: $statusDetail last: $(Get-ProbeDetail $probe)"
            exit 0
        }
        $statusDetail2 = Get-ProbeDetail $probe
    }

    Add-Content -LiteralPath $logPath -Value "$([DateTimeOffset]::Now.ToString('O')) stack unhealthy past ${graceWindowSeconds}s grace ($probes probes); restarting. first: $statusDetail last: $statusDetail2"
    try { & "$PSScriptRoot/stop.ps1" *> $null }
    catch { Add-Content -LiteralPath $logPath -Value "$([DateTimeOffset]::Now.ToString('O')) stop warning: $($_.Exception.Message)" }
    # Clear any non-zero native exit code before invoking start.ps1 so a
    # successful PowerShell script is not mistaken for a failed restart.
    $global:LASTEXITCODE = 0
    & "$PSScriptRoot/start.ps1" -SkipBuild *>> $logPath
    if ($LASTEXITCODE -ne 0) { throw "Production-like restart failed." }
}
finally {
    $mutex.ReleaseMutex()
    $mutex.Dispose()
}
