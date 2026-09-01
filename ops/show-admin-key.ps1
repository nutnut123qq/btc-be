. "$PSScriptRoot/common.ps1"
Import-OpsSecrets
if ([string]::IsNullOrWhiteSpace($env:AdminApiKey)) { throw "AdminApiKey is not configured." }
Write-Output $env:AdminApiKey
