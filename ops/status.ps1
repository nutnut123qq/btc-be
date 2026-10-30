param([switch]$Json)
. "$PSScriptRoot/common.ps1"

function Test-Http([string]$Url, [int]$TimeoutSeconds = 5) {
    try {
        $response = Invoke-WebRequest -Uri $Url -TimeoutSec $TimeoutSeconds -UseBasicParsing
        return [pscustomobject]@{ ok = $response.StatusCode -ge 200 -and $response.StatusCode -lt 400; detail = "HTTP $($response.StatusCode)" }
    }
    catch { return [pscustomobject]@{ ok = $false; detail = $_.Exception.Message } }
}

$state = @(Get-ProcessState)
$checks = @(
    [pscustomobject]@{ name = "backend"; url = "http://127.0.0.1:5197/api/health/ready"; timeout = 10 },
    [pscustomobject]@{ name = "ai"; url = "http://127.0.0.1:8000/api/capabilities"; timeout = 30 },
    [pscustomobject]@{ name = "frontend"; url = "http://127.0.0.1:3000/"; timeout = 10 }
) | Where-Object { $_.name -ne "frontend" -or (Test-OpsFrontendEnabled) } | ForEach-Object {
    $entry = $state | Where-Object name -eq $_.name | Select-Object -First 1
    $processOk = $null -ne $entry -and (Test-ManagedProcess $entry)
    $http = Test-Http $_.url $_.timeout
    [pscustomobject]@{ component = $_.name; process = $processOk; ready = $http.ok; restartable = $true; detail = $http.detail }
}

$pg = [pscustomobject]@{ component = "postgresql17-native"; process = $null; ready = $false; restartable = $true; detail = "PostgreSQL status unavailable" }
try {
    $server = Get-NativePg17Status
    $pg.ready = $server.ready
    $pg.detail = $server.detail
}
catch { $pg.detail = $_.Exception.Message }
# Data-plane health is split by what a restart can actually fix. A red
# freshness probe right after a candle close is normal ingest lag: restarting
# the stack only delays the next ingest further (the hourly restart loop of
# 2026-10). backend-freshness is therefore restartable=$false — visible in
# status output, never a restart trigger on its own. backend-workers keeps
# restartable=$true: a stale/failed heartbeat marks a wedged worker that a
# restart can clear. A worker reporting "disabled" (e.g. embedding backfill
# without an API key) is a configured-off state, not a failure; "idle" means
# the last cycle found no pending work and "partial" self-heals on the next
# cycle. None of these fail the gate, but non-healthy states stay visible.
# Freshness covers the decision equities (MBB, BID) since BTC ingestion is off (VNONLY-1); /api/health/freshness
# still measures BTC. The expected bar comes from the HOSE calendar (weekends/holidays never count), but equity
# ingestion runs once per session after RunAfterLocal (15:15 local), so intraday 1h bars and the day's 1d bar land
# after the close by design. Stale therefore means the newest expected finalized bar is still missing well past
# that window, ~18:00 local: 4 h after the 14:00 1h bar opens (4 h also spans the longest intraday wait, the
# 11:00 bar until the 13:00 bar finalizes at 14:10 with grace) and 11 h after the 1d bar's 00:00Z stamp.
$freshnessHealth = [pscustomobject]@{ component = "backend-freshness"; process = $null; ready = $false; restartable = $false; detail = "not checked" }
$workersHealth = [pscustomobject]@{ component = "backend-workers"; process = $null; ready = $false; restartable = $true; detail = "not checked" }
$backendCheck = @($checks | Where-Object { $_.component -eq "backend" }) | Select-Object -First 1
if ($backendCheck -and $backendCheck.ready) {
    try {
        $maxLagHours = @{ "1h" = 4; "1d" = 11 }  # unknown timeframe: no lag budget
        $equities = Invoke-RestMethod -Uri "http://127.0.0.1:5197/api/equities" -TimeoutSec 10
        $nowMs = [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds()
        $checked = @(); $stale = @(); $awaiting = @()
        foreach ($item in @($equities.items | Where-Object { $_.capabilities.decision })) {
            foreach ($tf in @($item.ingestion.timeframes)) {
                $name = "$($item.symbol) $($tf.timeframe)"
                $checked += $name
                $expected = $tf.latestExpectedFinalizedOpenTimeMs
                if ($null -eq $expected) { $stale += "$name no expected bar"; continue }
                if ($tf.upToDate) { continue }
                $budgetMs = $(if ($maxLagHours.ContainsKey($tf.timeframe)) { $maxLagHours[$tf.timeframe] } else { 0 }) * 3600000
                $missing = "$name missing bar opened $([DateTimeOffset]::FromUnixTimeMilliseconds($expected).ToString('u'))"
                if ($nowMs - $expected -gt $budgetMs) { $stale += $missing } else { $awaiting += $name }
            }
        }
        $freshnessHealth.ready = ($checked.Count -gt 0 -and $stale.Count -eq 0)
        $freshnessHealth.detail = $(if ($checked.Count -eq 0) { "no decision equity timeframes to check" }
            elseif ($stale.Count -gt 0) { "stale: " + ($stale -join ", ") }
            else { "within ingest window: " + ($checked -join ", ") }) + $(if ($awaiting.Count -gt 0) { "; awaiting session ingest: " + ($awaiting -join ", ") })
    }
    catch { $freshnessHealth.detail = $_.Exception.Message }
    try {
        $workers = Invoke-RestMethod -Uri "http://127.0.0.1:5197/api/health/workers" -TimeoutSec 10
        $passing = @("healthy", "idle", "disabled", "partial")
        $unhealthyWorkers = @($workers.workers | Where-Object { $passing -notcontains $_.status })
        $attentionWorkers = @($workers.workers | Where-Object { $_.status -ne "healthy" -and $passing -contains $_.status })
        $workersHealth.ready = ($unhealthyWorkers.Count -eq 0)
        $workersHealth.detail = $(if ($unhealthyWorkers.Count -gt 0) {
            "workers not healthy: $($unhealthyWorkers.name -join ', ')"
        } else { "workers ok" }) + $(if ($attentionWorkers.Count -gt 0) {
            "; note: $((@($attentionWorkers) | ForEach-Object { "$($_.name)=$($_.status)" }) -join ', ')"
        } else { "" })
    }
    catch { $workersHealth.detail = $_.Exception.Message }
}
else { $freshnessHealth.detail = "backend not ready"; $workersHealth.detail = "backend not ready" }

$all = @($pg) + @($checks) + @($freshnessHealth) + @($workersHealth)

if ($Json) { $all | ConvertTo-Json -Depth 3 }
else { $all | Format-Table -AutoSize }
if (@($all | Where-Object { -not $_.ready }).Count -gt 0) { exit 1 }
exit 0
