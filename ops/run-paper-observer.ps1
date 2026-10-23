# Task entrypoint for BTCPaperObserver. Runs `run-ai-job.ps1 -Job PaperObserver` in a child process and appends
# one timestamped line (identity, session, batch logon, exit code, PowerShell-level error) to
# .ops/logs/paperobserver-task.log, so failures before Python starts (e.g. secrets decryption) are still logged.
# The observer's own JSON summary keeps going to paperobserver-job.log via run-ai-job.ps1.
$ErrorActionPreference = "Continue"
$logPath = Join-Path (Split-Path $PSScriptRoot -Parent) ".ops/logs/paperobserver-task.log"
$powerShell = Join-Path $env:SystemRoot "System32\WindowsPowerShell\v1.0\powershell.exe"
$started = [DateTimeOffset]::UtcNow
$output = & $powerShell -NoProfile -NonInteractive -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot "run-ai-job.ps1") -Job PaperObserver 2>&1 |
    ForEach-Object { "$_" }
$code = if ($null -eq $LASTEXITCODE) { 1 } else { $LASTEXITCODE }
$batch = [bool](& (Join-Path $env:SystemRoot "System32\whoami.exe") /groups /fo csv | ConvertFrom-Csv | Where-Object SID -eq "S-1-5-3")
$detail = (($output | Where-Object { $_ }) -join " | ") -replace "\s+", " "
if ($detail.Length -gt 500) { $detail = $detail.Substring(0, 500) }
# shortcut: one short line per 5-minute run (~10 MB/year) is not rotated; add Rotate-OpsLog if cadence grows.
$line = "{0} user={1} session={2} batch={3} exit={4} seconds={5:N1} {6}" -f $started.ToString("o"), [Environment]::UserName,
    (Get-Process -Id $PID).SessionId, $batch, $code, ([DateTimeOffset]::UtcNow - $started).TotalSeconds, $detail
New-Item -ItemType Directory -Force (Split-Path $logPath -Parent) | Out-Null
Add-Content -LiteralPath $logPath -Value $line.TrimEnd() -Encoding UTF8
exit $code
