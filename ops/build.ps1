param([switch]$SkipDependencies)
. "$PSScriptRoot/common.ps1"
Initialize-OpsDirectories

dotnet publish (Join-Path $script:BackendDir "Backend.csproj") -c Release -o (Join-Path $script:PublishDir "backend") /p:UseAppHost=false
if ($LASTEXITCODE -ne 0) { throw "Backend publish failed." }

if (-not $SkipDependencies) {
    Push-Location (Join-Path $script:WorkspaceDir "frontend")
    try {
        npm ci
        if ($LASTEXITCODE -ne 0) { throw "Frontend npm ci failed." }
        npm run build
        if ($LASTEXITCODE -ne 0) { throw "Frontend production build failed." }
    }
    finally { Pop-Location }

    $python = Join-Path $script:WorkspaceDir "ai/venv/Scripts/python.exe"
    if (-not (Test-Path -LiteralPath $python)) { throw "AI virtualenv not found: $python" }
    & $python -m pip check
    if ($LASTEXITCODE -ne 0) { throw "AI dependency check failed." }
}

Write-Host "Production-like artifacts are ready under $script:RuntimeDir"
