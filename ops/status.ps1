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
$all = @($pg) + @($checks)

if ($Json) { $all | ConvertTo-Json -Depth 3 }
else { $all | Format-Table -AutoSize }
if (@($all | Where-Object { -not $_.ready }).Count -gt 0) { exit 1 }
exit 0
