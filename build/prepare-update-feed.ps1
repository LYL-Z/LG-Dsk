# Build a signed, static-only update feed. Private key is DPAPI-protected outside the repo.
[CmdletBinding()]
param(
    [string]$InstallerPath,
    [string]$ReleaseNotesPath,
    [string]$Version,
    [string]$OutputDirectory,
    [ValidateSet('stable','candidate')][string]$Channel = 'stable',
    [switch]$InitializeKey
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$keyDirectory = Join-Path $env:LOCALAPPDATA 'LG Dsk/ReleaseSigning'
$keyPath = Join-Path $keyDirectory 'update-feed.dpapi'
$publicPath = Join-Path $repo 'src/GlassDesk.AppServices/Resources/update-publisher-public.pem'
Add-Type -AssemblyName System.Security
$rsa = [System.Security.Cryptography.RSA]::Create(3072)
try {
    if (-not (Test-Path -LiteralPath $keyPath)) {
        if (-not $InitializeKey) { throw 'Publisher key missing. Run with -InitializeKey once, then back it up securely.' }
        [System.IO.Directory]::CreateDirectory($keyDirectory) | Out-Null
        $private = $rsa.ExportPkcs8PrivateKey()
        try {
            $protected = [System.Security.Cryptography.ProtectedData]::Protect($private, $null, 'CurrentUser')
            [System.IO.File]::WriteAllBytes($keyPath, $protected)
        } finally { [System.Array]::Clear($private, 0, $private.Length) }
    } else {
        $private = [System.Security.Cryptography.ProtectedData]::Unprotect([System.IO.File]::ReadAllBytes($keyPath), $null, 'CurrentUser')
        try { $rsa.ImportPkcs8PrivateKey($private, [ref]0) }
        finally { [System.Array]::Clear($private, 0, $private.Length) }
    }
    $public = $rsa.ExportSubjectPublicKeyInfoPem()
    if (Test-Path -LiteralPath $publicPath) {
        if ([System.IO.File]::ReadAllText($publicPath).Trim() -cne $public.Trim()) {
            throw 'Signing key differs from the compiled publisher key; refusing to rotate silently.'
        }
    } else { [System.IO.File]::WriteAllText($publicPath, $public) }
    if (-not $InstallerPath) { Write-Output 'Publisher public key ready; private key remains DPAPI-protected.'; return }
    if ($Version -notmatch '^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$') { throw 'Version must be x.y.z' }
    $installer = Get-Item -LiteralPath $InstallerPath
    if ($installer.Name -cne "LG-Dsk-$Version-win-x64-Setup.exe") { throw 'Unexpected installer filename' }
    $out = [System.IO.Path]::GetFullPath($OutputDirectory)
    $safeRoot = [System.IO.Path]::GetFullPath((Join-Path $repo 'artifacts')).TrimEnd('\') + '\'
    if (-not $out.StartsWith($safeRoot, [System.StringComparison]::OrdinalIgnoreCase)) { throw 'Feed output must be under artifacts' }
    [System.IO.Directory]::CreateDirectory((Join-Path $out 'chunks')) | Out-Null
    $parts = [System.Collections.Generic.List[object]]::new()
    $inputFile = $installer.OpenRead()
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        $buffer = [byte[]]::new(8MB)
        while (($count = $inputFile.Read($buffer, 0, $buffer.Length)) -gt 0) {
            $hash = [Convert]::ToHexString($sha.ComputeHash($buffer,0,$count)).ToLowerInvariant()
            $path = Join-Path $out "chunks/$hash.bin"
            $outputFile = [System.IO.File]::Create($path)
            try { $outputFile.Write($buffer,0,$count) } finally { $outputFile.Dispose() }
            $parts.Add(@{sha256=$hash;size=$count})
        }
    } finally { $inputFile.Dispose(); $sha.Dispose() }
    $now = [DateTimeOffset]::UtcNow
    $payload = [ordered]@{
        schemaVersion=1; channel=$Channel;version=$Version;fileName=$installer.Name;size=$installer.Length
        sha256=(Get-FileHash -LiteralPath $installer.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        publishedUtc=$now.ToString('O');expiresUtc=$now.AddDays(90).ToString('O')
        notes=[System.IO.File]::ReadAllText((Resolve-Path -LiteralPath $ReleaseNotesPath));chunks=$parts.ToArray()
    } | ConvertTo-Json -Depth 8 -Compress
    $bytes = [System.Text.Encoding]::UTF8.GetBytes($payload)
    $signature = $rsa.SignData($bytes,[System.Security.Cryptography.HashAlgorithmName]::SHA256,[System.Security.Cryptography.RSASignaturePadding]::Pss)
    $signed = @{payload=[Convert]::ToBase64String($bytes);signature=[Convert]::ToBase64String($signature)} | ConvertTo-Json -Compress
    [System.IO.File]::WriteAllText((Join-Path $out "$Channel.json"), $signed)
    [System.IO.File]::WriteAllText((Join-Path $out 'publisher-key.json'), (ConvertTo-Json -InputObject $public -Compress))
    Copy-Item -LiteralPath (Join-Path $repo 'tools/update-distribution/index.html') -Destination (Join-Path $out 'index.html') -Force
    [System.IO.File]::WriteAllText((Join-Path $out '_headers'), "/chunks/*`n  Cache-Control: public, max-age=31536000, immutable`n/*.json`n  Cache-Control: public, max-age=60, must-revalidate`n/*`n  X-Content-Type-Options: nosniff`n")
    Write-Output "Signed $Channel feed and $($parts.Count) chunks prepared."
} finally { $rsa.Dispose() }
