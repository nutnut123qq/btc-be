Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$script:BackendDir = Split-Path $PSScriptRoot -Parent
$script:WorkspaceDir = Split-Path $script:BackendDir -Parent
$script:RuntimeDir = Join-Path $script:BackendDir ".ops"
$script:LogsDir = Join-Path $script:RuntimeDir "logs"
$script:PublishDir = Join-Path $script:RuntimeDir "publish"
$script:StatePath = Join-Path $script:RuntimeDir "processes.json"
$script:SecretsPath = Join-Path $script:RuntimeDir "secrets.clixml"

function Initialize-OpsDirectories {
    New-Item -ItemType Directory -Force -Path $script:RuntimeDir, $script:LogsDir, $script:PublishDir | Out-Null
}

function Import-OpsSecrets {
    if (-not (Test-Path -LiteralPath $script:SecretsPath)) { return }
    $secrets = Import-Clixml -LiteralPath $script:SecretsPath
    foreach ($name in @("PGPASSWORD", "DB_PASS", "AdminApiKey")) {
        if (-not [Environment]::GetEnvironmentVariable($name, "Process")) {
            $secureValue = $secrets.$name
            if ($secureValue -isnot [Security.SecureString]) { throw "Invalid protected value: $name" }
            [Environment]::SetEnvironmentVariable(
                $name,
                (ConvertFrom-SecureString $secureValue -AsPlainText),
                "Process"
            )
        }
    }
    foreach ($name in @("PGHOST", "PGPORT", "PGUSER", "PGDATABASE", "LLM_PROVIDER")) {
        if (-not [Environment]::GetEnvironmentVariable($name, "Process") -and $secrets.$name) {
            [Environment]::SetEnvironmentVariable($name, [string]$secrets.$name, "Process")
        }
    }
}

function Rotate-OpsLog([string]$Path, [long]$MaximumBytes = 25MB) {
    if (-not (Test-Path -LiteralPath $Path) -or (Get-Item -LiteralPath $Path).Length -le $MaximumBytes) { return }
    $previous = "$Path.previous"
    if (Test-Path -LiteralPath $previous) { [IO.File]::Delete($previous) }
    [IO.File]::Move($Path, $previous)
}

function Get-ProcessState {
    if (-not (Test-Path -LiteralPath $script:StatePath)) { return @() }
    $state = Get-Content -LiteralPath $script:StatePath -Raw | ConvertFrom-Json
    return @($state)
}

function Save-ProcessState([array]$Processes) {
    Initialize-OpsDirectories
    ConvertTo-Json -InputObject @($Processes) -Depth 4 | Set-Content -LiteralPath $script:StatePath -Encoding utf8
}

function Test-ManagedProcess($Entry) {
    $process = Get-Process -Id $Entry.pid -ErrorAction SilentlyContinue
    if ($null -eq $process) { return $false }
    try {
        return [Math]::Abs($process.StartTime.ToUniversalTime().Ticks - [long]$Entry.startedAtUtcTicks) -lt [TimeSpan]::FromSeconds(5).Ticks
    }
    catch { return $false }
}

