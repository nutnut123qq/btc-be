# Self-test for the paper observer secret loader and task entrypoint. Uses a temporary directory and a random
# test password only; touches no real secret, task or database.
$ErrorActionPreference = "Stop"
. "$PSScriptRoot/observer-secret.ps1"
$testRoot = Join-Path ([IO.Path]::GetTempPath()) "btc-observer-selftest-$([Guid]::NewGuid().ToString('N'))"
$secret = Join-Path $testRoot "observer-db.secret"
$testPassword = "selftest-" + [Guid]::NewGuid().ToString("N")
$me = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
$wrapper = Join-Path $PSScriptRoot "run-paper-observer.ps1"
$powerShell = Join-Path $env:SystemRoot "System32\WindowsPowerShell\v1.0\powershell.exe"
function Assert($condition, $message) { if (-not $condition) { throw "SELF-TEST FAILED: $message" } }
try {
    New-Item -ItemType Directory -Force $testRoot | Out-Null

    # 1. Round trip, ciphertext only, tight ACL.
    New-ObserverSecretFile -Path $secret -Password (ConvertTo-SecureString $testPassword -AsPlainText -Force) -ReaderSid $me
    Assert ((Read-ObserverDbPassword $secret) -eq $testPassword) "round trip"
    Assert (-not ([Text.Encoding]::UTF8.GetString([IO.File]::ReadAllBytes($secret)).Contains($testPassword))) "plaintext on disk"
    $acl = Get-Acl -LiteralPath $secret
    Assert ($acl.AreAccessRulesProtected) "ACL inheritance not removed"
    $sids = @($acl.Access | ForEach-Object { $_.IdentityReference.Translate([Security.Principal.SecurityIdentifier]).Value })
    Assert (@($sids | Where-Object { $_ -notin @("S-1-5-18", "S-1-5-32-544", $me) }).Count -eq 0) "unexpected ACL principal: $($sids -join ',')"

    # 2. Machine scope, not the user's DPAPI master key. Unprotect ignores the scope argument (the blob names its own
    # master key), so check the blob header instead: dwFlags at offset 40 must carry CRYPTPROTECT_LOCAL_MACHINE (0x4).
    $blob = [IO.File]::ReadAllBytes($secret)
    Assert (([BitConverter]::ToUInt32($blob, 40) -band 0x4) -ne 0) "DPAPI blob is not LocalMachine-protected"
    $userBlob = [Security.Cryptography.ProtectedData]::Protect([byte[]](1, 2, 3), $null, "CurrentUser")
    Assert (([BitConverter]::ToUInt32($userBlob, 40) -band 0x4) -eq 0) "flag check cannot tell user scope from machine scope"

    # 3. Missing and corrupt files fail closed without echoing the password.
    foreach ($case in @("missing", "corrupt")) {
        $path = Join-Path $testRoot "$case.secret"
        if ($case -eq "corrupt") { [IO.File]::WriteAllBytes($path, [byte[]](1..64)) }
        $message = ""
        try { [void](Read-ObserverDbPassword $path) } catch { $message = $_.Exception.Message }
        Assert ($message -and -not $message.Contains($testPassword)) "$case secret did not fail closed"
    }

    # 4. Entrypoint: no user-scope secret loading, and a missing secret exits non-zero with a log line.
    $source = Get-Content -LiteralPath $wrapper -Raw
    $code = ($source -split "`n" | Where-Object { $_ -notmatch '^\s*#' }) -join "`n"
    Assert ($code -notmatch 'common\.ps1|Import-OpsSecrets|run-ai-job|Import-Clixml') "entrypoint loads user-scope secrets"
    $logs = Join-Path $testRoot "logs"
    & $powerShell -NoProfile -NonInteractive -ExecutionPolicy Bypass -File $wrapper -SecretPath (Join-Path $testRoot "absent.secret") -AiDir $testRoot -LogDirectory $logs | Out-Null
    Assert ($LASTEXITCODE -ne 0) "missing secret returned exit 0"
    $line = Get-Content -LiteralPath (Join-Path $logs "paperobserver-task.log") -Tail 1
    Assert ($line -match 'exit=1' -and $line -match 'Observer secret file not found') "missing-secret log line: $line"

    # 5. A valid secret but no virtualenv also fails closed, and the password never reaches the log.
    & $powerShell -NoProfile -NonInteractive -ExecutionPolicy Bypass -File $wrapper -SecretPath $secret -AiDir $testRoot -LogDirectory $logs | Out-Null
    Assert ($LASTEXITCODE -ne 0) "missing virtualenv returned exit 0"
    $log = Get-Content -LiteralPath (Join-Path $logs "paperobserver-task.log") -Raw
    Assert ($log -match 'AI virtualenv is missing' -and -not $log.Contains($testPassword)) "virtualenv/secret leak check"
    Write-Host "paper-observer-self-test: PASS (5 checks)"
}
finally { Remove-Item -LiteralPath $testRoot -Recurse -Force -ErrorAction SilentlyContinue }
