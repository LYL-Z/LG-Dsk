[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$NewInstallDirectory
)

$ErrorActionPreference = 'Stop'
$uninstallKeyPath = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\GlassDesk.Independent_is1'
$runKeyPath = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$runValueName = 'GlassDesk.Independent'
$stageName = 'LGDsk-MigrationStaging'
$logPath = Join-Path $env:TEMP 'LG-Dsk-install-migration.log'
$script:stageRoot = $null
$script:preserveStageOnError = $false

function Write-MigrationLog {
    param([string]$Message)
    Add-Content -LiteralPath $logPath -Value (('{0:o} {1}' -f [DateTime]::UtcNow, $Message)) -Encoding UTF8
}

function Get-NormalizedPath {
    param([string]$Path)
    $fullPath = [IO.Path]::GetFullPath($Path)
    $pathSeparators = [char[]]@([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
    $pathRoot = [IO.Path]::GetPathRoot($fullPath)
    if ([string]::Equals($fullPath.TrimEnd($pathSeparators), $pathRoot.TrimEnd($pathSeparators), [StringComparison]::OrdinalIgnoreCase)) {
        return $pathRoot
    }
    return $fullPath.TrimEnd($pathSeparators)
}

function Assert-InstallDirectory {
    param([string]$Path)
    $normalized = Get-NormalizedPath $Path
    $root = [IO.Path]::GetPathRoot($normalized)
    if ([string]::Equals($normalized, $root, [StringComparison]::OrdinalIgnoreCase)) {
        throw ('拒绝将卷根目录用作安装目录：' + $normalized)
    }
}

function Test-SamePath {
    param([string]$Left, [string]$Right)
    return [string]::Equals((Get-NormalizedPath $Left), (Get-NormalizedPath $Right), [StringComparison]::OrdinalIgnoreCase)
}

function Test-ChildPath {
    param([string]$Root, [string]$Candidate)
    $prefix = (Get-NormalizedPath $Root) + [IO.Path]::DirectorySeparatorChar
    return (Get-NormalizedPath $Candidate).StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)
}

function Assert-NoReparsePoints {
    param([string]$Path)
    $current = Get-NormalizedPath $Path
    while (-not [string]::IsNullOrWhiteSpace($current)) {
        if (Test-Path -LiteralPath $current) {
            $item = Get-Item -LiteralPath $current -Force
            if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw ('拒绝处理包含联接或符号链接的路径：' + $current)
            }
        }
        $parent = [IO.Directory]::GetParent($current)
        if ($null -eq $parent) { break }
        $current = $parent.FullName
    }
}

