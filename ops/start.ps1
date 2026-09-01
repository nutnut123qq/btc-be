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
$frontendDir = Join-Path $script:WorkspaceDir "frontend"
$aiDir = Join-Path $script:WorkspaceDir "ai"
$python = Join-Path $aiDir "venv/Scripts/python.exe"
if (-not $SkipBuild) {
    Write-Host "Building production artifacts before start. Use -SkipBuild only for an intentional unchanged-artifact restart."
    & "$PSScriptRoot/build.ps1"
}
if (-not (Test-Path -LiteralPath $backendDll)) { throw "Backend publish is missing." }
if (-not (Test-Path -LiteralPath (Join-Path $frontendDir ".next/BUILD_ID"))) { throw "Frontend production build is missing." }
if (-not (Test-Path -LiteralPath $python)) { throw "AI virtualenv missing: $python" }

$processes = @()
try {
    $processes += Start-ManagedProcess "ai" $python @("-m", "uvicorn", "main:app", "--host", "127.0.0.1", "--port", "8000") $aiDir @{
        "LLM_PROVIDER" = $(if ($env:LLM_PROVIDER) { $env:LLM_PROVIDER } else { "none" })
    }
    $processes += Start-ManagedProcess "backend" "dotnet" @("Backend.dll") $backendPublish @{
        "ASPNETCORE_ENVIRONMENT" = "ProductionLike"
        "ASPNETCORE_URLS" = "http://127.0.0.1:5197"
        "ConnectionStrings__DefaultConnection" = Get-BackendConnectionString
    }
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
