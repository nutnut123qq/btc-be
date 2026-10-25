Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$script:BackendDir = Split-Path $PSScriptRoot -Parent
$script:WorkspaceDir = Split-Path $script:BackendDir -Parent

function Resolve-OpsComponentDirectory([string]$WorkspacePath, [string]$MonorepoName, [string]$SiblingName) {
    $monorepoPath = Join-Path $WorkspacePath $MonorepoName
    if (Test-Path -LiteralPath $monorepoPath -PathType Container) { return $monorepoPath }
    return Join-Path $WorkspacePath $SiblingName
}

$script:FrontendDir = Resolve-OpsComponentDirectory $script:WorkspaceDir "frontend" "btc-fe"
$script:AiDir = Resolve-OpsComponentDirectory $script:WorkspaceDir "ai" "btc-ai"
$script:RuntimeDir = Join-Path $script:BackendDir ".ops"
$script:LogsDir = Join-Path $script:RuntimeDir "logs"
$script:PublishDir = Join-Path $script:RuntimeDir "publish"
$script:StatePath = Join-Path $script:RuntimeDir "processes.json"
$script:SecretsPath = Join-Path $script:RuntimeDir "secrets.clixml"
# Machine-scope subset for unattended (S4U) tasks, which cannot unlock the deploy user's DPAPI key. See
# configure-machine-secrets.ps1. The file ACL (SYSTEM, Administrators, deploy user read) is the boundary.
$script:MachineSecretsPath = Join-Path $script:RuntimeDir "machine-secrets.bin"
$script:MachineSecretNames = @("PGPASSWORD", "DB_PASS", "AdminApiKey", "PGHOST", "PGPORT", "PGUSER", "PGDATABASE", "LLM_PROVIDER")

function Initialize-OpsDirectories {
    New-Item -ItemType Directory -Force -Path $script:RuntimeDir, $script:LogsDir, $script:PublishDir | Out-Null
}

function Set-OpsSecretsFileAcl([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { throw "Protected secret file not found: $Path" }
    $currentUserSid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
    & icacls.exe $Path /inheritance:r /grant:r "*$currentUserSid`:(F)" "*S-1-5-18`:(F)" "*S-1-5-32-544`:(F)" | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Could not restrict the protected secret file ACL." }
}

function Get-OpsMachineSecretEntropy { return [Text.Encoding]::UTF8.GetBytes("btc-ops-machine-secrets/v1") }

function Read-OpsMachineSecrets([string]$Path = $script:MachineSecretsPath) {
    Add-Type -AssemblyName System.Security
    try {
        $bytes = [Security.Cryptography.ProtectedData]::Unprotect([IO.File]::ReadAllBytes($Path), (Get-OpsMachineSecretEntropy),
            [Security.Cryptography.DataProtectionScope]::LocalMachine)
        $values = [Text.Encoding]::UTF8.GetString($bytes) | ConvertFrom-Json
    }
    catch { throw "Machine-scope ops secret could not be decrypted or parsed ($($_.Exception.GetType().Name))." }
    if ($values.schema -ne "btc-ops-machine-secrets/v1") { throw "Machine-scope ops secret has an unexpected format." }
    return $values
}

function Import-OpsSecrets {
    if (-not (Test-Path -LiteralPath $script:SecretsPath)) { return }
    try { $secrets = Import-Clixml -LiteralPath $script:SecretsPath }
    catch {
        # User-scope DPAPI is unavailable in a non-interactive S4U logon; fall back to the machine-scope subset only.
        if (-not (Test-Path -LiteralPath $script:MachineSecretsPath -PathType Leaf)) { throw }
        $machine = Read-OpsMachineSecrets
        foreach ($name in $script:MachineSecretNames) {
            $property = $machine.PSObject.Properties[$name]
            if (-not [Environment]::GetEnvironmentVariable($name, "Process") -and $property -and $property.Value) {
                [Environment]::SetEnvironmentVariable($name, [string]$property.Value, "Process")
            }
        }
        return
    }
    foreach ($name in @("PGPASSWORD", "DB_PASS", "AdminApiKey", "GEMINI_API_KEY", "OPENROUTER_API_KEY", "Ingress__SharedSecret")) {
        $property = $secrets.PSObject.Properties[$name]
        if (-not [Environment]::GetEnvironmentVariable($name, "Process") -and $property) {
            $secureValue = $property.Value
            if ($secureValue -isnot [Security.SecureString]) { throw "Invalid protected value: $name" }
            [Environment]::SetEnvironmentVariable(
                $name,
                (ConvertFrom-OpsSecureString $secureValue),
                "Process"
            )
        }
    }
    foreach ($name in @("PGHOST", "PGPORT", "PGUSER", "PGDATABASE", "LLM_PROVIDER", "GEMINI_MODEL", "OPENROUTER_MODEL", "OPENROUTER_BASE_URL")) {
        $property = $secrets.PSObject.Properties[$name]
        if (-not [Environment]::GetEnvironmentVariable($name, "Process") -and $property -and $property.Value) {
            [Environment]::SetEnvironmentVariable($name, [string]$property.Value, "Process")
        }
    }
}

function ConvertFrom-OpsSecureString([Security.SecureString]$Value) {
    $pointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($Value)
    try { return [Runtime.InteropServices.Marshal]::PtrToStringBSTR($pointer) }
    finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($pointer) }
}

