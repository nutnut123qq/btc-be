param([Parameter(Mandatory)][string]$BaseRevision)
. "$PSScriptRoot/common.ps1"

if ($BaseRevision -match '^0+$') {
    $parent = git -C $script:BackendDir rev-parse HEAD^ 2>$null
    if ($LASTEXITCODE -ne 0) { Write-Host "Initial push has no contract baseline; version comparison skipped."; return }
    $BaseRevision = $parent.Trim()
}

$currentPath = Join-Path $script:BackendDir "contracts/openapi.json"
if (-not (Test-Path -LiteralPath $currentPath)) { throw "Current OpenAPI contract is missing." }
$baseText = git -C $script:BackendDir show "$BaseRevision`:contracts/openapi.json" 2>$null
if ($LASTEXITCODE -ne 0) {
    Write-Host "No OpenAPI contract exists at $BaseRevision; version comparison skipped."
    return
}
$currentText = Get-Content -LiteralPath $currentPath -Raw
$baseJoined = ($baseText -join "`n") + "`n"
if ($currentText -eq $baseJoined) { Write-Host "OpenAPI contract is unchanged from $BaseRevision."; return }

$currentVersion = ($currentText | ConvertFrom-Json).info.version
$baseVersion = ($baseJoined | ConvertFrom-Json).info.version
if ($currentVersion -eq $baseVersion) {
    throw "OpenAPI changed without incrementing apiContractVersion ($currentVersion)."
}
Write-Host "OpenAPI changed with apiContractVersion $baseVersion -> $currentVersion."
