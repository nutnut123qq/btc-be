param(
    [string]$DatabasePassword = $env:PGPASSWORD,
    [string]$AdminKey = $env:AdminApiKey
)
. "$PSScriptRoot/common.ps1"
Initialize-OpsDirectories

if ([string]::IsNullOrWhiteSpace($DatabasePassword)) { throw "DatabasePassword or PGPASSWORD is required." }
if ([string]::IsNullOrWhiteSpace($AdminKey)) {
    $AdminKey = [Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
}

[pscustomobject]@{
    PGHOST = $(if ($env:PGHOST) { $env:PGHOST } else { "127.0.0.1" })
    PGPORT = $(if ($env:PGPORT) { $env:PGPORT } else { "5432" })
    PGUSER = $(if ($env:PGUSER) { $env:PGUSER } else { "postgres" })
    PGDATABASE = $(if ($env:PGDATABASE) { $env:PGDATABASE } else { "bitcoin_analyst" })
    PGPASSWORD = ConvertTo-SecureString $DatabasePassword -AsPlainText -Force
    DB_PASS = ConvertTo-SecureString $DatabasePassword -AsPlainText -Force
    AdminApiKey = ConvertTo-SecureString $AdminKey -AsPlainText -Force
    LLM_PROVIDER = $(if ($env:LLM_PROVIDER) { $env:LLM_PROVIDER } else { "none" })
} | Export-Clixml -LiteralPath $script:SecretsPath -Force

& icacls.exe $script:SecretsPath /inheritance:r /grant:r "$env:USERNAME`:(F)" "SYSTEM`:(F)" | Out-Null
if ($LASTEXITCODE -ne 0) { throw "Could not restrict the protected secret file ACL." }
Write-Host "Protected local runtime secrets configured at $script:SecretsPath"
