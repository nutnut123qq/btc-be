param([Parameter(Mandatory)][string]$BackupPath)
. "$PSScriptRoot/common.ps1"
Import-OpsSecrets

if (@(Get-ProcessState | Where-Object { Test-ManagedProcess $_ }).Count -gt 0) {
    throw "Stop the managed stack before migration."
}
$postgres = Assert-NativePg17

& "$PSScriptRoot/restore-verify.ps1" -BackupPath $BackupPath -Mode ListOnly
if ($LASTEXITCODE -ne 0) { throw "Backup verification failed; migration was not started." }
$dumpPath = [IO.Path]::GetFullPath($BackupPath)
$manifestPath = Join-Path (Split-Path $dumpPath -Parent) "$([IO.Path]::GetFileNameWithoutExtension($dumpPath)).manifest.json"
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
if ($manifest.source.host -ne $postgres.host -or [string]$manifest.source.port -ne [string]$postgres.port `
    -or $manifest.source.database -ne $postgres.database -or $manifest.source.serverVersion -ne $postgres.serverVersion `
    -or $manifest.source.dataDirectory -ne $postgres.dataDirectory) {
    throw "Backup source identity does not match the validated migration target."
}

Push-Location $script:BackendDir
try {
    dotnet tool restore
    if ($LASTEXITCODE -ne 0) { throw "dotnet tool restore failed." }
    $oldEnvironment = $env:ASPNETCORE_ENVIRONMENT
    $oldConnection = $env:ConnectionStrings__DefaultConnection
    try {
        $env:ASPNETCORE_ENVIRONMENT = "ProductionLike"
        $env:ConnectionStrings__DefaultConnection = Get-BackendConnectionString
        dotnet tool run dotnet-ef database update --project Backend.csproj --startup-project Backend.csproj
        if ($LASTEXITCODE -ne 0) { throw "Database migration failed." }
    }
    finally {
        $env:ASPNETCORE_ENVIRONMENT = $oldEnvironment
        $env:ConnectionStrings__DefaultConnection = $oldConnection
    }
}
finally { Pop-Location }