function Start-ManagedProcess {
    param(
        [string]$Name,
        [string]$FilePath,
        [string[]]$ArgumentList,
        [string]$WorkingDirectory,
        [hashtable]$Environment = @{}
    )

    Initialize-OpsDirectories
    $saved = @{}
    try {
        foreach ($key in $Environment.Keys) {
            $saved[$key] = [Environment]::GetEnvironmentVariable($key, "Process")
            [Environment]::SetEnvironmentVariable($key, [string]$Environment[$key], "Process")
        }
        $process = Start-Process -FilePath $FilePath -ArgumentList $ArgumentList `
            -WorkingDirectory $WorkingDirectory -WindowStyle Hidden -PassThru `
            -RedirectStandardOutput (Join-Path $script:LogsDir "$Name.out.log") `
            -RedirectStandardError (Join-Path $script:LogsDir "$Name.err.log")
        $process.Refresh()
        return [pscustomobject]@{
            name = $Name
            pid = $process.Id
            startedAtUtcTicks = $process.StartTime.ToUniversalTime().Ticks
        }
    }
    finally {
        foreach ($key in $Environment.Keys) {
            [Environment]::SetEnvironmentVariable($key, $saved[$key], "Process")
        }
    }
}

function Stop-ManagedProcessTree($Entry) {
    $process = Get-Process -Id $Entry.pid -ErrorAction SilentlyContinue
    if ($null -eq $process) { return }
    if (-not (Test-ManagedProcess $Entry)) { throw "Refusing to stop reused PID $($Entry.pid) for $($Entry.name)." }
    & taskkill.exe /PID $Entry.pid /T /F | Out-Null
    $taskkillExit = $LASTEXITCODE
    if ($taskkillExit -ne 0) { throw "taskkill failed for $($Entry.name) process tree (exit $taskkillExit)." }
    for ($attempt = 0; $attempt -lt 30 -and (Get-Process -Id $Entry.pid -ErrorAction SilentlyContinue); $attempt++) {
        Start-Sleep -Milliseconds 100
    }
    if (Get-Process -Id $Entry.pid -ErrorAction SilentlyContinue) {
        throw "Could not stop $($Entry.name) process tree (taskkill exit $taskkillExit)."
    }
}

function Invoke-BoundedProcess {
    param(
        [Parameter(Mandatory)][string]$FilePath,
        [string[]]$ArgumentList = @(),
        [int]$TimeoutSeconds = 60,
        [string]$WorkingDirectory = ""
    )
    if ($TimeoutSeconds -le 0) { throw "TimeoutSeconds must be positive." }
    $info = [Diagnostics.ProcessStartInfo]::new()
    $info.FileName = $FilePath
    $info.UseShellExecute = $false
    $info.RedirectStandardOutput = $true
    $info.RedirectStandardError = $true
    if ($WorkingDirectory) { $info.WorkingDirectory = $WorkingDirectory }
    foreach ($argument in $ArgumentList) { $info.ArgumentList.Add($argument) }
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $info
    try {
        if (-not $process.Start()) { throw "Could not start $FilePath." }
        $stdoutTask = $process.StandardOutput.ReadToEndAsync()
        $stderrTask = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
            $process.Kill($true)
            $process.WaitForExit(5000) | Out-Null
            throw "$([IO.Path]::GetFileName($FilePath)) timed out after $TimeoutSeconds seconds and was terminated."
        }
        return [pscustomobject]@{
            ExitCode = $process.ExitCode
            Output = $stdoutTask.GetAwaiter().GetResult()
            Error = $stderrTask.GetAwaiter().GetResult()
        }
    }
    finally { $process.Dispose() }
}

function Resolve-PgTool([string]$Name) {
    $command = Get-Command $Name -ErrorAction SilentlyContinue
    if ($command) { $path = $command.Source }
    else { $path = Join-Path $env:ProgramFiles "PostgreSQL\17\bin\$Name.exe" }
    if (-not (Test-Path -LiteralPath $path)) {
        throw "$Name not found. Install PostgreSQL 17 client tools or add them to PATH."
    }
    $versionResult = Invoke-BoundedProcess $path @("--version") 10
    if ($versionResult.ExitCode -ne 0 -or $versionResult.Output -notmatch "PostgreSQL\) 17\.") {
        throw "$Name must be from PostgreSQL 17. Found: $($versionResult.Output) $($versionResult.Error)"
    }
    return $path
}

function Get-PgConnectionArgs([switch]$MaintenanceDatabase) {
    $database = if ($MaintenanceDatabase) { "postgres" } else { $env:PGDATABASE }
    if ([string]::IsNullOrWhiteSpace($database)) {
        throw "PGDATABASE is required. Set PGHOST/PGPORT/PGUSER/PGPASSWORD as needed."
    }
    $hostName = if ($env:PGHOST) { $env:PGHOST } else { "localhost" }
    $port = if ($env:PGPORT) { $env:PGPORT } else { "5432" }
    $user = if ($env:PGUSER) { $env:PGUSER } else { "postgres" }
    return @("--host", $hostName, "--port", $port, "--username", $user, "--no-password", "--dbname", $database)
}

function Get-PgServerArgs {
    $hostName = if ($env:PGHOST) { $env:PGHOST } else { "localhost" }
    $port = if ($env:PGPORT) { $env:PGPORT } else { "5432" }
    $user = if ($env:PGUSER) { $env:PGUSER } else { "postgres" }
    return @("--host", $hostName, "--port", $port, "--username", $user, "--no-password")
}

function Get-PgTableCounts([string]$Database, [string]$Snapshot = "", [int]$TimeoutSeconds = 1800) {
    $psql = Resolve-PgTool "psql"
    $sqlPath = Join-Path $script:RuntimeDir "row-counts-$([Guid]::NewGuid().ToString('N')).sql"
    if ($Snapshot -and $Snapshot -notmatch '^[0-9A-Fa-f]+-[0-9A-Fa-f]+-\d+$') { throw "Invalid PostgreSQL snapshot id." }
    $snapshotPrefix = if ($Snapshot) { "BEGIN ISOLATION LEVEL REPEATABLE READ;`nSET TRANSACTION SNAPSHOT '$Snapshot';`n" } else { "" }
    $sql = $snapshotPrefix + @'
\pset tuples_only on
\pset format unaligned
SELECT format(
  'SELECT %L || ''|'' || count(*)::text FROM %I.%I',
  schemaname || '.' || tablename,
  schemaname,
  tablename)
FROM pg_tables
WHERE schemaname = 'public'
ORDER BY tablename
\gexec
'@
    if ($Snapshot) { $sql += "`nCOMMIT;`n" }
    try {
        [IO.File]::WriteAllText($sqlPath, $sql, [Text.UTF8Encoding]::new($false))
        $args = @(Get-PgServerArgs) + @("--dbname", $Database, "--no-psqlrc", "--quiet", "--file", $sqlPath)
        $result = Invoke-BoundedProcess $psql $args $TimeoutSeconds
        if ($result.ExitCode -ne 0) { throw "Could not read exact table counts from $Database. $($result.Error)" }
        $lines = $result.Output -split "`r?`n"
        $counts = [ordered]@{}
        foreach ($line in $lines) {
            if ($line -match "^([^|]+)\|(\d+)$") { $counts[$Matches[1]] = [long]$Matches[2] }
        }
        return $counts
    }
    finally { Remove-Item -LiteralPath $sqlPath -Force -ErrorAction SilentlyContinue }
}

function Get-NativePg17Status {
    $psql = Resolve-PgTool "psql"
    $pgArgs = @(Get-PgConnectionArgs) + @("--no-psqlrc", "--tuples-only", "--no-align", "--command", "SHOW server_version; SHOW data_directory;")
    $result = Invoke-BoundedProcess $psql $pgArgs 15
    $lines = @($result.Output -split "`r?`n" | Where-Object { $_ })
    if ($result.ExitCode -ne 0 -or $lines.Count -lt 2) {
        return [pscustomobject]@{ ready = $false; detail = "$($result.Output) $($result.Error)".Trim() }
    }
    $version = $lines[0].Trim()
    $dataDirectory = $lines[1].Trim()
    $nativeWindowsPath = $dataDirectory -match '^[A-Za-z]:[\\/]'
    return [pscustomobject]@{
        ready = $version.StartsWith("17.") -and $nativeWindowsPath
        detail = "server=$version data_directory=$dataDirectory nativeWindows=$nativeWindowsPath"
        serverVersion = $version
        dataDirectory = $dataDirectory
        host = $(if ($env:PGHOST) { $env:PGHOST } else { "localhost" })
        port = $(if ($env:PGPORT) { $env:PGPORT } else { "5432" })
        database = $env:PGDATABASE
    }
}

function Assert-NativePg17 {
    $status = Get-NativePg17Status
    if (-not $status.ready) { throw "Expected native Windows PostgreSQL 17. $($status.detail)" }
    return $status
}

function ConvertTo-NpgsqlValue([string]$Value) {
    if ($Value.Contains("`r") -or $Value.Contains("`n")) { throw "Invalid newline in PostgreSQL connection value." }
    return '"' + $Value.Replace('"', '""') + '"'
}

function Get-BackendConnectionString {
    if ([string]::IsNullOrEmpty($env:PGPASSWORD)) { throw "PGPASSWORD is required for production-like backend startup/migration." }
    $hostName = if ($env:PGHOST) { $env:PGHOST } else { "localhost" }
    $port = if ($env:PGPORT) { $env:PGPORT } else { "5432" }
    $user = if ($env:PGUSER) { $env:PGUSER } else { "postgres" }
    if ($port -notmatch '^\d{1,5}$') { throw "Invalid PGPORT." }
    return "Host=$(ConvertTo-NpgsqlValue $hostName);Port=$port;Database=$(ConvertTo-NpgsqlValue $env:PGDATABASE);Username=$(ConvertTo-NpgsqlValue $user);Password=$(ConvertTo-NpgsqlValue $env:PGPASSWORD);Pooling=true;Maximum Pool Size=100;Minimum Pool Size=10;Connection Lifetime=300;Timeout=15;Command Timeout=30;"
}

function Assert-SafeDataDirectory([string]$Path) {
    $resolved = [IO.Path]::GetFullPath($Path)
    if ($resolved -eq [IO.Path]::GetPathRoot($resolved) `
        -or $resolved -eq [IO.Path]::GetFullPath($script:WorkspaceDir) `
        -or $resolved -eq [IO.Path]::GetFullPath($script:BackendDir)) {
        throw "Refusing broad data directory: $resolved"
    }
    return $resolved
}

Import-OpsSecrets
