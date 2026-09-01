. "$PSScriptRoot/common.ps1"
$output = dotnet list (Join-Path $script:BackendDir "Backend.csproj") package --vulnerable --include-transitive --format json
if ($LASTEXITCODE -ne 0) { throw "NuGet vulnerability scan failed." }
$json = $output -join "`n"
Write-Output $json
if ($json -match '"severity"\s*:') { throw "Vulnerable NuGet package detected." }
Write-Host "NuGet vulnerability scan passed."
