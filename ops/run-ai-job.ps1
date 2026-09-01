param([Parameter(Mandatory)][ValidateSet("Futures", "Paper")][string]$Job)
. "$PSScriptRoot/common.ps1"
Initialize-OpsDirectories
Import-OpsSecrets

$aiDir = Join-Path $script:WorkspaceDir "ai"
$python = Join-Path $aiDir "venv/Scripts/python.exe"
if (-not (Test-Path -LiteralPath $python)) { throw "AI virtualenv is missing." }
$scriptPath = if ($Job -eq "Futures") { Join-Path $aiDir "futures_collector.py" } else { Join-Path $aiDir "paper_trader.py" }
[string[]]$jobArguments = if ($Job -eq "Futures") { @($scriptPath, "poll") } else { @($scriptPath) }
$logPath = Join-Path $script:LogsDir "$($Job.ToLowerInvariant())-job.log"
Rotate-OpsLog $logPath

Push-Location $aiDir
try {
    & $python @jobArguments *>> $logPath
    exit $LASTEXITCODE
}
finally { Pop-Location }
