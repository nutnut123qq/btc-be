. "$PSScriptRoot/common.ps1"

$wrapper = Join-Path $PSScriptRoot "run-guarded-backup.ps1"
$tokens = $null
$parseErrors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile($wrapper, [ref]$tokens, [ref]$parseErrors)
if ($parseErrors.Count -gt 0) { throw "Backup wrapper has PowerShell parse errors." }
$unsafeDefaults = @($ast.ParamBlock.Parameters | Where-Object {
    $_.DefaultValue -and $_.DefaultValue.Extent.Text -match '\$PSScriptRoot'
})
if ($unsafeDefaults.Count -gt 0) {
    throw "Backup wrapper parameter defaults must not depend on PSScriptRoot under inbox Windows PowerShell."
}

$testRoot = Join-Path ([IO.Path]::GetTempPath()) "btc-backup-wrapper-$([Guid]::NewGuid().ToString('N'))"
$fakeGuard = Join-Path $testRoot "fake-guard.ps1"
$logs = Join-Path $testRoot "logs"
$status = Join-Path $testRoot "status"
$output = Join-Path $testRoot "backups"
try {
    New-Item -ItemType Directory -Force -Path $testRoot, $logs, $status, $output | Out-Null
    @'
param([string]$OutputDirectory,[int]$RetentionCount,[int]$MinimumFreeGiB,[switch]$SkipModels)
Write-Output "fake backup ok"
exit 0
'@ | Set-Content -LiteralPath $fakeGuard -Encoding utf8

    $inboxPowerShell = Join-Path $env:SystemRoot "System32\WindowsPowerShell\v1.0\powershell.exe"
    $arguments = @(
        "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", $wrapper,
        "-OutputDirectory", $output, "-GuardScriptPath", $fakeGuard,
        "-LogDirectory", $logs, "-StatusDirectory", $status,
        "-MaximumRuntimeSeconds", "60"
    )
    $result = Invoke-BoundedProcess $inboxPowerShell $arguments 90
    if ($result.ExitCode -ne 0) { throw "Success wrapper run failed: $($result.Error)" }
    $statusPath = Join-Path $status "backup-scheduler-status.json"
    $run = Get-Content -LiteralPath $statusPath -Raw | ConvertFrom-Json
    $required = @("schema", "runId", "succeeded", "exitCode", "failureType", "startedAtUtc",
        "completedAtUtc", "durationSeconds", "logFileName", "outputDirectory")
    foreach ($name in $required) {
        if (-not $run.PSObject.Properties[$name]) { throw "Status field missing: $name" }
    }
    if (-not $run.succeeded -or $run.exitCode -ne 0 -or $run.failureType) { throw "Success status is invalid." }
    if ($run.logFileName -ne "backup-scheduler.log") { throw "Unexpected log filename." }

    [IO.File]::WriteAllText((Join-Path $logs "unrelated.log"), "keep")
    [IO.File]::WriteAllText((Join-Path $logs "backup-scheduler.log"), ("x" * (26MB)))
    $result = Invoke-BoundedProcess $inboxPowerShell $arguments 90
    if ($result.ExitCode -ne 0) { throw "Rotation wrapper run failed: $($result.Error)" }
    if (-not (Test-Path -LiteralPath (Join-Path $logs "backup-scheduler.log.previous"))) { throw "Exact log was not rotated." }
    if ((Get-Content -LiteralPath (Join-Path $logs "unrelated.log") -Raw) -ne "keep") { throw "Unrelated log was modified." }

    @'
param([string]$OutputDirectory,[int]$RetentionCount,[int]$MinimumFreeGiB,[switch]$SkipModels)
Write-Error "fake failure"
exit 7
'@ | Set-Content -LiteralPath $fakeGuard -Encoding utf8
    $result = Invoke-BoundedProcess $inboxPowerShell $arguments 90
    if ($result.ExitCode -ne 7) { throw "Failure exit code was not propagated." }
    $run = Get-Content -LiteralPath $statusPath -Raw | ConvertFrom-Json
    if ($run.succeeded -or $run.exitCode -ne 7 -or $run.failureType -ne "guarded_backup_failed") {
        throw "Failure status is invalid."
    }

    Write-Host "Backup scheduler wrapper self-test passed."
}
finally {
    if (Test-Path -LiteralPath $testRoot) { Remove-Item -LiteralPath $testRoot -Recurse -Force }
}
