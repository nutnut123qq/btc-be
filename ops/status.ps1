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
) | ForEach-Object {
    $entry = $state | Where-Object name -eq $_.name | Select-Object -First 1
    $processOk = $null -ne $entry -and (Test-ManagedProcess $entry)
    $http = Test-Http $_.url $_.timeout
    [pscustomobject]@{ component = $_.name; process = $processOk; ready = $http.ok; detail = $http.detail }
}

$pg = [pscustomobject]@{ component = "postgresql17-native"; process = $null; ready = $false; detail = "PostgreSQL status unavailable" }
try {
    $server = Get-NativePg17Status
    $pg.ready = $server.ready
    $pg.detail = $server.detail
}
catch { $pg.detail = $_.Exception.Message }
# Data-plane health: a reachable API can still serve stale data when workers
# stall (the previous silent-stale incident). freshness.status is "healthy" only
# when every active timeframe is fresh; workers are healthy unless stale/failed.
# A worker reporting "disabled" (e.g. embedding backfill without an API key) is
# a configured-off state, not a failure; "idle" means the last cycle found no
# pending work and "partial" self-heals on the next cycle. None of these fail
# the gate, but non-healthy states stay visible in detail.
$dataHealth = [pscustomobject]@{ component = "backend-data"; process = $null; ready = $false; detail = "not checked" }
$backendCheck = @($checks | Where-Object { $_.component -eq "backend" }) | Select-Object -First 1
if ($backendCheck -and $backendCheck.ready) {
    try {
        $freshness = Invoke-RestMethod -Uri "http://127.0.0.1:5197/api/health/freshness" -TimeoutSec 10
        $workers = Invoke-RestMethod -Uri "http://127.0.0.1:5197/api/health/workers" -TimeoutSec 10
        $passing = @("healthy", "idle", "disabled", "partial")
        $unhealthyWorkers = @($workers.workers | Where-Object { $passing -notcontains $_.status })
        $attentionWorkers = @($workers.workers | Where-Object { $_.status -ne "healthy" -and $passing -contains $_.status })
        $dataHealth.ready = ($freshness.status -eq "healthy") -and $unhealthyWorkers.Count -eq 0
        $dataHealth.detail = "freshness=$($freshness.status)" + $(if ($unhealthyWorkers.Count -gt 0) {
            "; workers not healthy: $($unhealthyWorkers.name -join ', ')"
        } else { "; workers ok" }) + $(if ($attentionWorkers.Count -gt 0) {
            "; note: $((@($attentionWorkers) | ForEach-Object { "$($_.name)=$($_.status)" }) -join ', ')"
        } else { "" })
    }
    catch { $dataHealth.detail = $_.Exception.Message }
}
else { $dataHealth.detail = "backend not ready" }

$all = @($pg) + @($checks) + @($dataHealth)

if ($Json) { $all | ConvertTo-Json -Depth 3 }
else { $all | Format-Table -AutoSize }
if (@($all | Where-Object { -not $_.ready }).Count -gt 0) { exit 1 }
exit 0
