# Creates a portable ZIP and a per-user Inno Setup installer from a self-contained publish.
# Keep this script ASCII-only so Windows PowerShell 5.1 can parse it on localized systems.

[CmdletBinding()]
param(
    [string]$PublishDirectory,
    [string]$OutputDirectory,
    [string]$DotnetRoot,
    [string]$InnoCompilerPath,
    [string]$ReleaseNotesPath,
    [string]$DotnetPath
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot

function Test-PathOverlap {
    param([string]$Path, [string]$OtherPath)

    $left = [System.IO.Path]::GetFullPath($Path).TrimEnd('\')
    $right = [System.IO.Path]::GetFullPath($OtherPath).TrimEnd('\')
    if ($left.Equals($right, [System.StringComparison]::OrdinalIgnoreCase)) { return $true }
    $leftPrefix = $left + [System.IO.Path]::DirectorySeparatorChar
    $rightPrefix = $right + [System.IO.Path]::DirectorySeparatorChar
    return $left.StartsWith($rightPrefix, [System.StringComparison]::OrdinalIgnoreCase) -or
        $right.StartsWith($leftPrefix, [System.StringComparison]::OrdinalIgnoreCase)
}

function Assert-SafeOutputTarget {
    param([string]$Path, [string]$AllowedRoot, [string]$ProtectedDataRoot)

    $resolved = [System.IO.Path]::GetFullPath($Path).TrimEnd('\')
    $allowed = [System.IO.Path]::GetFullPath($AllowedRoot).TrimEnd('\')
    $prefix = $allowed + [System.IO.Path]::DirectorySeparatorChar
    if (-not $resolved.StartsWith($prefix, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Output path must be a new child of '$allowed'."
    }
    if (Test-PathOverlap -Path $resolved -OtherPath $ProtectedDataRoot) {
        throw 'Output path overlaps protected user data.'
    }
    if (Test-Path -LiteralPath $resolved) { throw "Output path already exists: '$resolved'." }

    $current = [System.IO.Directory]::GetParent($resolved)
    while ($null -ne $current) {
        if (Test-Path -LiteralPath $current.FullName) {
            $item = Get-Item -LiteralPath $current.FullName -Force
            if (($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "Output path has a reparse-point ancestor: '$($current.FullName)'."
            }
        }
        if ($current.FullName.TrimEnd('\').Equals($allowed, [System.StringComparison]::OrdinalIgnoreCase)) { break }
        $current = $current.Parent
    }
}

function Get-Sha256HexFromFile {
    param([string]$Path)
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { throw "Required file is missing: '$Path'." }
    $stream = [System.IO.File]::OpenRead($Path)
    try {
        $sha = [System.Security.Cryptography.SHA256]::Create()
        try { return [System.BitConverter]::ToString($sha.ComputeHash($stream)).Replace('-', '') }
        finally { $sha.Dispose() }
    }
    finally { $stream.Dispose() }
}

function Assert-RequiredLicenseSources {
    param(
        [string]$DotnetRoot,
        [string]$Stage,
        [string]$RepositoryRoot = $repoRoot
    )

    if ([string]::IsNullOrWhiteSpace($RepositoryRoot)) { throw 'RepositoryRoot is required to stage release notices.' }
    if (-not (Test-Path -LiteralPath $DotnetRoot -PathType Container)) { throw "The .NET license directory is missing: '$DotnetRoot'." }
    if (-not (Test-Path -LiteralPath $Stage -PathType Container)) { throw "The package stage is missing: '$Stage'." }

    $props = [xml](Get-Content -LiteralPath (Join-Path $RepositoryRoot 'Directory.Build.props') -Raw)
    $version = [string]$props.Project.PropertyGroup.Version
    if ([string]::IsNullOrWhiteSpace($version)) { throw 'Could not resolve the product version for release notes.' }
    $notes = Join-Path $RepositoryRoot ("docs/RELEASE-NOTES-LG-Dsk-{0}.md" -f $version)
    if (-not (Test-Path -LiteralPath $notes -PathType Leaf)) { throw "Versioned release notes are missing: '$notes'." }

    $sources = @(
        @{ Source = (Join-Path $DotnetRoot 'LICENSE.txt'); Target = 'DOTNET-RUNTIME-LICENSE.txt'; Label = 'license' },
        @{ Source = (Join-Path $DotnetRoot 'ThirdPartyNotices.txt'); Target = 'ThirdPartyNotices.txt'; Label = 'license' },
        @{ Source = (Join-Path $RepositoryRoot 'LICENSE'); Target = 'LG-Dsk-LICENSE.txt'; Label = 'license' },
        @{ Source = (Join-Path $RepositoryRoot 'docs/third-party/windows-sdk-license-en-us.txt'); Target = 'WINDOWS-SDK-LICENSE.txt'; Label = 'license' },
        @{ Source = (Join-Path $RepositoryRoot 'docs/third-party/inno-setup-license.txt'); Target = 'INNO-SETUP-LICENSE.txt'; Label = 'license' },
        @{ Source = (Join-Path $RepositoryRoot 'docs/third-party/unicode-license-v3.txt'); Target = 'UNICODE-LICENSE.txt'; Label = 'license' },
        @{ Source = (Join-Path $RepositoryRoot 'docs/THIRD-PARTY-NOTICES.md'); Target = 'THIRD-PARTY-NOTICES.md'; Label = 'license' },
        @{ Source = $notes; Target = 'RELEASE-NOTES.md'; Label = 'release-notes.md' }
    )

    foreach ($item in $sources) {
        if (-not (Test-Path -LiteralPath $item.Source -PathType Leaf)) { throw "Required $($item.Label) source is missing: '$($item.Source)'." }
        $target = Join-Path $Stage $item.Target
        if (Test-Path -LiteralPath $target -PathType Leaf) {
            if ((Get-Sha256HexFromFile $target) -ne (Get-Sha256HexFromFile $item.Source)) {
                throw "Staged $($item.Label) differs from its checked-in source: '$($item.Target)'."
            }
        }
        else {
            Copy-Item -LiteralPath $item.Source -Destination $target
        }
    }
}

function Get-InnoCompileArguments {
    param(
        [string]$AppId,
        [string]$ProductVersion,
        [string]$FileVersion,
        [string]$PackageBaseName,
        [string]$OutputDirectory,
        [string]$ScriptPath
    )

    return @(
        ('/DAppIdValue=' + $AppId),
        ('/DProductVersion=' + $ProductVersion),
        ('/DFileVersion=' + $FileVersion),
        ('/DPackageBaseName=' + $PackageBaseName),
        ('/O' + $OutputDirectory),
        $ScriptPath
    )
}

function Resolve-Dotnet {
    param([string]$Explicit)
    if ($Explicit) {
        if (-not (Test-Path -LiteralPath $Explicit -PathType Leaf)) { throw "dotnet not found at '$Explicit'." }
        return $Explicit
    }
    if ($env:GLASSDESK_DOTNET -and (Test-Path -LiteralPath $env:GLASSDESK_DOTNET -PathType Leaf)) { return $env:GLASSDESK_DOTNET }
    if (Test-Path -LiteralPath 'D:\dotnet10\dotnet.exe') { return 'D:\dotnet10\dotnet.exe' }
    $command = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($command) { return $command.Source }
    throw 'No .NET SDK found. Pass -DotnetPath or install the .NET 10 SDK.'
}

function Resolve-InnoCompiler {
    param([string]$Explicit)
    if ($Explicit) {
        if (-not (Test-Path -LiteralPath $Explicit -PathType Leaf)) { throw "Inno Setup compiler not found at '$Explicit'." }
        return $Explicit
    }
    if ($env:INNO_SETUP_COMPILER -and (Test-Path -LiteralPath $env:INNO_SETUP_COMPILER -PathType Leaf)) { return $env:INNO_SETUP_COMPILER }
    $candidates = @(
        'C:\Program Files (x86)\Inno Setup 7\ISCC.exe',
        'C:\Program Files\Inno Setup 7\ISCC.exe',
        'C:\Program Files (x86)\Inno Setup 6\ISCC.exe',
        'C:\Program Files\Inno Setup 6\ISCC.exe'
    )
    foreach ($candidate in $candidates) { if (Test-Path -LiteralPath $candidate -PathType Leaf) { return $candidate } }
    throw 'ISCC.exe was not found. Install Inno Setup or pass -InnoCompilerPath.'
}

function Resolve-InnoSetupVersion {
    param([string]$CompilerPath)

    $compilerDirectory = [System.IO.Path]::GetFullPath((Split-Path -Parent $CompilerPath)).TrimEnd('\')
    $keys = @(
        'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\Inno Setup 7_is1',
        'HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall\Inno Setup 7_is1',
        'HKLM:\Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\Inno Setup 7_is1',
        'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\Inno Setup 6_is1',
        'HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall\Inno Setup 6_is1',
        'HKLM:\Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\Inno Setup 6_is1'
    )
    foreach ($key in $keys) {
        if (-not (Test-Path -LiteralPath $key)) { continue }
        $entry = Get-ItemProperty -LiteralPath $key
        $registered = [string]$entry.'Inno Setup: App Path'
        if (-not $registered) { $registered = [string]$entry.InstallLocation }
        if (-not $registered) { continue }
        try { $registered = [System.IO.Path]::GetFullPath($registered).TrimEnd('\') }
        catch { continue }
        if ($registered.Equals($compilerDirectory, [System.StringComparison]::OrdinalIgnoreCase)) {
            $version = [string]$entry.DisplayVersion
            if ($version -match '^\d+\.\d+\.\d+$') { return $version }
        }
    }
    throw 'Could not verify the compiler version from the matching Inno Setup installation record.'
}

function Invoke-WaitingProcess {
    param([string]$FilePath, [string[]]$Arguments, [string]$WorkingDirectory = (Get-Location).Path)
    $startInfo = New-Object System.Diagnostics.ProcessStartInfo
    $startInfo.FileName = $FilePath
    $startInfo.WorkingDirectory = $WorkingDirectory
    $startInfo.UseShellExecute = $false
    $quoted = foreach ($value in $Arguments) {
        ConvertTo-ProcessArgument -Value $value
    }
    $startInfo.Arguments = $quoted -join ' '
    $process = New-Object System.Diagnostics.Process
    try {
        $process.StartInfo = $startInfo
        if (-not $process.Start()) { throw "Could not start '$FilePath'." }
        $process.WaitForExit()
        return $process.ExitCode
    }
    finally { $process.Dispose() }
}

function ConvertTo-ProcessArgument {
    param([string]$Value)

    $builder = New-Object System.Text.StringBuilder
    [void]$builder.Append([char]34)
    $backslashes = 0
    foreach ($character in $Value.ToCharArray()) {
        if ($character -eq [char]92) { $backslashes++; continue }
        if ($character -eq [char]34) {
            [void]$builder.Append([char]92, (2 * $backslashes) + 1)
            [void]$builder.Append([char]34)
        }
        else {
            [void]$builder.Append([char]92, $backslashes)
            [void]$builder.Append($character)
        }
        $backslashes = 0
    }
    if ($backslashes -gt 0) { [void]$builder.Append([char]92, 2 * $backslashes) }
    [void]$builder.Append([char]34)
    return $builder.ToString()
}

function New-ReleaseZip {
    param([string]$SourceDirectory, [string]$ZipPath)
    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $stream = [System.IO.File]::Open($ZipPath, [System.IO.FileMode]::CreateNew, [System.IO.FileAccess]::ReadWrite, [System.IO.FileShare]::None)
    try {
        $archive = New-Object System.IO.Compression.ZipArchive($stream, [System.IO.Compression.ZipArchiveMode]::Create, $false)
        try {
            foreach ($file in Get-ChildItem -LiteralPath $SourceDirectory -Recurse -File | Sort-Object FullName) {
                $relative = $file.FullName.Substring($SourceDirectory.TrimEnd('\').Length + 1).Replace('\', '/')
                $entry = $archive.CreateEntry($relative, [System.IO.Compression.CompressionLevel]::Optimal)
                $inputStream = [System.IO.File]::OpenRead($file.FullName)
                try {
                    $outputStream = $entry.Open()
                    try { $inputStream.CopyTo($outputStream) } finally { $outputStream.Dispose() }
                }
                finally { $inputStream.Dispose() }
            }
        }
        finally { $archive.Dispose() }
    }
    finally { $stream.Dispose() }
}

if (-not $OutputDirectory) {
    $OutputDirectory = Join-Path $repoRoot ("artifacts/packages/LG-Dsk-candidate-{0}" -f [guid]::NewGuid().ToString('N'))
}
$packageRoot = Join-Path $repoRoot 'artifacts/packages'
if (-not (Test-Path -LiteralPath $packageRoot)) { New-Item -ItemType Directory -Path $packageRoot | Out-Null }
$protectedDataRoot = Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData)) 'LG Dsk\Data'
Assert-SafeOutputTarget -Path $OutputDirectory -AllowedRoot $packageRoot -ProtectedDataRoot $protectedDataRoot

$dotnet = Resolve-Dotnet -Explicit $DotnetPath
$compiler = Resolve-InnoCompiler -Explicit $InnoCompilerPath
$props = [xml](Get-Content -LiteralPath (Join-Path $repoRoot 'Directory.Build.props') -Raw)
$propertyGroup = $props.Project.PropertyGroup | Where-Object { $_.Version } | Select-Object -First 1
if (-not $ProductVersion) { $ProductVersion = [string]$propertyGroup.Version }
$assemblyVersion = [string]$propertyGroup.AssemblyVersion
$fileVersion = [string]$propertyGroup.FileVersion
$releaseManifest = Get-Content -LiteralPath (Join-Path $repoRoot 'packaging/release.json') -Raw -Encoding UTF8 | ConvertFrom-Json
$appId = [string]$releaseManifest.appId
if ($ProductVersion -notmatch '^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$' -or
    $assemblyVersion -notmatch '^\d+\.\d+\.\d+\.\d+$' -or $fileVersion -notmatch '^\d+\.\d+\.\d+\.\d+$') {
    throw 'Directory.Build.props contains an invalid release version.'
}
if ($releaseManifest.productName -ne 'LG Dsk' -or $releaseManifest.productVersion -ne $ProductVersion -or
    $releaseManifest.assemblyVersion -ne $assemblyVersion -or $releaseManifest.fileVersion -ne $fileVersion -or
    $releaseManifest.rid -ne 'win-x64' -or $releaseManifest.status -ne 'local-candidate') {
    throw 'packaging/release.json must exactly match the product identity and versions in Directory.Build.props.'
}
$packageBaseName = "LG-Dsk-$ProductVersion-win-x64"
$outputFullPath = [System.IO.Path]::GetFullPath($OutputDirectory)
$stageDirectory = Join-Path ([System.IO.Path]::GetTempPath()) ("LG-Dsk-package-stage-{0}" -f [guid]::NewGuid().ToString('N'))
$previousStage = $env:GLASSDESK_PACKAGE_STAGE

try {
    New-Item -ItemType Directory -Path $outputFullPath | Out-Null
    New-Item -ItemType Directory -Path $stageDirectory | Out-Null

    if (-not $PublishDirectory) {
        $publishRoot = Join-Path $repoRoot ("artifacts/publish/LG-Dsk-$ProductVersion-{0}" -f [guid]::NewGuid().ToString('N'))
        & (Join-Path $PSScriptRoot 'publish.ps1') -DotnetPath $dotnet -OutputDirectory $publishRoot `
            -ProductVersion $ProductVersion -AssemblyVersion $assemblyVersion -FileVersion $fileVersion
        if ($LASTEXITCODE -ne 0) { throw "Self-contained publish failed with exit code $LASTEXITCODE." }
        $PublishDirectory = $publishRoot
    }
    $PublishDirectory = [System.IO.Path]::GetFullPath($PublishDirectory)
    $appExecutable = Join-Path $PublishDirectory 'LGDsk.exe'
    if (-not (Test-Path -LiteralPath $appExecutable -PathType Leaf)) { throw "Publish directory does not contain LGDsk.exe: '$PublishDirectory'." }

    Get-ChildItem -LiteralPath $PublishDirectory -Force | Copy-Item -Destination $stageDirectory -Recurse -Force
    $releaseNotes = if ($ReleaseNotesPath) { $ReleaseNotesPath } else { Join-Path $repoRoot "docs/RELEASE-NOTES-LG-Dsk-$ProductVersion.md" }
    if (-not (Test-Path -LiteralPath $releaseNotes -PathType Leaf)) { throw "Versioned release notes are missing: '$releaseNotes'." }
    Copy-Item -LiteralPath $releaseNotes -Destination (Join-Path $stageDirectory 'RELEASE-NOTES.md') -Force
    Set-Content -LiteralPath (Join-Path $stageDirectory 'portable.flag') -Value 'LG Dsk portable deployment marker' -Encoding ASCII
    if (-not $DotnetRoot) { $DotnetRoot = Join-Path $repoRoot 'docs/third-party/dotnet-10.0.12' }
    Assert-RequiredLicenseSources -DotnetRoot $DotnetRoot -Stage $stageDirectory -RepositoryRoot $repoRoot

    $forbidden = Get-ChildItem -LiteralPath $stageDirectory -Recurse -File | Where-Object {
        $_.Extension -in @('.pdb', '.cs', '.xaml', '.csproj', '.slnx') -or $_.Name -match '\.Tests\.dll$'
    }
    if ($forbidden) { throw "Developer-only file is present in the release stage: '$($forbidden[0].FullName)'." }

    $env:GLASSDESK_PACKAGE_STAGE = $stageDirectory
    $innoArguments = Get-InnoCompileArguments -AppId $appId -ProductVersion $ProductVersion -FileVersion $fileVersion `
        -PackageBaseName $packageBaseName -OutputDirectory $outputFullPath -ScriptPath (Join-Path $repoRoot 'packaging/GlassDesk-Capture.iss')
    $compileExit = Invoke-WaitingProcess -FilePath $compiler -Arguments $innoArguments -WorkingDirectory $repoRoot
    if ($compileExit -ne 0) { throw "Inno Setup compilation failed with exit code $compileExit." }

    $zipPath = Join-Path $outputFullPath ($packageBaseName + '.zip')
    New-ReleaseZip -SourceDirectory $stageDirectory -ZipPath $zipPath
    $files = foreach ($file in Get-ChildItem -LiteralPath $stageDirectory -Recurse -File | Sort-Object FullName) {
        $relative = $file.FullName.Substring($stageDirectory.TrimEnd('\').Length + 1).Replace('\', '/')
        @{ relativePath = $relative; length = $file.Length; sha256 = Get-Sha256HexFromFile $file.FullName }
    }
    $payloadManifest = [ordered]@{
        schemaVersion = 1; productName = 'LG Dsk'; productVersion = $ProductVersion
        assemblyVersion = $assemblyVersion; fileVersion = $fileVersion; appId = $appId; rid = 'win-x64'; files = @($files)
    }
    $payloadManifestPath = Join-Path $outputFullPath 'payload-manifest.json'
    $payloadManifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $payloadManifestPath -Encoding UTF8

    $sdkVersion = (& $dotnet --version).Trim()
    $runtimeVersion = '10.0.12'
    $runtimeLine = & $dotnet --list-runtimes | Where-Object { $_ -match '^Microsoft\.NETCore\.App\s+10\.' } | Select-Object -First 1
    if ($runtimeLine -match '^Microsoft\.NETCore\.App\s+([^\s]+)') { $runtimeVersion = $Matches[1] }
    $compilerVersion = Resolve-InnoSetupVersion -CompilerPath $compiler
    if ($compilerVersion -ne $releaseManifest.innoVersion) {
        throw "Inno Setup $($releaseManifest.innoVersion) is required; selected compiler is $compilerVersion."
    }
    $toolchain = [ordered]@{
        schemaVersion = 1; sdkVersion = $sdkVersion; runtimeVersion = $runtimeVersion
        innoVersion = $compilerVersion; innoCompilerSha256 = Get-Sha256HexFromFile $compiler
    }
    $toolchainPath = Join-Path $outputFullPath 'toolchain.json'
    $toolchain | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $toolchainPath -Encoding UTF8

    $report = [ordered]@{
        schemaVersion = 1; productName = 'LG Dsk'; productVersion = $ProductVersion
        assemblyVersion = $assemblyVersion; fileVersion = $fileVersion; appId = $appId
        rid = 'win-x64'; status = 'Built'; packageBaseName = $packageBaseName
    }
    $reportPath = Join-Path $outputFullPath 'build-report.json'
    $report | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $reportPath -Encoding UTF8

    $innoLicense = Join-Path $outputFullPath 'INNO-SETUP-LICENSE.txt'
    Copy-Item -LiteralPath (Join-Path $repoRoot 'docs/third-party/inno-setup-license.txt') -Destination $innoLicense -Force
    $checksummedFiles = Get-ChildItem -LiteralPath $outputFullPath -File | Where-Object { $_.Name -ne 'SHA256SUMS.json' } | Sort-Object Name
    $checksumRecords = foreach ($file in $checksummedFiles) { @{ relativePath = $file.Name; length = $file.Length; sha256 = Get-Sha256HexFromFile $file.FullName } }
    $checksumManifest = [ordered]@{ schemaVersion = 1; files = @($checksumRecords) }
    $checksumPath = Join-Path $outputFullPath 'SHA256SUMS.json'
    $checksumManifest | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $checksumPath -Encoding UTF8

    Write-Host "Package candidate: $outputFullPath" -ForegroundColor Green
    Write-Host "Installer: $(Join-Path $outputFullPath ($packageBaseName + '-Setup.exe'))"
    Write-Host "Portable: $zipPath"
}
finally {
    $env:GLASSDESK_PACKAGE_STAGE = $previousStage
    if (Test-Path -LiteralPath $stageDirectory) { Remove-Item -LiteralPath $stageDirectory -Recurse -Force }
}