function Assert-NoReparsePointsInTree {
    param([string]$Directory)
    $root = Get-NormalizedPath $Directory
    if (-not (Test-Path -LiteralPath $root -PathType Container)) {
        throw ('预期的迁移目录不存在：' + $root)
    }
    Assert-NoReparsePoints $root

    $pending = New-Object 'System.Collections.Generic.Stack[string]'
    $pending.Push($root)
    while ($pending.Count -gt 0) {
        $current = $pending.Pop()
        foreach ($entry in [IO.Directory]::EnumerateFileSystemEntries($current)) {
            $attributes = [IO.File]::GetAttributes($entry)
            if (($attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw ('迁移目录中包含联接或符号链接：' + $entry)
            }
            if (($attributes -band [IO.FileAttributes]::Directory) -ne 0) {
                $pending.Push($entry)
            }
        }
    }
}

function Copy-UserDataTree {
    param([string]$SourceDirectory, [string]$DestinationDirectory)

    $sourceRoot = Get-NormalizedPath $SourceDirectory
    $destinationRoot = Get-NormalizedPath $DestinationDirectory
    Assert-NoReparsePoints $sourceRoot
    [IO.Directory]::CreateDirectory($destinationRoot) | Out-Null

    $pending = New-Object 'System.Collections.Generic.Stack[string]'
    $pending.Push($sourceRoot)
    $fileCount = 0

    while ($pending.Count -gt 0) {
        $currentSource = $pending.Pop()
        $relativeDirectory = $currentSource.Substring($sourceRoot.Length).TrimStart([char[]]@('\', '/'))
        $currentDestination = if ([string]::IsNullOrEmpty($relativeDirectory)) {
            $destinationRoot
        } else {
            Join-Path $destinationRoot $relativeDirectory
        }
        [IO.Directory]::CreateDirectory($currentDestination) | Out-Null

        foreach ($entry in [IO.Directory]::EnumerateFileSystemEntries($currentSource)) {
            $attributes = [IO.File]::GetAttributes($entry)
            if (($attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw ('用户数据中包含联接或符号链接，已停止迁移：' + $entry)
            }

            $target = Join-Path $currentDestination ([IO.Path]::GetFileName($entry))
            if (($attributes -band [IO.FileAttributes]::Directory) -ne 0) {
                $pending.Push($entry)
            } else {
                [IO.File]::Copy($entry, $target, $false)
                [IO.File]::SetLastWriteTimeUtc($target, [IO.File]::GetLastWriteTimeUtc($entry))
                $sourceHash = (Get-FileHash -LiteralPath $entry -Algorithm SHA256).Hash
                $targetHash = (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash
                if ($sourceHash -ne $targetHash) {
                    throw ('迁移后的文件校验失败：' + $entry)
                }
                $fileCount++
            }
        }
    }

    Write-MigrationLog ('Verified {0} user-data files.' -f $fileCount)
}

function Get-UninstallerPath {
    param([string]$UninstallCommand, [string]$OldInstallDirectory)

    if ($UninstallCommand -match '^\s*"([^"]+\.exe)"(?:\s|$)') {
        $candidate = $Matches[1]
    } elseif ($UninstallCommand -match '^\s*([^\s]+\.exe)(?:\s|$)') {
        $candidate = $Matches[1]
    } else {
        throw '旧版卸载命令格式无法核验。'
    }

    $candidate = Get-NormalizedPath $candidate
    if (-not (Test-SamePath ([IO.Path]::GetDirectoryName($candidate)) $OldInstallDirectory)) {
        throw '旧版卸载程序不在已登记的安装目录中，已拒绝执行。'
    }
    if ([IO.Path]::GetFileName($candidate) -notmatch '^unins\d{3}\.exe$') {
        throw '旧版卸载程序名称无法核验，已拒绝执行。'
    }
    if (-not (Test-Path -LiteralPath $candidate -PathType Leaf)) {
        throw '旧版卸载程序不在已登记的安装目录中，已拒绝执行。'
    }

    Assert-NoReparsePoints $candidate
    return $candidate
}

function Get-ProductExecutable {
    param([string]$Directory)
    foreach ($name in @('LGDsk.exe', 'GlassDesk.exe')) {
        $candidate = Join-Path $Directory $name
        if (Test-Path -LiteralPath $candidate -PathType Leaf) { return $candidate }
    }
    return $null
}

function Get-ExistingProductExecutables {
    param([string]$Directory)
    $paths = foreach ($name in @('LGDsk.exe', 'GlassDesk.exe')) {
        $candidate = Join-Path $Directory $name
        if (Test-Path -LiteralPath $candidate -PathType Leaf) { $candidate }
    }
    return @($paths)
}

function Update-ProductStartupPath {
    param([string]$NewExecutable, [string]$PreviousExecutable)
    if (-not (Test-Path -LiteralPath $runKeyPath)) { return }
    $runKey = Get-ItemProperty -LiteralPath $runKeyPath
    $runProperty = $runKey.PSObject.Properties[$runValueName]
    $current = if ($null -ne $runProperty) { [string]$runProperty.Value } else { '' }
    $directory = Split-Path -Parent $PreviousExecutable
    $candidates = @($PreviousExecutable, (Join-Path $directory 'LGDsk.exe'), (Join-Path $directory 'GlassDesk.exe')) |
        Where-Object { $_ } | Select-Object -Unique
    foreach ($candidate in $candidates) {
        $expected = '"' + $candidate + '" --autostart'
        if ([string]::Equals($current.Trim(), $expected, [StringComparison]::OrdinalIgnoreCase)) {
            Set-ItemProperty -LiteralPath $runKeyPath -Name $runValueName -Value ('"' + $NewExecutable + '" --autostart')
            Write-MigrationLog 'Updated the existing product-owned login startup path.'
            return
        }
    }
}

try {
    $newDirectory = Get-NormalizedPath $NewInstallDirectory
    Assert-InstallDirectory $newDirectory
    Assert-NoReparsePoints $newDirectory
    if (Test-Path -LiteralPath (Join-Path $newDirectory '.git')) {
        throw '安装目标是 Git 仓库目录。请选择独立的应用程序目录。'
    }

    $script:stageRoot = Join-Path $newDirectory $stageName
    $stagedData = Join-Path $script:stageRoot 'Data'
    $stageMarker = Join-Path $script:stageRoot 'migration-source.txt'
    $stageStatePath = Join-Path $script:stageRoot 'migration-state.txt'
    $dataPresencePath = Join-Path $script:stageRoot 'data-present.txt'
    $newData = Join-Path $newDirectory 'Data'
    $reuseReadyStage = $false

    if (-not (Test-Path -LiteralPath $uninstallKeyPath)) {
        if (Test-Path -LiteralPath $stageMarker) {
            Assert-NoReparsePointsInTree $script:stageRoot
            $recoverySource = (Get-Content -LiteralPath $stageMarker -Raw).Trim()
            $recoveryState = if (Test-Path -LiteralPath $stageStatePath) { (Get-Content -LiteralPath $stageStatePath -Raw).Trim() } else { '' }
            if ($recoveryState -notin @('uninstall-started', 'uninstall-complete', 'data-restored')) {
                throw ('迁移暂存状态无法核验，暂存文件已保留：' + $script:stageRoot)
            }
            $recoverySource = Get-NormalizedPath $recoverySource
            Assert-InstallDirectory $recoverySource
            if (Test-SamePath $recoverySource $newDirectory) {
                throw ('迁移来源与目标路径相同；暂存文件已保留：' + $script:stageRoot)
            }
            if ((Test-ChildPath $recoverySource $newDirectory) -or (Test-ChildPath $newDirectory $recoverySource)) {
                throw ('迁移来源与目标路径彼此嵌套；暂存文件已保留：' + $script:stageRoot)
            }
            $recoveryOldExecutable = Join-Path $recoverySource 'GlassDesk.exe'
            $recoveryNewExecutable = Join-Path $newDirectory 'LGDsk.exe'
            if ((Get-ExistingProductExecutables $recoverySource).Count -gt 0) {
                throw ('旧程序文件仍存在。为保护迁移数据，安装已停止；暂存文件已保留：' + $script:stageRoot)
            }
            $recoveryHadData = if (Test-Path -LiteralPath $dataPresencePath) { (Get-Content -LiteralPath $dataPresencePath -Raw).Trim() } else { '' }
            if ($recoveryHadData -notin @('0', '1')) {
                throw ('恢复时无法核验用户数据状态；暂存文件已保留：' + $script:stageRoot)
            }
            if (($recoveryHadData -eq '0') -and (Test-Path -LiteralPath $stagedData)) {
                throw ('恢复时发现未登记的用户数据；暂存文件已保留：' + $script:stageRoot)
            }
            if (($recoveryHadData -eq '1') -and (Test-Path -LiteralPath $stagedData) -and -not (Test-Path -LiteralPath $stagedData -PathType Container)) {
                throw ('恢复时暂存的 Data 不是目录；暂存文件已保留：' + $script:stageRoot)
            }
            if ($recoveryHadData -eq '1') {
                if (Test-Path -LiteralPath $stagedData -PathType Container) {
                    Assert-NoReparsePointsInTree $stagedData
                    if (Test-Path -LiteralPath $newData) {
                        throw '迁移恢复目录和目标 Data 同时存在；为避免覆盖数据，安装已停止。'
                    }
                    Move-Item -LiteralPath $stagedData -Destination $newData
                } elseif (Test-Path -LiteralPath $newData -PathType Container) {
                    # The process may have stopped after moving Data but before cleaning its marker.
                    Assert-NoReparsePointsInTree $newData
                    Set-Content -LiteralPath $stageStatePath -Value 'data-restored' -Encoding UTF8
                } else {
                    throw ('恢复时未找到用户数据暂存副本或已恢复目录；暂存文件已保留：' + $script:stageRoot)
                }
                Set-Content -LiteralPath $stageStatePath -Value 'data-restored' -Encoding UTF8
            }
            if ($recoveryOldExecutable) { Update-ProductStartupPath $recoveryNewExecutable $recoveryOldExecutable }
            Assert-NoReparsePointsInTree $script:stageRoot
            Remove-Item -LiteralPath $script:stageRoot -Recurse -Force
            Write-MigrationLog 'Recovered staged user data and startup registration after a previous installer interruption.'
        } elseif (Test-Path -LiteralPath $script:stageRoot) {
            throw ('发现无有效标记的迁移临时目录；安装已停止，目录已保留：' + $script:stageRoot)
        }
        exit 0
    }

    $registration = Get-ItemProperty -LiteralPath $uninstallKeyPath
    if ($registration.DisplayName -notin @('GlassDesk', 'LG Dsk')) {
        throw '安装登记名称与 GlassDesk/LG Dsk 不符，已拒绝迁移。'
    }
    if ([string]::IsNullOrWhiteSpace([string]$registration.InstallLocation)) {
        throw '旧版安装登记没有安装路径。'
    }

    $oldDirectory = Get-NormalizedPath ([string]$registration.InstallLocation)
    Assert-InstallDirectory $oldDirectory
    $newExecutable = Join-Path $newDirectory 'LGDsk.exe'
    $oldExecutable = Get-ProductExecutable $oldDirectory
    if (-not $oldExecutable) {
        throw '已登记的 LG Dsk 安装目录中找不到主程序。'
    }
    $runningProcesses = @(Get-Process -Name 'GlassDesk', 'LGDsk' -ErrorAction SilentlyContinue)
    foreach ($process in $runningProcesses) {
        try {
            if (Test-SamePath $process.MainModule.FileName $oldExecutable) {
                throw '请先从系统托盘完全退出 LG Dsk，再重新运行安装程序。'
            }
        } catch [System.Management.Automation.MethodInvocationException] {
            throw '无法确认 LG Dsk 是否仍在运行。请先从系统托盘退出所有 LG Dsk 窗口后重试。'
        }
    }
    if (Test-SamePath $oldDirectory $newDirectory) {
        Write-MigrationLog 'Existing install already uses the selected directory; in-place upgrade.'
        exit 0
    }
    if ((Test-ChildPath $oldDirectory $newDirectory) -or (Test-ChildPath $newDirectory $oldDirectory)) {
        throw '新旧安装目录彼此嵌套，已拒绝迁移。'
    }

    Assert-NoReparsePoints $oldExecutable

    $uninstaller = Get-UninstallerPath ([string]$registration.UninstallString) $oldDirectory
    $runCommand = '"' + $newExecutable + '" --autostart'
    if ($runCommand.Length -gt 260) {
        throw '所选安装路径过长，无法安全迁移登录启动项。'
    }

    $oldData = Join-Path $oldDirectory 'Data'
    if ((Test-Path -LiteralPath $oldData) -and -not (Test-Path -LiteralPath $oldData -PathType Container)) {
        throw '旧安装中的 Data 不是目录；为避免漏迁移用户文件，安装已停止。'
    }
    if (Test-Path -LiteralPath $script:stageRoot) {
        Assert-NoReparsePointsInTree $script:stageRoot
        $existingMarker = if (Test-Path -LiteralPath $stageMarker) { Get-Content -LiteralPath $stageMarker -Raw } else { '' }
        if (-not [string]::Equals($existingMarker.Trim(), $oldDirectory, [StringComparison]::OrdinalIgnoreCase)) {
            throw '发现无法识别的旧迁移临时目录；为避免删除用户文件，安装已停止。'
        }
        $existingState = if (Test-Path -LiteralPath $stageStatePath) { (Get-Content -LiteralPath $stageStatePath -Raw).Trim() } else { '' }
        if ($existingState -in @('uninstall-started', 'uninstall-complete')) {
            throw ('上次迁移在卸载阶段中断。为保护数据，安装已停止；请保留此暂存目录并查看日志：' + $script:stageRoot)
        }
        if ($existingState -eq 'ready') {
            Assert-NoReparsePoints $script:stageRoot
            $presenceValue = if (Test-Path -LiteralPath $dataPresencePath) { (Get-Content -LiteralPath $dataPresencePath -Raw).Trim() } else { '' }
            if ($presenceValue -notin @('0', '1')) {
                throw ('无法核验暂存目录中的用户数据状态；暂存文件已保留：' + $script:stageRoot)
            }
            if (($presenceValue -eq '1') -and -not (Test-Path -LiteralPath $stagedData -PathType Container)) {
                throw ('用户数据暂存副本不完整；暂存文件已保留：' + $script:stageRoot)
            }
            if (($presenceValue -eq '1') -and (Test-Path -LiteralPath $stagedData -PathType Container)) {
                Assert-NoReparsePointsInTree $stagedData
            }
            if (($presenceValue -eq '0') -and (Test-Path -LiteralPath $stagedData)) {
                throw ('发现未登记的暂存数据；为避免误用，暂存文件已保留：' + $script:stageRoot)
            }
            $reuseReadyStage = $true
        } elseif ($existingState -eq 'copying') {
            $previouslyHadData = (Test-Path -LiteralPath $dataPresencePath) -and ((Get-Content -LiteralPath $dataPresencePath -Raw).Trim() -eq '1')
            if ($previouslyHadData -and -not (Test-Path -LiteralPath $oldData -PathType Container)) {
                throw ('旧数据源已不存在；为保护迁移文件，安装已停止。请保留此目录：' + $script:stageRoot)
            }
            Remove-Item -LiteralPath $script:stageRoot -Recurse -Force
        } else {
            throw ('发现无法核验的迁移状态；暂存文件已保留：' + $script:stageRoot)
        }
    }

    $migrationHasData = (Test-Path -LiteralPath $oldData -PathType Container) -or
        (Test-Path -LiteralPath $stagedData -PathType Container)
    if ($migrationHasData -and (Test-Path -LiteralPath $newData)) {
        Assert-NoReparsePoints $newData
        if (-not (Test-Path -LiteralPath $newData -PathType Container)) {
            throw '新安装路径中的 Data 已存在但不是目录；安装不会覆盖该文件。'
        }
        $existingNewData = @(Get-ChildItem -LiteralPath $newData -Force)
        if ($existingNewData.Count -gt 0) {
            throw '新安装目录已有 Data 文件。请备份后选择空目录，安装器不会覆盖它。'
        }
        Remove-Item -LiteralPath $newData -Force
    }

    if (-not $reuseReadyStage) {
        New-Item -ItemType Directory -Path $script:stageRoot -Force | Out-Null
        Set-Content -LiteralPath $stageMarker -Value $oldDirectory -Encoding UTF8
        $hasOldData = Test-Path -LiteralPath $oldData -PathType Container
        Set-Content -LiteralPath $stageStatePath -Value 'copying' -Encoding UTF8
        Set-Content -LiteralPath $dataPresencePath -Value $(if ($hasOldData) { '1' } else { '0' }) -Encoding UTF8
        if ($hasOldData) {
            New-Item -ItemType Directory -Path $stagedData -Force | Out-Null
            Copy-UserDataTree $oldData $stagedData
            Assert-NoReparsePointsInTree $stagedData
        }
        Set-Content -LiteralPath $stageStatePath -Value 'ready' -Encoding UTF8
    }

    Write-MigrationLog ('Starting previous uninstaller from {0}.' -f $oldDirectory)
    $script:preserveStageOnError = $true
    Set-Content -LiteralPath $stageStatePath -Value 'uninstall-started' -Encoding UTF8
    $uninstallProcess = Start-Process -FilePath $uninstaller -ArgumentList '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART' -WorkingDirectory $oldDirectory -WindowStyle Hidden -Wait -PassThru
    if ($uninstallProcess.ExitCode -ne 0) {
        throw ('旧版卸载程序返回错误码 {0}。' -f $uninstallProcess.ExitCode)
    }

    if (Test-Path -LiteralPath $uninstallKeyPath) {
        throw '旧版卸载后仍存在安装登记；为避免两份程序共用身份，安装已停止。'
    }
    Set-Content -LiteralPath $stageStatePath -Value 'uninstall-complete' -Encoding UTF8

    if ((Get-ExistingProductExecutables $oldDirectory).Count -gt 0) {
        throw '旧版程序文件仍存在，可能正在被占用。迁移数据已暂存，可关闭旧程序后重试。'
    }

    if (Test-Path -LiteralPath $stagedData -PathType Container) {
        if (Test-Path -LiteralPath $newData) {
            throw '新 Data 目录在迁移期间被创建；为避免覆盖数据，已保留迁移暂存文件。'
        }
        Move-Item -LiteralPath $stagedData -Destination $newData
        Set-Content -LiteralPath $stageStatePath -Value 'data-restored' -Encoding UTF8
    }

    Update-ProductStartupPath $newExecutable $oldExecutable

    Assert-NoReparsePointsInTree $script:stageRoot
    Remove-Item -LiteralPath $script:stageRoot -Recurse -Force
    Write-MigrationLog ('Migrated existing install from {0} to {1}.' -f $oldDirectory, $newDirectory)
    exit 0
} catch {
    try { Write-MigrationLog ('ERROR: ' + $_.Exception.Message) } catch { }
    if ($script:stageRoot -and (Test-Path -LiteralPath $script:stageRoot) -and -not $script:preserveStageOnError) {
        try {
            Assert-NoReparsePointsInTree $script:stageRoot
            Remove-Item -LiteralPath $script:stageRoot -Recurse -Force
        } catch { }
    }
    exit 1
}
