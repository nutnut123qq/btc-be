# Task entrypoint for BTCPaperObserver. Deliberately does NOT dot-source common.ps1 or call run-ai-job.ps1: both
# load the user-scope DPAPI ops secrets, which need the deploy user's master key (absent after logout/reboot).
# It reads only the machine-scope observer secret, runs `paper_observer.py --once`, and appends one line per run
# (identity, session, batch logon, exit code, observer JSON or error) to .ops/logs/paperobserver-task.log.
param([string]$SecretPath, [string]$AiDir, [string]$LogDirectory)
$ErrorActionPreference = "Continue"
$backendDir = Split-Path $PSScriptRoot -Parent
if (-not $LogDirectory) { $LogDirectory = Join-Path $backendDir ".ops/logs" }
if (-not $SecretPath) { $SecretPath = Join-Path $backendDir ".ops/observer-db.secret" }
if (-not $AiDir) {
    $workspace = Split-Path $backendDir -Parent
    $AiDir = @("btc-ai", "ai") | ForEach-Object { Join-Path $workspace $_ } |
        Where-Object { Test-Path -LiteralPath (Join-Path $_ "paper_observer.py") } | Select-Object -First 1
}
$started = [DateTimeOffset]::UtcNow
$code = 1
$detail = ""
try {
    . "$PSScriptRoot/observer-secret.ps1"
    $env:DB_PASS = Read-ObserverDbPassword $SecretPath  # process environment only; never logged
    $env:PGPASSWORD = $env:DB_PASS
    if (-not $AiDir) { throw "AI directory with paper_observer.py not found." }
    $python = Join-Path $AiDir "venv\Scripts\python.exe"
    if (-not (Test-Path -LiteralPath $python)) { throw "AI virtualenv is missing: $python" }
    Push-Location -LiteralPath $AiDir
    try {
        $output = & $python paper_observer.py --once 2>&1 | ForEach-Object { "$_" }
        $code = if ($null -eq $LASTEXITCODE) { 1 } else { $LASTEXITCODE }
    }
    finally { Pop-Location }
    $detail = ($output | Where-Object { $_ }) -join " | "
}
catch { $detail = "error: $($_.Exception.Message)"; $code = 1 }
finally { Remove-Item Env:DB_PASS, Env:PGPASSWORD -ErrorAction SilentlyContinue }
$detail = $detail -replace "\s+", " "
if ($detail.Length -gt 800) { $detail = $detail.Substring(0, 800) }
$batch = [bool](& (Join-Path $env:SystemRoot "System32\whoami.exe") /groups /fo csv | ConvertFrom-Csv | Where-Object SID -eq "S-1-5-3")
# shortcut: one short line per 5-minute run (~15 MB/year) is not rotated; rotate if cadence or line size grows.
$line = "{0} user={1} session={2} batch={3} exit={4} seconds={5:N1} {6}" -f $started.ToString("o"), [Environment]::UserName,
    (Get-Process -Id $PID).SessionId, $batch, $code, ([DateTimeOffset]::UtcNow - $started).TotalSeconds, $detail
New-Item -ItemType Directory -Force $LogDirectory | Out-Null
Add-Content -LiteralPath (Join-Path $LogDirectory "paperobserver-task.log") -Value $line.TrimEnd() -Encoding UTF8
exit $code
