param(
    [switch]$Check,
    [switch]$NoBuild
)
. "$PSScriptRoot/common.ps1"
Initialize-OpsDirectories

if (-not $NoBuild) {
    dotnet build (Join-Path $script:BackendDir "Backend.csproj") -c Release
    if ($LASTEXITCODE -ne 0) { throw "Backend build failed." }
}

$assemblyDir = Join-Path $script:BackendDir "bin/Release/net8.0"
$assembly = Join-Path $assemblyDir "Backend.dll"
if (-not (Test-Path -LiteralPath $assembly)) { throw "Release assembly missing: $assembly" }

$listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
$listener.Start()
$port = ([Net.IPEndPoint]$listener.LocalEndpoint).Port
$listener.Stop()
$url = "http://127.0.0.1:$port"
$tempContract = Join-Path $script:RuntimeDir "openapi.generated.json"
$stdout = Join-Path $script:LogsDir "contract.out.log"
$stderr = Join-Path $script:LogsDir "contract.err.log"
$oldEnvironment = $env:ASPNETCORE_ENVIRONMENT
$oldWorkers = $env:BackgroundWorkers__Enabled
$process = $null

try {
    $env:ASPNETCORE_ENVIRONMENT = "ContractGeneration"
    $env:BackgroundWorkers__Enabled = "false"
    $process = Start-Process dotnet -ArgumentList @("Backend.dll", "--urls", $url) `
        -WorkingDirectory $assemblyDir -PassThru `
        -RedirectStandardOutput $stdout -RedirectStandardError $stderr

    $content = $null
    for ($attempt = 0; $attempt -lt 50 -and $null -eq $content; $attempt++) {
        if ($process.HasExited) { throw "Contract host exited early. See $stderr" }
        try { $content = (Invoke-WebRequest "$url/swagger/v1/swagger.json" -TimeoutSec 2 -UseBasicParsing).Content }
        catch { Start-Sleep -Milliseconds 100 }
    }
    if ($null -eq $content) { throw "Timed out generating OpenAPI contract. See $stderr" }
    [IO.File]::WriteAllText($tempContract, $content.TrimEnd() + "`n", [Text.UTF8Encoding]::new($false))
}
finally {
    if ($process -and -not $process.HasExited) {
        Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
        Wait-Process -Id $process.Id -Timeout 10 -ErrorAction SilentlyContinue
    }
    $env:ASPNETCORE_ENVIRONMENT = $oldEnvironment
    $env:BackgroundWorkers__Enabled = $oldWorkers
}

$contractDir = Join-Path $script:BackendDir "contracts"
$contractPath = Join-Path $contractDir "openapi.json"
$versionSource = Get-Content -LiteralPath (Join-Path $script:BackendDir "Data/ResearchRecordMetadata.cs") -Raw
if ($versionSource -notmatch 'ApiContract\s*=\s*"([^"]+)"') { throw "Could not read apiContractVersion from ResearchRecordMetadata.cs." }
$expectedVersion = $Matches[1]
$generatedVersion = (Get-Content -LiteralPath $tempContract -Raw | ConvertFrom-Json).info.version
if ($generatedVersion -ne $expectedVersion) {
    throw "Generated OpenAPI version '$generatedVersion' does not match apiContractVersion '$expectedVersion'."
}
if ($Check) {
    if (-not (Test-Path -LiteralPath $contractPath)) { throw "Committed contract is missing: $contractPath" }
    $expected = [IO.File]::ReadAllBytes($contractPath)
    $actual = [IO.File]::ReadAllBytes($tempContract)
    if (-not [Linq.Enumerable]::SequenceEqual[byte]($expected, $actual)) {
        throw "OpenAPI contract changed. Run ops/contract.ps1, review contracts/openapi.json, and increment apiContractVersion."
    }
    Write-Host "OpenAPI contract matches contracts/openapi.json."
}
else {
    New-Item -ItemType Directory -Force -Path $contractDir | Out-Null
    Copy-Item -LiteralPath $tempContract -Destination $contractPath -Force
    Write-Host "Updated $contractPath"
}
