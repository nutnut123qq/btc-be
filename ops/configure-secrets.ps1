param(
    [string]$DatabasePassword = $env:PGPASSWORD,
    [string]$AdminKey = $env:AdminApiKey,
    [switch]$PromptForGeminiApiKey
)
. "$PSScriptRoot/common.ps1"
Initialize-OpsDirectories

if ([string]::IsNullOrWhiteSpace($DatabasePassword)) { $DatabasePassword = $env:PGPASSWORD }
if ([string]::IsNullOrWhiteSpace($AdminKey)) { $AdminKey = $env:AdminApiKey }
if ([string]::IsNullOrWhiteSpace($DatabasePassword)) { throw "DatabasePassword or PGPASSWORD is required." }
if ([string]::IsNullOrWhiteSpace($AdminKey)) {
    $bytes = New-Object byte[] 32
    $generator = [Security.Cryptography.RandomNumberGenerator]::Create()
    try { $generator.GetBytes($bytes) }
    finally { $generator.Dispose() }
    $AdminKey = ConvertTo-OpsHexString $bytes
}

$existingGeminiApiKey = $null
if (Test-Path -LiteralPath $script:SecretsPath) {
    $existingSecrets = Import-Clixml -LiteralPath $script:SecretsPath
    $existingProperty = $existingSecrets.PSObject.Properties["GEMINI_API_KEY"]
    if ($existingProperty -and $existingProperty.Value -is [Security.SecureString]) {
        $existingGeminiApiKey = $existingProperty.Value
    }
}
$geminiApiKey = if ($PromptForGeminiApiKey) {
    Read-Host "Gemini API key" -AsSecureString
} else {
    $existingGeminiApiKey
}
if ($PromptForGeminiApiKey -and $geminiApiKey.Length -eq 0) { throw "Gemini API key cannot be empty." }

$protectedSecrets = [ordered]@{
    PGHOST = $(if ($env:PGHOST) { $env:PGHOST } else { "127.0.0.1" })
    PGPORT = $(if ($env:PGPORT) { $env:PGPORT } else { "5432" })
    PGUSER = $(if ($env:PGUSER) { $env:PGUSER } else { "postgres" })
    PGDATABASE = $(if ($env:PGDATABASE) { $env:PGDATABASE } else { "bitcoin_analyst" })
    PGPASSWORD = ConvertTo-SecureString $DatabasePassword -AsPlainText -Force
    DB_PASS = ConvertTo-SecureString $DatabasePassword -AsPlainText -Force
    AdminApiKey = ConvertTo-SecureString $AdminKey -AsPlainText -Force
    LLM_PROVIDER = $(if ($env:LLM_PROVIDER) { $env:LLM_PROVIDER } else { "none" })
}
if ($geminiApiKey) { $protectedSecrets.GEMINI_API_KEY = $geminiApiKey }
[pscustomobject]$protectedSecrets | Export-Clixml -LiteralPath $script:SecretsPath -Force
Set-OpsSecretsFileAcl $script:SecretsPath
Write-Host "Protected local runtime secrets configured at $script:SecretsPath"
