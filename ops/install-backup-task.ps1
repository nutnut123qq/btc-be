[CmdletBinding(SupportsShouldProcess)]
param(
    [string]$TaskName = "Bitcoin Analyst DB Backup",
    [string]$OutputDirectory = (Join-Path $PSScriptRoot "../.ops/backups"),
    [ValidateRange(2, 30)][int]$RetentionCount = 2,
    [ValidateRange(1, 1024)][int]$MinimumFreeGiB = 15,
    [datetime]$DailyAt = "02:00",
    [switch]$Enable
)

. "$PSScriptRoot/common.ps1"
$output = Assert-SafeDataDirectory $OutputDirectory
$existing = Get-ScheduledTask -TaskName $TaskName -ErrorAction Stop
$guardScript = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot "guarded-backup.ps1"))
if (-not (Test-Path -LiteralPath $guardScript)) { throw "Guarded backup script not found: $guardScript" }

& $guardScript -OutputDirectory $output -RetentionCount $RetentionCount -MinimumFreeGiB $MinimumFreeGiB -PreflightOnly

$powerShell = Join-Path $env:SystemRoot "System32\WindowsPowerShell\v1.0\powershell.exe"
$arguments = "-NoProfile -ExecutionPolicy Bypass -File `"$guardScript`" -OutputDirectory `"$output`" -RetentionCount $RetentionCount -MinimumFreeGiB $MinimumFreeGiB"
$action = New-ScheduledTaskAction -Execute $powerShell -Argument $arguments -WorkingDirectory $script:BackendDir
$trigger = New-ScheduledTaskTrigger -Daily -At $DailyAt
$settings = New-ScheduledTaskSettingsSet -MultipleInstances IgnoreNew -ExecutionTimeLimit (New-TimeSpan -Hours 4) -StartWhenAvailable -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries

if ($PSCmdlet.ShouldProcess($TaskName, "install guarded daily backup task")) {
    Set-ScheduledTask -TaskName $TaskName -Action $action -Trigger $trigger -Settings $settings | Out-Null
    if ($Enable) { Enable-ScheduledTask -TaskName $TaskName | Out-Null }
    else { Disable-ScheduledTask -TaskName $TaskName | Out-Null }
}

$task = Get-ScheduledTask -TaskName $TaskName
Write-Host "Backup task configured: $TaskName"
Write-Host "State: $($task.State)"
Write-Host "Principal: $($existing.Principal.UserId)"
Write-Host "Action: $powerShell $arguments"
Write-Host "Daily at: $($DailyAt.ToString('HH:mm'))"
