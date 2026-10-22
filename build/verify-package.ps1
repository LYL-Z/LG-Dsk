# Read-only verification for a local LG Dsk package candidate.
# Keep this script ASCII-only for Windows PowerShell 5.1 compatibility.

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$PackageDirectory,
    [string]$ReleaseManifest
)

$ErrorActionPreference = 'Stop'

function Get-JsonObject {
    param([string]$Path)
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { throw "Missing package metadata: '$Path'." }
    return (Get-Content -LiteralPath $Path -Raw -Encoding UTF8 | ConvertFrom-Json)
}

function Get-Sha256HexFromFile {
    param([string]$Path)
    $stream = [System.IO.File]::OpenRead($Path)
    try {
        $sha = [System.Security.Cryptography.SHA256]::Create()
        try { return [System.BitConverter]::ToString($sha.ComputeHash($stream)).Replace('-', '') }
        finally { $sha.Dispose() }
    }
    finally { $stream.Dispose() }
}

function Assert-RelativePayloadPath {
    param([string]$Path)
    if ([string]::IsNullOrWhiteSpace($Path) -or $Path.Contains('\') -or $Path.StartsWith('/') -or $Path.StartsWith('\') -or
        $Path -match '^[A-Za-z]:' -or $Path.Contains(':')) {
        throw "Invalid payload path: '$Path'."
    }
    $parts = $Path.Replace('\', '/').Split('/')
    if ($parts | Where-Object { $_ -eq '' -or $_ -eq '.' -or $_ -eq '..' }) { throw "Invalid payload path traversal: '$Path'." }
}

function Assert-PayloadIsAllowed {
    param([string]$Path)
    Assert-RelativePayloadPath -Path $Path
    $normalized = $Path.Replace('\', '/')
    if ($normalized -match '(^|/)(src|tests|sdk|obj|\.git)(/|$)' -or
        $normalized -match '\.(cs|xaml|csproj|slnx|pdb|props|targets)$' -or
        $normalized -match '(^|/)GlassDesk[^/]*\.Tests?\.dll$' -or
        [System.IO.Path]::GetFileName($normalized) -match '\.Tests\.dll$') {
        throw "Forbidden development or SDK payload: '$Path'."
    }
}

function Assert-ManifestMatchesFiles {
    param([object[]]$Records, [System.Collections.IDictionary]$Actual, [string]$Label)
    $seen = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::OrdinalIgnoreCase)
    foreach ($record in $Records) {
        $path = [string]$record.relativePath
        Assert-RelativePayloadPath -Path $path
        if (-not $seen.Add($path)) { throw "Duplicate $Label record: '$path'." }
        if (-not $Actual.Contains($path)) { throw "Missing $Label file: '$path'." }
        $file = $Actual[$path]
        if ([long]$record.length -ne [long]$file.Length -or
            -not ([string]$record.sha256).Equals([string]$file.Sha256, [System.StringComparison]::OrdinalIgnoreCase)) {
            throw "$Label checksum or length mismatch: '$path'."
        }
    }
    foreach ($path in $Actual.Keys) {
        if (-not $seen.Contains([string]$path)) { throw "Extra $Label file: '$path'." }
    }
}

try {
    $packagePath = [System.IO.Path]::GetFullPath($PackageDirectory)
    if (-not (Test-Path -LiteralPath $packagePath -PathType Container)) { throw "Package directory does not exist: '$packagePath'." }
    if (-not $ReleaseManifest) {
        $scriptRoot = Split-Path -Parent $PSScriptRoot
        $ReleaseManifest = Join-Path $scriptRoot 'packaging/release.json'
    }
    $release = Get-JsonObject -Path $ReleaseManifest
    $payload = Get-JsonObject -Path (Join-Path $packagePath 'payload-manifest.json')
    $report = Get-JsonObject -Path (Join-Path $packagePath 'build-report.json')
    $checksums = Get-JsonObject -Path (Join-Path $packagePath 'SHA256SUMS.json')

    if ($release.status -ne 'local-candidate') { throw "Unsupported release status '$($release.status)'." }
    if ($release.schemaVersion -ne 1 -or $payload.schemaVersion -ne 1 -or $report.schemaVersion -ne 1 -or $checksums.schemaVersion -ne 1) {
        throw 'Unsupported package metadata schema version.'
    }
    if ($release.productName -ne 'LG Dsk' -or $release.rid -ne 'win-x64' -or
        $release.productName -ne $payload.productName -or $release.productName -ne $report.productName -or
        $release.appId -ne $payload.appId -or $release.appId -ne $report.appId) {
        throw 'Package identity metadata does not match the release manifest.'
    }
    if ($release.productVersion -ne $payload.productVersion -or $release.productVersion -ne $report.productVersion -or
        $release.assemblyVersion -ne $payload.assemblyVersion -or $release.assemblyVersion -ne $report.assemblyVersion -or
        $release.fileVersion -ne $payload.fileVersion -or $release.fileVersion -ne $report.fileVersion -or
        $release.packageBaseName -notmatch '^LG-Dsk-[0-9]+\.[0-9]+\.[0-9]+(-[0-9A-Za-z.-]+)?-win-x64$') {
        throw 'Package version metadata does not match the release manifest.'
    }
    if ($report.status -ne 'Built') { throw "Unsupported build report status '$($report.status)'." }
    if ($report.PSObject.Properties.Name -contains 'packageDirectory') {
        throw 'Package metadata must not contain machine-specific build paths.'
    }
    if ($report.packageBaseName -ne $release.packageBaseName) {
        throw 'Package build or compiler hash metadata is invalid.'
    }

    $baseName = [string]$release.packageBaseName
    $expectedNames = @(
        ($baseName + '.zip'), ($baseName + '-Setup.exe'), 'payload-manifest.json',
        'toolchain.json', 'build-report.json', 'INNO-SETUP-LICENSE.txt', 'SHA256SUMS.json'
    )
    $actualNames = @(Get-ChildItem -LiteralPath $packagePath -Force | Where-Object { -not $_.PSIsContainer } | ForEach-Object Name)
    foreach ($name in $expectedNames) {
        if ($name -notin $actualNames) { throw "Missing candidate package file: '$name'." }
    }
    foreach ($name in $actualNames) {
        if ($name -notin $expectedNames) { throw "Extra candidate package file: '$name'." }
    }
    if (Get-ChildItem -LiteralPath $packagePath -Directory -Force) { throw 'Extra candidate package directory found.' }

    $deliveryFiles = @{}
    foreach ($file in Get-ChildItem -LiteralPath $packagePath -File -Force | Where-Object { $_.Name -ne 'SHA256SUMS.json' }) {
        $deliveryFiles[$file.Name] = @{ Length = $file.Length; Sha256 = Get-Sha256HexFromFile $file.FullName }
    }
    Assert-ManifestMatchesFiles -Records @($checksums.files) -Actual $deliveryFiles -Label 'delivery checksum'

    $setupPath = Join-Path $packagePath ($baseName + '-Setup.exe')
    $setupInfo = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($setupPath)
    $setupFileVersion = ([string]$setupInfo.FileVersion).Trim()
    $setupProductVersion = ([string]$setupInfo.ProductVersion).Trim()
    if ($setupFileVersion -ne $release.fileVersion -or $setupProductVersion -ne $release.productVersion) {
        throw "Setup PE version metadata does not match the release manifest (Setup: $setupFileVersion, $setupProductVersion)."
    }
    if ($baseName -ne ('LG-Dsk-' + $release.productVersion + '-win-x64')) {
        throw 'Package base name does not match the release product version.'
    }

    $zipPath = Join-Path $packagePath ($baseName + '.zip')
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [System.IO.Compression.ZipFile]::OpenRead($zipPath)
    try {
        $actualPayload = @{}
        foreach ($entry in $archive.Entries) {
            if ([string]::IsNullOrWhiteSpace($entry.Name)) { continue }
            $relative = $entry.FullName.Replace('\', '/')
            Assert-PayloadIsAllowed -Path $relative
            if ($actualPayload.ContainsKey($relative)) { throw "Duplicate payload path: '$relative'." }
            $stream = $entry.Open()
            try {
                $sha = [System.Security.Cryptography.SHA256]::Create()
                try {
                    $hashBytes = $sha.ComputeHash($stream)
                    $hash = [System.BitConverter]::ToString($hashBytes).Replace('-', '')
                }
                finally { $sha.Dispose() }
            }
            finally { $stream.Dispose() }
            $actualPayload[$relative] = @{ Length = $entry.Length; Sha256 = $hash }
        }
    }
    finally { $archive.Dispose() }
    Assert-ManifestMatchesFiles -Records @($payload.files) -Actual $actualPayload -Label 'payload manifest'

    $requiredPayload = @(
        'LGDsk.exe', 'LICENSE.txt', 'DOTNET-RUNTIME-LICENSE.txt', 'ThirdPartyNotices.txt', 'LG-Dsk-LICENSE.txt',
        'WINDOWS-SDK-LICENSE.txt', 'INNO-SETUP-LICENSE.txt', 'UNICODE-LICENSE.txt',
        'THIRD-PARTY-NOTICES.md', 'RELEASE-NOTES.md'
    )
    foreach ($name in $requiredPayload) {
        if (-not $actualPayload.ContainsKey($name)) {
            if ($name -eq 'RELEASE-NOTES.md') { throw 'Payload is missing RELEASE-NOTES.md.' }
            throw "Package is missing required license or attribution file '$name'."
        }
    }

    $toolchain = Get-JsonObject -Path (Join-Path $packagePath 'toolchain.json')
    if ($toolchain.PSObject.Properties.Name -contains 'innoCompilerPath' -or
        [string]$toolchain.innoCompilerSha256 -notmatch '^[A-Fa-f0-9]{64}$') {
        throw 'Package toolchain metadata contains a local path or invalid compiler hash.'
    }
    if ([string]::IsNullOrWhiteSpace([string]$toolchain.sdkVersion) -or
        [string]::IsNullOrWhiteSpace([string]$toolchain.runtimeVersion) -or
        [string]::IsNullOrWhiteSpace([string]$toolchain.innoVersion) -or
        $toolchain.sdkVersion -ne $release.dotnetSdkVersion -or
        $toolchain.runtimeVersion -ne $release.runtimeVersion -or
        $toolchain.innoVersion -ne $release.innoVersion) {
        throw 'Package toolchain metadata does not match the verified release manifest.'
    }
    Write-Output "PASS: verified $baseName (portable ZIP, installer, checksums, payload, notices, and version metadata)."
    exit 0
}
catch {
    [Console]::Error.WriteLine($_.Exception.Message)
    exit 1
}
