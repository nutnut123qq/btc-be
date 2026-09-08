. "$PSScriptRoot/common.ps1"

if ([string]::IsNullOrWhiteSpace($env:PGPASSWORD) -or [string]::IsNullOrWhiteSpace($env:AdminApiKey)) {
    throw "Protected ops secrets were not imported."
}
if ((ConvertTo-OpsHexString ([byte[]]@(0, 255))) -ne "00FF") { throw "Hex conversion failed." }

$originalSecretsPath = $script:SecretsPath
$originalGeminiApiKey = [Environment]::GetEnvironmentVariable("GEMINI_API_KEY", "Process")
$originalGeminiModel = [Environment]::GetEnvironmentVariable("GEMINI_MODEL", "Process")
$testSecretsPath = Join-Path ([IO.Path]::GetTempPath()) "btc-ops-secrets-$([Guid]::NewGuid().ToString('N')).clixml"
try {
    $testSecrets = [pscustomobject]@{
        PGPASSWORD = ConvertTo-SecureString "test-password" -AsPlainText -Force
        DB_PASS = ConvertTo-SecureString "test-password" -AsPlainText -Force
        AdminApiKey = ConvertTo-SecureString "test-admin-key" -AsPlainText -Force
        PGHOST = "127.0.0.1"
        PGPORT = "5432"
        PGUSER = "postgres"
        PGDATABASE = "bitcoin_analyst"
        LLM_PROVIDER = "none"
        GEMINI_MODEL = "gemini-3.8-flash"
    }
    $testSecrets | Export-Clixml -LiteralPath $testSecretsPath
    $script:SecretsPath = $testSecretsPath
    [Environment]::SetEnvironmentVariable("GEMINI_API_KEY", $null, "Process")
    [Environment]::SetEnvironmentVariable("GEMINI_MODEL", $null, "Process")
    Import-OpsSecrets
    if ($env:GEMINI_API_KEY) { throw "Legacy secret import invented a Gemini key." }

    $testSecrets | Add-Member -NotePropertyName GEMINI_API_KEY -NotePropertyValue (ConvertTo-SecureString "test-gemini-key" -AsPlainText -Force)
    $testSecrets | Export-Clixml -LiteralPath $testSecretsPath
    Import-OpsSecrets
    if ($env:GEMINI_API_KEY -ne "test-gemini-key") { throw "Optional Gemini secret import failed." }
    if ($env:GEMINI_MODEL -ne "gemini-3.8-flash") { throw "Gemini model import failed." }
}
finally {
    $script:SecretsPath = $originalSecretsPath
    [Environment]::SetEnvironmentVariable("GEMINI_API_KEY", $originalGeminiApiKey, "Process")
    [Environment]::SetEnvironmentVariable("GEMINI_MODEL", $originalGeminiModel, "Process")
    if (Test-Path -LiteralPath $testSecretsPath) { [IO.File]::Delete($testSecretsPath) }
}

$python = Join-Path $script:AiDir "venv/Scripts/python.exe"
$missingWorkspace = Join-Path ([IO.Path]::GetTempPath()) "btc-ops-$([Guid]::NewGuid().ToString('N'))"
$fallback = Resolve-OpsComponentDirectory $missingWorkspace "frontend" "btc-fe"
if ($fallback -ne (Join-Path $missingWorkspace "btc-fe")) { throw "Sibling component fallback failed." }
$arguments = @(
    "-c", "import json,sys; print(json.dumps(sys.argv[1:]))",
    "plain", "two words", 'quote"inside', "C:\path with space\", ""
)
$expected = @("plain", "two words", 'quote"inside', "C:\path with space\", "")
$result = Invoke-BoundedProcess $python $arguments 10
if ($result.ExitCode -ne 0) { throw "Argument subprocess failed: $($result.Error)" }
$actual = @()
foreach ($item in ($result.Output.Trim() | ConvertFrom-Json)) { $actual += $item }
if (Compare-Object $expected $actual -SyncWindow 0) { throw "Native argument round-trip failed." }

$stdinResult = Invoke-BoundedProcess $python @("-c", "import sys; print(sys.stdin.read())") 10 -StandardInput "stdin round-trip"
if ($stdinResult.ExitCode -ne 0 -or $stdinResult.Output.Trim() -ne "stdin round-trip") {
    throw "Native standard-input round-trip failed: $($stdinResult.Error)"
}

Write-Host "Ops common self-test passed under PowerShell $($PSVersionTable.PSVersion)."
