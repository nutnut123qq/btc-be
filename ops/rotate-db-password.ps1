. "$PSScriptRoot/common.ps1"
Initialize-OpsDirectories

$running = @(Get-ProcessState | Where-Object { Test-ManagedProcess $_ })
if ($running.Count -gt 0) {
    throw "Stop the managed stack before rotating the database password. Running: $($running.name -join ', ')"
}
$taskNames = @(
    "BTC_AI_Server_Watchdog",
    "BTC_Futures_Metrics_Collector",
    "BTC_Paper_Trading_Runner",
    "BTC_Ensemble_Paper_Runner",
    "BTC_Liquidation_Collector",
    "BTC_Sentiment_Collector",
    "BTC_Confluence_Collector",
    "Bitcoin Analyst DB Backup"
)
if (Get-Command Get-ScheduledTask -ErrorAction SilentlyContinue) {
    $enabledTasks = @($taskNames | ForEach-Object {
        Get-ScheduledTask -TaskName $_ -ErrorAction SilentlyContinue
    } | Where-Object { $_.State -ne "Disabled" })
    if ($enabledTasks.Count -gt 0) {
        throw "Disable application and backup Scheduled Tasks before rotating the database password. Enabled: $($enabledTasks.TaskName -join ', ')"
    }
}
if (-not (Test-Path -LiteralPath $script:SecretsPath -PathType Leaf)) {
    throw "Protected ops secrets do not exist. Run configure-secrets.ps1 first."
}
if ([string]::IsNullOrWhiteSpace($env:PGPASSWORD)) { throw "The current protected database password is unavailable." }

$role = if ($env:PGUSER) { $env:PGUSER } else { "postgres" }
if ($role -notmatch '^[A-Za-z_][A-Za-z0-9_]*$') { throw "PGUSER is not a supported PostgreSQL role identifier." }

$psql = Resolve-PgTool "psql"
$connectionArgs = @(Get-PgConnectionArgs) + @("--no-psqlrc", "--quiet", "--tuples-only", "--no-align", "--set", "ON_ERROR_STOP=1")
$identity = Invoke-BoundedProcess $psql ($connectionArgs + @("--command", "SELECT current_user;")) 30
if ($identity.ExitCode -ne 0 -or $identity.Output.Trim() -ne $role) {
    throw "Could not authenticate as the configured PostgreSQL role before rotation. $($identity.Error)"
}

$randomBytes = New-Object byte[] 32
$generator = [Security.Cryptography.RandomNumberGenerator]::Create()
try { $generator.GetBytes($randomBytes) }
finally { $generator.Dispose() }
$newPassword = [Convert]::ToBase64String($randomBytes).TrimEnd('=').Replace('+', '-').Replace('/', '_')

$existing = Import-Clixml -LiteralPath $script:SecretsPath
foreach ($required in @("AdminApiKey")) {
    $property = $existing.PSObject.Properties[$required]
    if (-not $property -or $property.Value -isnot [Security.SecureString]) {
        throw "Invalid protected value: $required"
    }
}

$protectedSecrets = [ordered]@{
    PGHOST = $(if ($existing.PGHOST) { [string]$existing.PGHOST } else { "127.0.0.1" })
    PGPORT = $(if ($existing.PGPORT) { [string]$existing.PGPORT } else { "5432" })
    PGUSER = $role
    PGDATABASE = $(if ($existing.PGDATABASE) { [string]$existing.PGDATABASE } else { $env:PGDATABASE })
    PGPASSWORD = ConvertTo-SecureString $newPassword -AsPlainText -Force
    DB_PASS = ConvertTo-SecureString $newPassword -AsPlainText -Force
    AdminApiKey = $existing.AdminApiKey
    LLM_PROVIDER = $(if ($existing.LLM_PROVIDER) { [string]$existing.LLM_PROVIDER } else { "none" })
}
$geminiProperty = $existing.PSObject.Properties["GEMINI_API_KEY"]
if ($geminiProperty) {
    if ($geminiProperty.Value -isnot [Security.SecureString]) { throw "Invalid protected value: GEMINI_API_KEY" }
    $protectedSecrets.GEMINI_API_KEY = $geminiProperty.Value
}

$stagedPath = Join-Path $script:RuntimeDir "secrets.rotation-$([Guid]::NewGuid().ToString('N')).clixml"
$recoveryPath = Join-Path $script:RuntimeDir "secrets.pre-rotation-$([Guid]::NewGuid().ToString('N')).clixml"
$failedPath = Join-Path $script:RuntimeDir "secrets.failed-rotation-$([Guid]::NewGuid().ToString('N')).clixml"
$secretReplaced = $false
$databaseChanged = $false
try {
    [pscustomobject]$protectedSecrets | Export-Clixml -LiteralPath $stagedPath
    Set-OpsSecretsFileAcl $stagedPath
    [IO.File]::Replace($stagedPath, $script:SecretsPath, $recoveryPath)
    $secretReplaced = $true
    Set-OpsSecretsFileAcl $script:SecretsPath
    Set-OpsSecretsFileAcl $recoveryPath

    $quotedRole = '"' + $role.Replace('"', '""') + '"'
    $sql = "ALTER ROLE $quotedRole WITH PASSWORD '$newPassword';`n"
    $alter = Invoke-BoundedProcess $psql $connectionArgs 30 -StandardInput $sql
    if ($alter.ExitCode -ne 0) { throw "PostgreSQL rejected the password rotation; diagnostic output is suppressed because it may contain the submitted SQL." }
    $databaseChanged = $true

    [Environment]::SetEnvironmentVariable("PGPASSWORD", $newPassword, "Process")
    [Environment]::SetEnvironmentVariable("DB_PASS", $newPassword, "Process")
    $verify = Invoke-BoundedProcess $psql ($connectionArgs + @("--command", "SELECT 1;")) 30
    if ($verify.ExitCode -ne 0 -or $verify.Output.Trim() -ne "1") {
        throw "The password changed, but a fresh PostgreSQL login could not be verified. $($verify.Error)"
    }

    [IO.File]::Delete($recoveryPath)
    Write-Host "PostgreSQL password rotated and fresh authentication verified. Protected secrets remain at $script:SecretsPath"
}
catch {
    if ($secretReplaced -and -not $databaseChanged -and (Test-Path -LiteralPath $recoveryPath)) {
        [IO.File]::Replace($recoveryPath, $script:SecretsPath, $failedPath)
        Set-OpsSecretsFileAcl $script:SecretsPath
        [Environment]::SetEnvironmentVariable("PGPASSWORD", $null, "Process")
        [Environment]::SetEnvironmentVariable("DB_PASS", $null, "Process")
        Import-OpsSecrets
    }
    throw
}
finally {
    foreach ($path in @($stagedPath, $failedPath)) {
        if (Test-Path -LiteralPath $path) { [IO.File]::Delete($path) }
    }
}
