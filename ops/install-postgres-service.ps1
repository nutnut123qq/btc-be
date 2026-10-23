# Prepare (never start) a Windows service for the EXISTING PostgreSQL 17 cluster so it runs without a logged-on
# session. Same binaries, data directory, port and config; no initdb/upgrade. Identity: NT AUTHORITY\NetworkService
# (built-in, no password, not an administrator). The per-service virtual account NT SERVICE\<name> was rejected by
# this Windows 10 Pro host (sc 1057 / WMI 22). NetworkService is shared by other services, so it is granted only
# Modify on the data directory and Read/Execute on the PostgreSQL install. Switching over (stop the session-bound
# postmaster, then start the service) is a separate, supervised step: see ops/README.md "PostgreSQL service".
[CmdletBinding()]
param(
    [string]$ServiceName = "postgresql-btc-17",
    [string]$DataDirectory = "E:\PostgreSQL\17-link\data",
    [string]$InstallDirectory = "E:\Runtime\PostgreSQL\17",
    [string]$Account = "NT AUTHORITY\NetworkService"
)
$ErrorActionPreference = "Stop"
if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw "Run elevated."
}
if ($ServiceName -notmatch '^[A-Za-z0-9_-]{1,64}$') { throw "Unsafe service name." }
$pgCtl = Join-Path $InstallDirectory "bin\pg_ctl.exe"
if (-not (Test-Path -LiteralPath $pgCtl)) { throw "pg_ctl.exe not found: $pgCtl" }
if ((& $pgCtl --version) -notmatch '\(PostgreSQL\) 17\.') { throw "pg_ctl is not PostgreSQL 17." }
$versionFile = Join-Path $DataDirectory "PG_VERSION"
if (-not (Test-Path -LiteralPath $versionFile) -or (Get-Content -LiteralPath $versionFile -TotalCount 1).Trim() -ne "17") {
    throw "Not a PostgreSQL 17 data directory: $DataDirectory"
}

$service = Get-CimInstance Win32_Service -Filter "Name='$ServiceName'"
if ($service -and $service.State -ne "Stopped") { throw "Service $ServiceName exists and is $($service.State); refusing to reconfigure a running service." }
if (-not $service) {
    & $pgCtl register -N $ServiceName -D $DataDirectory -S auto -w -t 120
    if ($LASTEXITCODE -ne 0) { throw "pg_ctl register failed ($LASTEXITCODE)." }
}
# WMI instead of sc.exe: Windows PowerShell 5.1 mangles the empty/quoted password= argument for native commands.
$change = Invoke-CimMethod -InputObject (Get-CimInstance Win32_Service -Filter "Name='$ServiceName'") -MethodName Change `
    -Arguments @{ StartName = $Account; StartPassword = ""; StartMode = "Automatic" }
if ($change.ReturnValue -ne 0) { throw "Win32_Service.Change failed ($($change.ReturnValue))." }
# Finite recovery: two restarts (1 min, 2 min), then stay stopped; counter resets after a day.
& sc.exe failure $ServiceName reset= 86400 actions= restart/60000/restart/120000//0 | Out-Null
if ($LASTEXITCODE -ne 0) { throw "sc failure failed ($LASTEXITCODE)." }
& sc.exe description $ServiceName "PostgreSQL 17 cluster $DataDirectory (btc ops)" | Out-Null

$sid = ([Security.Principal.NTAccount]$Account).Translate([Security.Principal.SecurityIdentifier]).Value
& icacls.exe $DataDirectory /grant "*$sid`:(OI)(CI)M" | Out-Null
if ($LASTEXITCODE -ne 0) { throw "Could not grant data directory access." }
& icacls.exe $InstallDirectory /grant "*$sid`:(OI)(CI)RX" | Out-Null
if ($LASTEXITCODE -ne 0) { throw "Could not grant install directory access." }

$service = Get-CimInstance Win32_Service -Filter "Name='$ServiceName'"
$pathMatches = $service.PathName.Replace('\', '/') -match [Regex]::Escape($DataDirectory.Replace('\', '/'))
if (($service.StartName -ne $Account) -or ($service.StartMode -ne "Auto") -or ($service.State -ne "Stopped") -or (-not $pathMatches)) {
    throw "Service $ServiceName does not match the intended definition: $($service.StartName) $($service.StartMode) $($service.State) $($service.PathName)"
}
Write-Host "Prepared $ServiceName (stopped): account=$Account sid=$sid start=Auto"
Write-Host "Binary: $($service.PathName)"
