# One-time (re)creation of the paper observer's machine-scope DB secret from the existing protected ops secrets.
# Run elevated as the deploy user while that user's DPAPI secrets are readable. Prints no secret.
param([string]$ReaderAccount)
. "$PSScriptRoot/common.ps1"
. "$PSScriptRoot/observer-secret.ps1"
if ([string]::IsNullOrEmpty($env:DB_PASS)) { throw "DB_PASS is not available from the protected ops secrets." }
$reader = if ($ReaderAccount) { ([Security.Principal.NTAccount]$ReaderAccount).Translate([Security.Principal.SecurityIdentifier]).Value }
          else { [Security.Principal.WindowsIdentity]::GetCurrent().User.Value }
$path = Join-Path $script:RuntimeDir "observer-db.secret"
New-ObserverSecretFile -Path $path -Password (ConvertTo-SecureString $env:DB_PASS -AsPlainText -Force) -ReaderSid $reader
if ((Read-ObserverDbPassword $path) -ne $env:DB_PASS) { throw "Observer secret round-trip check failed." }
Write-Host "Observer secret written: $path (DPAPI LocalMachine; ACL SYSTEM, Administrators, reader $reader)"
