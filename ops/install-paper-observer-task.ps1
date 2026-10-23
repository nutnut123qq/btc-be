[CmdletBinding(SupportsShouldProcess)]
param(
    [string]$TaskName = "BTCPaperObserver",
    [ValidateRange(1, 60)][int]$IntervalMinutes = 5,
    [ValidateRange(1, 60)][int]$TimeLimitMinutes = 4,
    [switch]$Enable
)

. "$PSScriptRoot/common.ps1"
if ($TimeLimitMinutes -ge $IntervalMinutes) { throw "TimeLimitMinutes must be shorter than IntervalMinutes so polls cannot overlap." }
$jobScript = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot "run-ai-job.ps1"))
if (-not (Test-Path -LiteralPath $jobScript)) { throw "Job entrypoint not found: $jobScript" }
foreach ($required in @("paper_observer.py", "venv/Scripts/python.exe")) {
    if (-not (Test-Path -LiteralPath (Join-Path $script:AiDir $required))) { throw "AI dependency missing: $required" }
}

# Same action shape and principal model as the other application tasks: inbox PowerShell, interactive
# logon of the deploy user (DPAPI secrets decrypt only for that user), no password on the command line.
$powerShell = Join-Path $env:SystemRoot "System32\WindowsPowerShell\v1.0\powershell.exe"
$arguments = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -File `"$jobScript`" -Job PaperObserver"
$action = New-ScheduledTaskAction -Execute $powerShell -Argument $arguments -WorkingDirectory $script:BackendDir
$trigger = New-ScheduledTaskTrigger -Once -At (Get-Date).AddMinutes(1) -RepetitionInterval (New-TimeSpan -Minutes $IntervalMinutes)
$settings = New-ScheduledTaskSettingsSet -MultipleInstances IgnoreNew -ExecutionTimeLimit (New-TimeSpan -Minutes $TimeLimitMinutes) `
    -StartWhenAvailable -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries
$principal = New-ScheduledTaskPrincipal -UserId ([Security.Principal.WindowsIdentity]::GetCurrent().Name) -LogonType Interactive -RunLevel Limited

if ($PSCmdlet.ShouldProcess($TaskName, "install paper observer task")) {
    if (Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue) {
        Set-ScheduledTask -TaskName $TaskName -Action $action -Trigger $trigger -Settings $settings | Out-Null
    }
    else {
        Register-ScheduledTask -TaskName $TaskName -Action $action -Trigger $trigger -Settings $settings -Principal $principal `
            -Description "Forward-paper fill/outcome observer (paper_observer.py --once). Writes only live-quote fills/outcomes." | Out-Null
    }
    if ($Enable) { Enable-ScheduledTask -TaskName $TaskName | Out-Null }
    else { Disable-ScheduledTask -TaskName $TaskName | Out-Null }
}

$task = Get-ScheduledTask -TaskName $TaskName
Write-Host "Paper observer task configured: $TaskName"
Write-Host "State: $($task.State)"
Write-Host "Principal: $($task.Principal.UserId) ($($task.Principal.LogonType), $($task.Principal.RunLevel))"
Write-Host "Action: $powerShell $arguments"
Write-Host "Every $IntervalMinutes min, time limit $TimeLimitMinutes min, MultipleInstances=IgnoreNew"
