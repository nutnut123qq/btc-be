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

# Host profile gate: missing file, partial profile and non-boolean values fail closed; a complete profile passes.
$profileRoot = Join-Path ([IO.Path]::GetTempPath()) "btc-ops-profile-$([Guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory -Force -Path $profileRoot | Out-Null
try {
    $profilePath = Join-Path $profileRoot "backend-settings.json"
    $failed = $false; try { Assert-HostProfile $profilePath } catch { $failed = $true }
    if (-not $failed) { throw "Missing host profile was accepted." }
    $complete = [ordered]@{ BackgroundWorkers__Enabled = "true"; Alerts__WorkerEnabled = "false"; BinanceTestnet__StreamEnabled = "false"; EquityDocuments__PipelineEnabled = "false" }
    foreach ($worker in @("KlinesIngestionWorker", "IndexingBackgroundWorker", "CausalSmartMoneyRebuildWorker", "RssIngestionService",
            "EmbeddingBackfillWorker", "MlDatasetBuilder", "WindowDatasetBuilder")) { $complete["BackgroundWorkers__$($worker)__Enabled"] = "false" }
    $complete | ConvertTo-Json | Set-Content -LiteralPath $profilePath -Encoding utf8
    Assert-HostProfile $profilePath
    foreach ($broken in @(@{ drop = "BackgroundWorkers__RssIngestionService__Enabled" }, @{ set = "yes" })) {
        $partial = [ordered]@{} + $complete
        if ($broken.ContainsKey("drop")) { $partial.Remove($broken.drop) } else { $partial["BackgroundWorkers__EmbeddingBackfillWorker__Enabled"] = $broken.set }
        $partial | ConvertTo-Json | Set-Content -LiteralPath $profilePath -Encoding utf8
        $failed = $false; try { Assert-HostProfile $profilePath } catch { $failed = $true }
        if (-not $failed) { throw "Incomplete host profile was accepted." }
    }
    @{ BackgroundWorkers__Enabled = "false" } | ConvertTo-Json | Set-Content -LiteralPath $profilePath -Encoding utf8
    Assert-HostProfile $profilePath

    # Frontend switch: only a JSON boolean false disables it; missing/unreadable/other values keep the default on.
    $stackPath = Join-Path $profileRoot "stack-settings.json"
    if (-not (Test-OpsFrontendEnabled $stackPath)) { throw "Missing stack settings disabled the frontend." }
    foreach ($case in @(@{ text = '{"frontendEnabled": false}'; want = $false }, @{ text = '{"frontendEnabled": true}'; want = $true },
            @{ text = '{"frontendEnabled": "false"}'; want = $true }, @{ text = '{"frontendEnabled": 0}'; want = $true },
            @{ text = '{}'; want = $true }, @{ text = 'null'; want = $true }, @{ text = '[false]'; want = $true }, @{ text = '{not json'; want = $true })) {
        [IO.File]::WriteAllText($stackPath, $case.text)
        if ((Test-OpsFrontendEnabled $stackPath) -ne $case.want) { throw "Stack settings '$($case.text)' gave frontendEnabled != $($case.want)." }
    }

    # Machine-scope fallback is used only when the user-scope file cannot be read, and never carries API keys.
    $originalMachinePath = $script:MachineSecretsPath
    $saved = @{}; foreach ($name in $script:MachineSecretNames + @("OPENROUTER_API_KEY")) { $saved[$name] = [Environment]::GetEnvironmentVariable($name, "Process") }
    try {
        $script:SecretsPath = Join-Path $profileRoot "unreadable.clixml"
        [IO.File]::WriteAllText($script:SecretsPath, "not clixml")
        $script:MachineSecretsPath = Join-Path $profileRoot "machine-secrets.bin"
        Add-Type -AssemblyName System.Security
        $json = @{ schema = "btc-ops-machine-secrets/v1"; PGPASSWORD = "machine-pass"; AdminApiKey = "machine-admin"; PGDATABASE = "machine_db" } | ConvertTo-Json -Compress
        [IO.File]::WriteAllBytes($script:MachineSecretsPath, [Security.Cryptography.ProtectedData]::Protect([Text.Encoding]::UTF8.GetBytes($json),
            (Get-OpsMachineSecretEntropy), [Security.Cryptography.DataProtectionScope]::LocalMachine))
        foreach ($name in $saved.Keys) { [Environment]::SetEnvironmentVariable($name, $null, "Process") }
        Import-OpsSecrets
        if ($env:PGPASSWORD -ne "machine-pass" -or $env:PGDATABASE -ne "machine_db" -or $env:OPENROUTER_API_KEY) { throw "Machine-scope secret fallback failed." }
        Remove-Item -LiteralPath $script:MachineSecretsPath
        $failed = $false; try { Import-OpsSecrets } catch { $failed = $true }
        if (-not $failed) { throw "Unreadable user secrets without a machine secret must fail." }
    }
    finally {
        $script:SecretsPath = $originalSecretsPath
        $script:MachineSecretsPath = $originalMachinePath
        foreach ($name in $saved.Keys) { [Environment]::SetEnvironmentVariable($name, $saved[$name], "Process") }
    }
}
finally { Remove-Item -LiteralPath $profileRoot -Recurse -Force -ErrorAction SilentlyContinue }

if ($null -eq (Get-ProcessStartUtcTicks $PID)) { throw "Process start time lookup failed." }
if ($null -ne (Get-ProcessStartUtcTicks 999999)) { throw "Missing PID reported a start time." }
Grant-OpsUserProcessAccess
# Process tree: a managed root with a child is enumerated and stopped completely, and state is not trusted for reused PIDs.
$treeRoot = Start-Process -FilePath "$env:SystemRoot\System32\cmd.exe" -ArgumentList "/c", "ping -n 30 127.0.0.1 >nul" -WindowStyle Hidden -PassThru
Start-Sleep -Seconds 1
$treeRoot.Refresh()
$treeEntry = [pscustomobject]@{ name = "self-test"; pid = $treeRoot.Id; startedAtUtcTicks = $treeRoot.StartTime.ToUniversalTime().Ticks }
$treeIds = @(Get-ProcessTreeIds $treeRoot.Id)
if ($treeIds.Count -lt 2 -or $treeIds[0] -ne $treeRoot.Id) { throw "Process tree enumeration missed the child." }
Stop-ManagedProcessTree $treeEntry
if (@($treeIds | Where-Object { Get-Process -Id $_ -ErrorAction SilentlyContinue }).Count -gt 0) { throw "Process tree was not fully stopped." }

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
