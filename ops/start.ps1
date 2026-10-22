param([switch]$SkipBuild)
. "$PSScriptRoot/common.ps1"
Initialize-OpsDirectories
Import-OpsSecrets

if ([string]::IsNullOrWhiteSpace($env:AdminApiKey)) {
    throw "AdminApiKey is required. Run ops/configure-secrets.ps1 first."
}

$running = @(Get-ProcessState | Where-Object { Test-ManagedProcess $_ })
if ($running.Count -gt 0) {
    throw "Managed processes are already running: $($running.name -join ', '). Run ops/status.ps1 or ops/stop.ps1."
}

$postgres = Assert-NativePg17
Write-Host $postgres.detail
if (-not $postgres.ready) {
    throw "Native PostgreSQL 17 is not ready. Start it separately; this script never starts a database or Docker."
}

$backendPublish = Join-Path $script:PublishDir "backend"
$backendDll = Join-Path $backendPublish "Backend.dll"
$frontendDir = $script:FrontendDir
$aiDir = $script:AiDir
$python = Join-Path $aiDir "venv/Scripts/python.exe"
if (-not $SkipBuild) {
    Write-Host "Building production artifacts before start. Use -SkipBuild only for an intentional unchanged-artifact restart."
    & "$PSScriptRoot/build.ps1"
}
if (-not (Test-Path -LiteralPath $backendDll)) { throw "Backend publish is missing." }
if (-not (Test-Path -LiteralPath (Join-Path $frontendDir ".next/BUILD_ID"))) { throw "Frontend production build is missing." }
if (-not (Test-Path -LiteralPath $python)) { throw "AI virtualenv missing: $python" }

$llmProvider = $(if ($env:LLM_PROVIDER) { $env:LLM_PROVIDER } else { "none" })
$aiEnvironment = @{ "LLM_PROVIDER" = $llmProvider }
if ($llmProvider -eq "gemini") {
    if ([string]::IsNullOrWhiteSpace($env:GEMINI_API_KEY)) { throw "Gemini API key is not configured." }
    $aiEnvironment["GOOGLE_API_KEY"] = $env:GEMINI_API_KEY
    $aiEnvironment["GEMINI_MODEL"] = $(if ($env:GEMINI_MODEL) { $env:GEMINI_MODEL } else { "gemini-3.8-flash" })
}
if ($llmProvider -eq "openrouter") {
    if ([string]::IsNullOrWhiteSpace($env:OPENROUTER_API_KEY)) { throw "OpenRouter API key is not configured." }
    $aiEnvironment["OPENROUTER_API_KEY"] = $env:OPENROUTER_API_KEY
    $aiEnvironment["OPENROUTER_MODEL"] = $(if ($env:OPENROUTER_MODEL) { $env:OPENROUTER_MODEL } else { "openai/gpt-4o-mini" })
    if ($env:OPENROUTER_BASE_URL) { $aiEnvironment["OPENROUTER_BASE_URL"] = $env:OPENROUTER_BASE_URL }
}

$backendEnvironment = @{
    "ASPNETCORE_ENVIRONMENT" = "ProductionLike"
    "ASPNETCORE_URLS" = "http://127.0.0.1:5197"
    "ConnectionStrings__DefaultConnection" = Get-BackendConnectionString
    "EvidenceCatalog__RootPath" = (Join-Path $script:AiDir "docs/research/evidence")
    # Equity document pipeline paths follow the workspace layout (appsettings defaults are relative to the
    # publish content root, which is not the repo).
    "EquityDocuments__PythonPath" = $python
    "EquityDocuments__PipelineScript" = (Join-Path $aiDir "equity_pipeline.py")
    "EquityDocuments__RootPath" = (Join-Path $script:WorkspaceDir "data/equity-documents")
    "EquityFacts__MetricCatalogPath" = (Join-Path $aiDir "equity_facts/metric_catalog.json")
}
# Host-only, non-secret switches (e.g. EquityIngestion__Enabled, EquityDocuments__PipelineEnabled) live in the
# ignored .ops/backend-settings.json as a flat {"Section__Key": "value"} object; committed defaults stay off.
$hostSettingsPath = Join-Path $script:RuntimeDir "backend-settings.json"
if (Test-Path -LiteralPath $hostSettingsPath) {
    $hostSettings = Get-Content -LiteralPath $hostSettingsPath -Raw | ConvertFrom-Json
    foreach ($property in $hostSettings.PSObject.Properties) {
        if ($property.Name -notmatch '^[A-Za-z][A-Za-z0-9_]*$' -or $property.Name -match '(?i)connectionstrings|password|apikey|secret|^aspnetcore_|^dotnet_') {
            throw "Refusing host setting '$($property.Name)' from $hostSettingsPath (secrets belong in configure-secrets.ps1)."
        }
        $backendEnvironment[$property.Name] = [string]$property.Value
    }
    Write-Host "Applied $(@($hostSettings.PSObject.Properties).Count) host setting(s) from $hostSettingsPath"
}

$processes = @()
try {
    $processes += Start-ManagedProcess "ai" $python @("-m", "uvicorn", "main:app", "--host", "127.0.0.1", "--port", "8000") $aiDir $aiEnvironment
    $processes += Start-ManagedProcess "backend" "dotnet" @("Backend.dll") $backendPublish $backendEnvironment
    $processes += Start-ManagedProcess "frontend" (Join-Path $frontendDir "node_modules/.bin/next.cmd") @("start", "-H", "127.0.0.1", "-p", "3000") $frontendDir @{
        "NODE_ENV" = "production"
        "BACKEND_INTERNAL_URL" = "http://127.0.0.1:5197"
    }
    Save-ProcessState $processes
}
catch {
    $primaryError = $_
    $remaining = @()
    foreach ($entry in $processes) {
        try { Stop-ManagedProcessTree $entry }
        catch { $remaining += $entry; Write-Warning $_.Exception.Message }
    }
    Save-ProcessState $remaining
    throw $primaryError
}

Write-Host "Stack started without database migration. Run ops/status.ps1 for readiness details."