function ConvertTo-OpsHexString([byte[]]$Bytes) {
    return ([BitConverter]::ToString($Bytes)).Replace("-", "")
}

function ConvertTo-OpsNativeArgument([AllowEmptyString()][string]$Value) {
    if ($Value.Length -gt 0 -and $Value -notmatch '[\s"]') { return $Value }

    $builder = New-Object Text.StringBuilder
    [void]$builder.Append('"')
    $backslashes = 0
    foreach ($character in $Value.ToCharArray()) {
        if ($character -eq [char]92) {
            $backslashes++
            continue
        }
        if ($character -eq [char]34) {
            [void]$builder.Append(([string][char]92) * ($backslashes * 2 + 1))
            [void]$builder.Append($character)
            $backslashes = 0
            continue
        }
        if ($backslashes -gt 0) {
            [void]$builder.Append(([string][char]92) * $backslashes)
            $backslashes = 0
        }
        [void]$builder.Append($character)
    }
    if ($backslashes -gt 0) { [void]$builder.Append(([string][char]92) * ($backslashes * 2)) }
    [void]$builder.Append('"')
    return $builder.ToString()
}

function Join-OpsNativeArguments([string[]]$ArgumentList) {
    return (@($ArgumentList | ForEach-Object { ConvertTo-OpsNativeArgument $_ }) -join " ")
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

function Get-ProcessStartUtcTicks([int]$Id) {
    $process = Get-Process -Id $Id -ErrorAction SilentlyContinue
    if ($null -eq $process) { return $null }
    try { if ($process.StartTime) { return $process.StartTime.ToUniversalTime().Ticks } } catch { }
    # A process of another logon session (the S4U watchdog's stack seen from an interactive session) cannot be
    # opened for its start time; CIM still reports the creation time, so the stack is not mistaken for stopped.
    $cim = Get-CimInstance Win32_Process -Filter "ProcessId=$Id" -ErrorAction SilentlyContinue
    if ($cim -and $cim.CreationDate) { return $cim.CreationDate.ToUniversalTime().Ticks }
    return $null
}

function Test-ManagedProcess($Entry) {
    $ticks = Get-ProcessStartUtcTicks ([int]$Entry.pid)
    if ($null -eq $ticks) { return $false }
    return [Math]::Abs($ticks - [long]$Entry.startedAtUtcTicks) -lt [TimeSpan]::FromSeconds(5).Ticks
}

# An S4U logon gives new processes a default DACL that only that logon session and SYSTEM can open, so neither the
# interactive user nor the next watchdog run (a different S4U logon) could query or stop the stack. Before starting
# children, replace this process token's default DACL with user + SYSTEM + Administrators; children inherit it.
function Grant-OpsUserProcessAccess {
    if (-not ("OpsTokenDacl" -as [type])) {
        Add-Type -TypeDefinition @"
using System; using System.Runtime.InteropServices;
public static class OpsTokenDacl {
    [DllImport("advapi32.dll", SetLastError = true)] static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
    [DllImport("kernel32.dll")] static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll")] static extern IntPtr LocalFree(IntPtr memory);
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool ConvertStringSecurityDescriptorToSecurityDescriptor(string sddl, uint revision, out IntPtr descriptor, out uint size);
    [DllImport("advapi32.dll", SetLastError = true)] static extern bool GetSecurityDescriptorDacl(IntPtr descriptor, out bool present, out IntPtr dacl, out bool defaulted);
    [DllImport("advapi32.dll", SetLastError = true)] static extern bool SetTokenInformation(IntPtr token, int infoClass, ref IntPtr info, uint length);
    public static void Apply(string sddl) {
        IntPtr token, descriptor, dacl; uint size; bool present, defaulted;
        if (!OpenProcessToken(GetCurrentProcess(), 0x0080 /* TOKEN_ADJUST_DEFAULT */, out token)) throw new System.ComponentModel.Win32Exception();
        try {
            if (!ConvertStringSecurityDescriptorToSecurityDescriptor(sddl, 1, out descriptor, out size)) throw new System.ComponentModel.Win32Exception();
            try {
                if (!GetSecurityDescriptorDacl(descriptor, out present, out dacl, out defaulted) || !present) throw new System.ComponentModel.Win32Exception();
                if (!SetTokenInformation(token, 6 /* TokenDefaultDacl */, ref dacl, (uint)IntPtr.Size)) throw new System.ComponentModel.Win32Exception();
            } finally { LocalFree(descriptor); }
        } finally { CloseHandle(token); }
    }
}
"@
    }
    $userSid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
    [OpsTokenDacl]::Apply("D:(A;;GA;;;$userSid)(A;;GA;;;SY)(A;;GA;;;BA)")
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

# Root plus descendants, following ParentProcessId only to children created after their parent (a recycled parent
# PID can never adopt an older process). Breadth-first, root first.
function Get-ProcessTreeIds([int]$RootId) {
    $all = @(Get-CimInstance Win32_Process | Select-Object ProcessId, ParentProcessId, CreationDate)
    $root = $all | Where-Object { $_.ProcessId -eq $RootId } | Select-Object -First 1
    if ($null -eq $root) { return @() }
    $ids = [Collections.Generic.List[int]]::new()
    $queue = [Collections.Generic.Queue[object]]::new()
    $queue.Enqueue($root)
    while ($queue.Count -gt 0) {
        $node = $queue.Dequeue()
        $ids.Add([int]$node.ProcessId)
        foreach ($child in @($all | Where-Object { $_.ParentProcessId -eq $node.ProcessId -and $_.ProcessId -ne $node.ProcessId -and $_.CreationDate -ge $node.CreationDate })) {
            $queue.Enqueue($child)
        }
    }
    return $ids.ToArray()
}

function Stop-ManagedProcessTree($Entry) {
    $process = Get-Process -Id $Entry.pid -ErrorAction SilentlyContinue
    if ($null -eq $process) { return }
    if (-not (Test-ManagedProcess $Entry)) { throw "Refusing to stop reused PID $($Entry.pid) for $($Entry.name)." }
    $tree = @(Get-ProcessTreeIds ([int]$Entry.pid))
    $taskkill = Invoke-BoundedProcess (Join-Path $env:SystemRoot "System32\taskkill.exe") @("/PID", [string]$Entry.pid, "/T", "/F") 60
    $taskkillExit = $taskkill.ExitCode
    if ($taskkillExit -ne 0) {
        # taskkill also asks for query rights, which the integrity policy withholds on a higher-integrity S4U process
        # even when its DACL grants this user; TERMINATE alone is allowed. Kill the tree leaves-first instead.
        $leavesFirst = @($tree); [Array]::Reverse($leavesFirst)
        foreach ($id in $leavesFirst) {
            try { [Diagnostics.Process]::GetProcessById($id).Kill() } catch { }
        }
    }
    for ($attempt = 0; $attempt -lt 30 -and @($tree | Where-Object { Get-Process -Id $_ -ErrorAction SilentlyContinue }).Count -gt 0; $attempt++) {
        Start-Sleep -Milliseconds 100
    }
    $survivors = @($tree | Where-Object { Get-Process -Id $_ -ErrorAction SilentlyContinue })
    if ($survivors.Count -gt 0) {
        throw "Could not stop $($Entry.name) process tree (taskkill exit $taskkillExit; remaining PIDs: $($survivors -join ', '))."
    }
}

function Invoke-BoundedProcess {
    param(
        [Parameter(Mandatory)][string]$FilePath,
        [string[]]$ArgumentList = @(),
        [int]$TimeoutSeconds = 60,
        [string]$WorkingDirectory = "",
        [AllowEmptyString()][string]$StandardInput
    )
    if ($TimeoutSeconds -le 0) { throw "TimeoutSeconds must be positive." }
    $info = [Diagnostics.ProcessStartInfo]::new()
    $info.FileName = $FilePath
    $info.UseShellExecute = $false
    $info.RedirectStandardOutput = $true
    $info.RedirectStandardError = $true
    $info.RedirectStandardInput = $PSBoundParameters.ContainsKey("StandardInput")
    if ($WorkingDirectory) { $info.WorkingDirectory = $WorkingDirectory }
    $info.Arguments = Join-OpsNativeArguments $ArgumentList
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $info
    try {
        if (-not $process.Start()) { throw "Could not start $FilePath." }
        if ($info.RedirectStandardInput) {
            $process.StandardInput.Write($StandardInput)
            $process.StandardInput.Close()
        }
        $stdoutTask = $process.StandardOutput.ReadToEndAsync()
        $stderrTask = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
            $process.Kill()
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

function Get-Sha256Hex {
    param([Parameter(Mandatory)][string]$LiteralPath)

    $resolved = [IO.Path]::GetFullPath($LiteralPath)
    if (-not (Test-Path -LiteralPath $resolved -PathType Leaf)) {
        throw "Cannot hash missing file: $resolved"
    }

    $stream = $null
    $sha = $null
    try {
        $stream = [IO.File]::OpenRead($resolved)
        $sha = [Security.Cryptography.SHA256]::Create()
        return ([BitConverter]::ToString($sha.ComputeHash($stream))).Replace("-", "").ToLowerInvariant()
    }
    finally {
        if ($sha) { $sha.Dispose() }
        if ($stream) { $stream.Dispose() }
    }
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

# The host profile (.ops/backend-settings.json) must decide every worker that defaults to on (RSS, paid embeddings,
# dataset builders, alerts, testnet stream, document pipeline) explicitly, so an unattended start fails closed
# instead of inheriting the all-on committed defaults.
function Assert-HostProfile([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { throw "Host profile missing: $Path. Refusing unattended start." }
    try { $settings = Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json } catch { throw "Host profile is not valid JSON: $Path" }
    $required = @("BackgroundWorkers__Enabled")
    if ([string]$settings.PSObject.Properties["BackgroundWorkers__Enabled"].Value -eq "true") {
        $required += @("KlinesIngestionWorker", "IndexingBackgroundWorker", "CausalSmartMoneyRebuildWorker", "RssIngestionService",
            "EmbeddingBackfillWorker", "MlDatasetBuilder", "WindowDatasetBuilder") | ForEach-Object { "BackgroundWorkers__$($_)__Enabled" }
        $required += @("Alerts__WorkerEnabled", "BinanceTestnet__StreamEnabled", "EquityDocuments__PipelineEnabled")
    }
    $bad = @($required | Where-Object { $p = $settings.PSObject.Properties[$_]; -not $p -or @("true", "false") -notcontains [string]$p.Value })
    if ($bad.Count -gt 0) { throw "Host profile $Path must set true/false explicitly for: $($bad -join ', '). Refusing unattended start." }
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
