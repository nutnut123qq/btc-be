. "$PSScriptRoot/common.ps1"

if ([string]::IsNullOrWhiteSpace($env:PGPASSWORD) -or [string]::IsNullOrWhiteSpace($env:AdminApiKey)) {
    throw "Protected ops secrets were not imported."
}
if ((ConvertTo-OpsHexString ([byte[]]@(0, 255))) -ne "00FF") { throw "Hex conversion failed." }

$python = Join-Path $script:WorkspaceDir "ai/venv/Scripts/python.exe"
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

Write-Host "Ops common self-test passed under PowerShell $($PSVersionTable.PSVersion)."
