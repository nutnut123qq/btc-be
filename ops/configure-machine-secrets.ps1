# One-time (re)creation of the machine-scope ops secret used by unattended S4U tasks (watchdog/autostart, paper,
# backup), from the deploy user's existing protected ops secrets. Contains only the database connection, admin key and
# LLM_PROVIDER — never paid API keys. DPAPI LocalMachine: any local process can decrypt it, so the ACL (SYSTEM,
# Administrators, deploy user read) is the boundary. Prints no secret. Re-run after rotate-db-password.ps1.
param([string]$ReaderAccount)
. "$PSScriptRoot/common.ps1"
Initialize-OpsDirectories
foreach ($name in @("PGPASSWORD", "AdminApiKey", "PGDATABASE")) {
    if ([string]::IsNullOrEmpty([Environment]::GetEnvironmentVariable($name, "Process"))) { throw "$name is not available from the protected ops secrets." }
}
$reader = if ($ReaderAccount) { ([Security.Principal.NTAccount]$ReaderAccount).Translate([Security.Principal.SecurityIdentifier]).Value }
          else { [Security.Principal.WindowsIdentity]::GetCurrent().User.Value }
$values = [ordered]@{ schema = "btc-ops-machine-secrets/v1" }
foreach ($name in $script:MachineSecretNames) { $values[$name] = [Environment]::GetEnvironmentVariable($name, "Process") }
if (-not $values.DB_PASS) { $values.DB_PASS = $values.PGPASSWORD }
if (-not $values.LLM_PROVIDER) { $values.LLM_PROVIDER = "none" }

Add-Type -AssemblyName System.Security
$protected = [Security.Cryptography.ProtectedData]::Protect([Text.Encoding]::UTF8.GetBytes(($values | ConvertTo-Json -Compress)),
    (Get-OpsMachineSecretEntropy), [Security.Cryptography.DataProtectionScope]::LocalMachine)
$path = $script:MachineSecretsPath
$temp = "$path.tmp-$([Guid]::NewGuid().ToString('N'))"
try {
    [IO.File]::WriteAllBytes($temp, $protected)
    & icacls.exe $temp /inheritance:r /grant:r "*S-1-5-18:(F)" "*S-1-5-32-544:(F)" "*$reader`:(R)" | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Could not restrict the machine secret ACL." }
    Move-Item -LiteralPath $temp -Destination $path -Force
}
finally { Remove-Item -LiteralPath $temp -Force -ErrorAction SilentlyContinue }
$check = Read-OpsMachineSecrets $path
foreach ($name in $script:MachineSecretNames) { if ([string]$check.PSObject.Properties[$name].Value -ne [string]$values[$name]) { throw "Machine secret round-trip check failed ($name)." } }
Write-Host "Machine-scope ops secret written: $path (DPAPI LocalMachine; ACL SYSTEM, Administrators, reader $reader; database $($values.PGDATABASE))"
