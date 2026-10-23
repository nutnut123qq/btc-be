[CmdletBinding(SupportsShouldProcess)]
param(
    [string]$TaskName = "BTCPaperObserver",
    [ValidateRange(1, 60)][int]$IntervalMinutes = 5,
    [ValidateRange(1, 60)][int]$TimeLimitMinutes = 4,
    [switch]$Enable
)

. "$PSScriptRoot/common.ps1"
if ($TimeLimitMinutes -ge $IntervalMinutes) { throw "TimeLimitMinutes must be shorter than IntervalMinutes so polls cannot overlap." }
$entry = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot "run-paper-observer.ps1"))
foreach ($required in @($entry, (Join-Path $PSScriptRoot "observer-secret.ps1"))) {
    if (-not (Test-Path -LiteralPath $required)) { throw "Ops script not found: $required" }
}
$observerSecret = Join-Path $script:RuntimeDir "observer-db.secret"
if ($Enable -and -not (Test-Path -LiteralPath $observerSecret -PathType Leaf)) {
    throw "Observer secret missing ($observerSecret); run configure-observer-secret.ps1 first."
}
foreach ($required in @("paper_observer.py", "venv/Scripts/python.exe")) {
    if (-not (Test-Path -LiteralPath (Join-Path $script:AiDir $required))) { throw "AI dependency missing: $required" }
}
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
if (-not ([Security.Principal.WindowsPrincipal]$identity).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw "Registering an S4U task requires an elevated session."
}

# S4U batch logon of the deploy user: runs whether or not anyone is logged on, stores no password, keeps the
# DPAPI-protected ops secrets on their existing user scope. Limited token; no other task is touched.
$powerShell = Join-Path $env:SystemRoot "System32\WindowsPowerShell\v1.0\powershell.exe"
$arguments = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -File `"$entry`""
$action = New-ScheduledTaskAction -Execute $powerShell -Argument $arguments -WorkingDirectory $script:BackendDir
# One time trigger repeating forever; Task Scheduler keeps it across reboots and the next 5-minute run is the retry.
$trigger = New-ScheduledTaskTrigger -Once -At (Get-Date).AddMinutes(1) -RepetitionInterval (New-TimeSpan -Minutes $IntervalMinutes)
$settings = New-ScheduledTaskSettingsSet -MultipleInstances IgnoreNew -ExecutionTimeLimit (New-TimeSpan -Minutes $TimeLimitMinutes) `
    -StartWhenAvailable -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries
$principal = New-ScheduledTaskPrincipal -UserId $identity.Name -LogonType S4U -RunLevel Limited

if ($PSCmdlet.ShouldProcess($TaskName, "install paper observer task")) {
    # -Force replaces an existing definition completely (principal included), so an upgrade cannot keep an old logon type.
    Register-ScheduledTask -TaskName $TaskName -Action $action -Trigger $trigger -Settings $settings -Principal $principal `
        -Description "Forward-paper fill/outcome observer (paper_observer.py --once). Writes only live-quote fills/outcomes." `
        -Force -ErrorAction Stop | Out-Null
    if ($Enable) { Enable-ScheduledTask -TaskName $TaskName -ErrorAction Stop | Out-Null }
    else { Disable-ScheduledTask -TaskName $TaskName -ErrorAction Stop | Out-Null }

    $task = Get-ScheduledTask -TaskName $TaskName -ErrorAction Stop
    $problems = @()
    if ($task.Principal.LogonType -ne "S4U") { $problems += "logon=$($task.Principal.LogonType)" }
    if ($task.Principal.RunLevel -ne "Limited") { $problems += "runLevel=$($task.Principal.RunLevel)" }
    if (@($task.Actions).Count -ne 1 -or $task.Actions[0].Arguments -ne $arguments) { $problems += "action" }
    if (@($task.Triggers).Count -ne 1 -or $task.Triggers[0].Repetition.Interval -ne "PT$($IntervalMinutes)M") { $problems += "trigger" }
    if ($task.Settings.MultipleInstances -ne "IgnoreNew" -or $task.Settings.ExecutionTimeLimit -ne "PT$($TimeLimitMinutes)M") { $problems += "settings" }
    if ($Enable -and $task.State -eq "Disabled") { $problems += "state=Disabled" }
    if ($problems) { throw "Task $TaskName does not match the intended definition: $($problems -join ', ')" }
}

$task = Get-ScheduledTask -TaskName $TaskName
Write-Host "Paper observer task configured: $TaskName"
Write-Host "State: $($task.State)"
Write-Host "Principal: $($task.Principal.UserId) ($($task.Principal.LogonType), $($task.Principal.RunLevel))"
Write-Host "Action: $powerShell $arguments"
Write-Host "Every $IntervalMinutes min, time limit $TimeLimitMinutes min, MultipleInstances=IgnoreNew"
