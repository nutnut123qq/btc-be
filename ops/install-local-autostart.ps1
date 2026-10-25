# Elevated, one-time: switch this host's unattended tasks to an S4U batch logon of the deploy user so the stack,
# the forward-paper recorder and the guarded backup run after boot without anyone logging on. No password is stored;
# S4U cannot unlock the user's DPAPI key, so the tasks read only the machine-scope subset written by
# configure-machine-secrets.ps1 (DB connection + admin key, no paid API keys).
# Rollback (elevated): Disable-ScheduledTask for the three tasks; the stack then needs a manual start.ps1 again.
[CmdletBinding(SupportsShouldProcess)]
param(
    [string]$WatchdogTask = "BTCServiceWatchdog",
    [string]$PaperTask = "BTCPaperTrader",
    [string]$BackupTask = "Bitcoin Analyst DB Backup",
    [string]$BackupDailyAt = "02:00",
    [ValidateRange(1, 1024)][int]$BackupMinimumFreeGiB = 15
)
. "$PSScriptRoot/common.ps1"
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
if (-not ([Security.Principal.WindowsPrincipal]$identity).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw "Registering S4U tasks requires an elevated session."
}
$powerShell = Join-Path $env:SystemRoot "System32\WindowsPowerShell\v1.0\powershell.exe"
$logonType = "S4U"
$principal = New-ScheduledTaskPrincipal -UserId $identity.Name -LogonType $logonType -RunLevel Limited

Assert-HostProfile (Join-Path $script:RuntimeDir "backend-settings.json")
$machine = Read-OpsMachineSecrets
foreach ($name in @("PGPASSWORD", "AdminApiKey", "PGDATABASE")) {
    if (-not $machine.PSObject.Properties[$name] -or -not $machine.PSObject.Properties[$name].Value) { throw "Machine secret lacks $name; run configure-machine-secrets.ps1." }
}
if ($machine.PGDATABASE -ne $env:PGDATABASE) { throw "Machine secret database '$($machine.PGDATABASE)' differs from the user secrets '$env:PGDATABASE'." }

function Assert-Task([string]$Name, [string]$Arguments) {
    $task = Get-ScheduledTask -TaskName $Name -ErrorAction Stop
    $problems = @()
    if ($task.Principal.LogonType -ne $logonType) { $problems += "logon=$($task.Principal.LogonType)" }
    if ($task.Principal.RunLevel -ne "Limited") { $problems += "runLevel=$($task.Principal.RunLevel)" }
    if (@($task.Actions).Count -ne 1 -or $task.Actions[0].Arguments -ne $Arguments) { $problems += "action" }
    if ($task.Settings.MultipleInstances -ne "IgnoreNew") { $problems += "multipleInstances" }
    if ($task.State -eq "Disabled") { $problems += "state=Disabled" }
    if ($problems) { throw "Task $Name does not match the intended definition: $($problems -join ', ')" }
    Write-Host "$Name : $($task.Principal.UserId) $logonType, $($task.State), triggers=$(@($task.Triggers).Count)"
}

# Watchdog = autostart + recovery: at boot and every 15 min it probes status.ps1 and, after a 10-minute grace,
# restarts via start.ps1 -SkipBuild -RequireHostProfile. start.ps1 refuses (no process churn) when PostgreSQL is not
# ready, the schema head differs from the code, or the host profile is missing/partial.
$watchdogArgs = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -File `"$([IO.Path]::GetFullPath((Join-Path $PSScriptRoot 'watchdog.ps1')))`" -RequireHostProfile"
$paperArgs = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -File `"$([IO.Path]::GetFullPath((Join-Path $PSScriptRoot 'run-ai-job.ps1')))`" -Job Paper"
$every15 = New-ScheduledTaskTrigger -Once -At (Get-Date).Date.AddMinutes(9) -RepetitionInterval (New-TimeSpan -Minutes 15)
$commonSettings = @{ MultipleInstances = "IgnoreNew"; StartWhenAvailable = $true; AllowStartIfOnBatteries = $true; DontStopIfGoingOnBatteries = $true }

if ($PSCmdlet.ShouldProcess("$WatchdogTask, $PaperTask, $BackupTask", "register $logonType principal")) {
    Register-ScheduledTask -TaskName $WatchdogTask -Force -ErrorAction Stop -Principal $principal `
        -Action (New-ScheduledTaskAction -Execute $powerShell -Argument $watchdogArgs -WorkingDirectory $script:BackendDir) `
        -Trigger @((New-ScheduledTaskTrigger -AtStartup), $every15) `
        -Settings (New-ScheduledTaskSettingsSet @commonSettings -ExecutionTimeLimit (New-TimeSpan -Minutes 30)) `
        -Description "BTC local stack autostart/recovery (watchdog.ps1 -RequireHostProfile)." | Out-Null
    Register-ScheduledTask -TaskName $PaperTask -Force -ErrorAction Stop -Principal $principal `
        -Action (New-ScheduledTaskAction -Execute $powerShell -Argument $paperArgs -WorkingDirectory $script:BackendDir) `
        -Trigger $every15 `
        -Settings (New-ScheduledTaskSettingsSet @commonSettings -ExecutionTimeLimit (New-TimeSpan -Minutes 14)) `
        -Description "Forward-paper recorder (paper_trader.py --mode forward-paper); idempotent per finalized 4h bar." | Out-Null
    # Daily guarded backup, catch-up after a missed run (StartWhenAvailable), no rotation (RetentionCount 0).
    & "$PSScriptRoot/install-backup-task.ps1" -TaskName $BackupTask -OutputDirectory (Join-Path $script:RuntimeDir "backups") -RetentionCount 0 -MinimumFreeGiB $BackupMinimumFreeGiB -DailyAt $BackupDailyAt -Enable
    Set-ScheduledTask -TaskName $BackupTask -Principal $principal -ErrorAction Stop | Out-Null
    Enable-ScheduledTask -TaskName $WatchdogTask | Out-Null
    Enable-ScheduledTask -TaskName $PaperTask | Out-Null
}

Assert-Task $WatchdogTask $watchdogArgs
Assert-Task $PaperTask $paperArgs
$backup = Get-ScheduledTask -TaskName $BackupTask
if ($backup.Principal.LogonType -ne $logonType -or $backup.Actions[0].Arguments -notmatch '-RetentionCount 0 ') { throw "Backup task does not match the intended definition." }
Write-Host "$BackupTask : $($backup.Principal.UserId) $logonType, $($backup.State), $($backup.Actions[0].Arguments)"
