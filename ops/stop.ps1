. "$PSScriptRoot/common.ps1"

$state = @(Get-ProcessState)
$remaining = @()
$failures = @()
foreach ($entry in @($state | Sort-Object { @("frontend", "backend", "ai").IndexOf($_.name) })) {
    if (Test-ManagedProcess $entry) {
        try {
            Stop-ManagedProcessTree $entry
            Write-Host "Stopped $($entry.name) (PID $($entry.pid))."
        }
        catch { $remaining += $entry; $failures += $_.Exception.Message }
    }
    elseif (Get-Process -Id $entry.pid -ErrorAction SilentlyContinue) {
        $remaining += $entry
        $failures += "Refused reused PID $($entry.pid) for $($entry.name)."
    }
    else { Write-Host "$($entry.name) is not running." }
}
Save-ProcessState $remaining
if ($failures.Count -gt 0) { throw ($failures -join " ") }
