param([Parameter(Mandatory)][ValidateSet("Futures", "Paper", "EnsemblePaper", "Liquidation", "Sentiment", "Confluence")][string]$Job)
. "$PSScriptRoot/common.ps1"
Initialize-OpsDirectories
Import-OpsSecrets

if ($Job -in @("EnsemblePaper", "Confluence")) {
    if ([string]::IsNullOrWhiteSpace($env:AdminApiKey)) { throw "AdminApiKey is required for $Job." }
    $logPath = Join-Path $script:LogsDir "$($Job.ToLowerInvariant())-job.log"
    Rotate-OpsLog $logPath
    foreach ($target in @(@{ Symbol = "BTCUSDT"; Timeframe = "4h" }, @{ Symbol = "ETHUSDT"; Timeframe = "1h" }, @{ Symbol = "SOLUSDT"; Timeframe = "1h" })) {
        if ($Job -eq "EnsemblePaper") {
            $body = $target | ConvertTo-Json -Compress
            $result = Invoke-RestMethod -Uri "http://127.0.0.1:5197/api/paper-trades/evaluate-ensemble" `
                -Method Post -Headers @{ "X-Admin-Key" = $env:AdminApiKey } -ContentType "application/json" `
                -Body $body -TimeoutSec 120
            "$([DateTimeOffset]::Now.ToString('O')) $($target.Symbol) $($target.Timeframe) $($result.actionTaken)" | Add-Content -LiteralPath $logPath
        }
        else {
            $result = Invoke-RestMethod -Uri "http://127.0.0.1:5197/api/confluence/calculate?symbol=$($target.Symbol)" `
                -Method Post -Headers @{ "X-Admin-Key" = $env:AdminApiKey } -TimeoutSec 120
            "$([DateTimeOffset]::Now.ToString('O')) $($target.Symbol) score=$($result.confluenceScore)" | Add-Content -LiteralPath $logPath
        }
    }
    exit 0
}

$aiDir = $script:AiDir
$python = Join-Path $aiDir "venv/Scripts/python.exe"
if (-not (Test-Path -LiteralPath $python)) { throw "AI virtualenv is missing." }
$scriptName = switch ($Job) {
    "Futures" { "futures_collector.py" }
    "Paper" { "paper_trader.py" }
    "Liquidation" { "liquidation_engine.py" }
    "Sentiment" { "macro_sentiment.py" }
}
$scriptPath = Join-Path $aiDir $scriptName
[string[]]$jobArguments = if ($Job -eq "Futures") { @($scriptPath, "poll") } else { @($scriptPath) }
$logPath = Join-Path $script:LogsDir "$($Job.ToLowerInvariant())-job.log"
Rotate-OpsLog $logPath

Push-Location $aiDir
try {
    & $python @jobArguments *>> $logPath
    exit $LASTEXITCODE
}
finally { Pop-Location }
