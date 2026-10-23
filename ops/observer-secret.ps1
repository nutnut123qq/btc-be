# Paper-observer database secret: only the PostgreSQL password, protected with DPAPI LocalMachine scope so a
# non-interactive (S4U) task can read it without the deploy user's DPAPI master key being loaded. Any process on
# the machine can call LocalMachine Unprotect, so the file ACL (SYSTEM, Administrators, deploy user read) is the
# real boundary. Functions only; dot-source it. Never prints or returns the password except to the caller.
Add-Type -AssemblyName System.Security
$script:ObserverSecretEntropy = [Text.Encoding]::UTF8.GetBytes("btc-paper-observer-db/v1")

function New-ObserverSecretFile([string]$Path, [Security.SecureString]$Password, [string]$ReaderSid) {
    if (-not $Password -or $Password.Length -eq 0) { throw "Observer secret: password is empty." }
    $pointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($Password)
    try {
        $json = @{ schema = "btc-paper-observer-db/v1"; DB_PASS = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($pointer) } | ConvertTo-Json -Compress
    }
    finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($pointer) }
    $protected = [Security.Cryptography.ProtectedData]::Protect([Text.Encoding]::UTF8.GetBytes($json), $script:ObserverSecretEntropy,
        [Security.Cryptography.DataProtectionScope]::LocalMachine)
    $temp = "$Path.tmp-$([Guid]::NewGuid().ToString('N'))"
    try {
        [IO.File]::WriteAllBytes($temp, $protected)
        & icacls.exe $temp /inheritance:r /grant:r "*S-1-5-18:(F)" "*S-1-5-32-544:(F)" "*$ReaderSid`:(R)" | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "Observer secret: could not restrict the file ACL." }
        Move-Item -LiteralPath $temp -Destination $Path -Force
    }
    finally { Remove-Item -LiteralPath $temp -Force -ErrorAction SilentlyContinue }
}

function Read-ObserverDbPassword([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { throw "Observer secret file not found: $Path" }
    try {
        $bytes = [Security.Cryptography.ProtectedData]::Unprotect([IO.File]::ReadAllBytes($Path), $script:ObserverSecretEntropy,
            [Security.Cryptography.DataProtectionScope]::LocalMachine)
        $value = ([Text.Encoding]::UTF8.GetString($bytes) | ConvertFrom-Json)
    }
    catch { throw "Observer secret could not be decrypted or parsed ($($_.Exception.GetType().Name))." }
    if ($value.schema -ne "btc-paper-observer-db/v1" -or [string]::IsNullOrEmpty($value.DB_PASS)) {
        throw "Observer secret has an unexpected format."
    }
    return [string]$value.DB_PASS
}
