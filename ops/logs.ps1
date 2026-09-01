param(
    [ValidateSet("backend", "ai", "frontend")][string]$Component = "backend",
    [ValidateSet("out", "err")][string]$Stream = "out",
    [int]$Tail = 100,
    [switch]$Follow
)
. "$PSScriptRoot/common.ps1"
$path = Join-Path $script:LogsDir "$Component.$Stream.log"
if (-not (Test-Path -LiteralPath $path)) { throw "Log not found: $path" }
Get-Content -LiteralPath $path -Tail $Tail -Wait:$Follow
