#requires -Version 5.1

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Get-AgenTallyStablePaths {
    [CmdletBinding()]
    param([string] $InstallRoot)

    $localAppData = [Environment]::GetFolderPath(
        [Environment+SpecialFolder]::LocalApplicationData)
    $userProfile = [Environment]::GetFolderPath(
        [Environment+SpecialFolder]::UserProfile)
    $appData = [Environment]::GetFolderPath(
        [Environment+SpecialFolder]::ApplicationData)
    $desktop = [Environment]::GetFolderPath(
        [Environment+SpecialFolder]::DesktopDirectory)
    if ([string]::IsNullOrWhiteSpace($localAppData) -or
        [string]::IsNullOrWhiteSpace($userProfile) -or
        [string]::IsNullOrWhiteSpace($appData) -or
        [string]::IsNullOrWhiteSpace($desktop)) {
        throw 'Required per-user Windows folders cannot be determined.'
    }

    $stableRoot = Join-Path $localAppData 'AgenTally\Stable'
    if ([string]::IsNullOrWhiteSpace($InstallRoot)) {
        $resolvedInstallRoot = Join-Path $localAppData 'Programs\AgenTally'
    }
    elseif (-not [System.IO.Path]::IsPathRooted($InstallRoot) -or
        $InstallRoot.StartsWith('\\', [System.StringComparison]::Ordinal)) {
        throw 'The Stable install directory must be an absolute local path.'
    }
    else {
        $resolvedInstallRoot = [System.IO.Path]::GetFullPath($InstallRoot)
    }

    $installVolumeRoot = [System.IO.Path]::GetPathRoot($resolvedInstallRoot)
    if ([string]::IsNullOrWhiteSpace($installVolumeRoot) -or
        $resolvedInstallRoot.TrimEnd('\', '/').Equals(
            $installVolumeRoot.TrimEnd('\', '/'),
            [System.StringComparison]::OrdinalIgnoreCase)) {
        throw 'The Stable install directory cannot be a volume root.'
    }

    $runtimeRoot = Join-Path $stableRoot 'runtime'
    $databasePath = Join-Path $stableRoot 'data\agentally.db'
    $codexHome = Join-Path $userProfile '.codex'
    $profileInput = 'profile|Stable|{0}|{1}' -f @(
        (ConvertTo-AgenTallyStableIdentityPath $databasePath),
        (ConvertTo-AgenTallyStableIdentityPath $codexHome))
    $profileId = Get-AgenTallyStableHash $profileInput
    $userId = Get-AgenTallyStableHash (
        'user|{0}|{1}' -f [Environment]::UserDomainName, [Environment]::UserName)

    return [pscustomobject]@{
        InstallRoot = [System.IO.Path]::GetFullPath($resolvedInstallRoot).TrimEnd('\', '/')
        DefaultInstallRoot = [System.IO.Path]::GetFullPath(
            (Join-Path $localAppData 'Programs\AgenTally')).TrimEnd('\', '/')
        StableRoot = [System.IO.Path]::GetFullPath($stableRoot)
        DataRoot = [System.IO.Path]::GetFullPath((Join-Path $stableRoot 'data'))
        RuntimeRoot = [System.IO.Path]::GetFullPath($runtimeRoot)
        LogRoot = [System.IO.Path]::GetFullPath((Join-Path $stableRoot 'logs'))
        TempRoot = [System.IO.Path]::GetFullPath((Join-Path $stableRoot 'temp'))
        ShortcutPath = [System.IO.Path]::GetFullPath((Join-Path $appData 'Microsoft\Windows\Start Menu\Programs\AgenTally.lnk'))
        InnoStartMenuShortcutPath = [System.IO.Path]::GetFullPath((Join-Path $appData 'Microsoft\Windows\Start Menu\Programs\AgenTally\AgenTally.lnk'))
        DesktopShortcutPath = [System.IO.Path]::GetFullPath((Join-Path $desktop 'AgenTally.lnk'))
        UninstallRegistryPath = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\AgenTally'
        InnoUninstallRegistryPath = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\{A59B3C1C-D735-4D8E-9357-4DF501455822}_is1'
        InstallRecordRegistryPath = 'HKCU:\Software\AgenTally\Stable'
        ProfileId = $profileId
        ShutdownEventName = 'Local\AgenTally.AppShutdown.Stable.{0}.{1}' -f @(
            $userId,
            $profileId)
        ShutdownRequestPath = [System.IO.Path]::GetFullPath((Join-Path $runtimeRoot (
            'application-shutdown-request-{0}.json' -f $profileId)))
    }
}

function Get-AgenTallyStableHash {
    [CmdletBinding()]
    param([Parameter(Mandatory)] [string] $Value)

    $algorithm = [System.Security.Cryptography.SHA256]::Create()
    try {
        $bytes = [System.Text.Encoding]::UTF8.GetBytes($Value)
        $hash = $algorithm.ComputeHash($bytes)
        $hex = -join ($hash | ForEach-Object { $_.ToString('X2') })
        return $hex.Substring(0, 24)
    }
    finally {
        $algorithm.Dispose()
    }
}

function Get-AgenTallyStableRegistryValue {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] [string] $RegistryPath,
        [Parameter(Mandatory)] [string] $ValueName
    )

    if (-not (Test-Path -LiteralPath $RegistryPath)) {
        return $null
    }

    $registryKey = Get-Item -LiteralPath $RegistryPath -ErrorAction Stop
    try {
        return $registryKey.GetValue(
            $ValueName,
            $null,
            [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)
    }
    finally {
        $registryKey.Close()
    }
}

function Write-AgenTallyStableUtf8JsonFile {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] [string] $Path,
        [Parameter(Mandatory)] $Value
    )

    $json = $Value | ConvertTo-Json -Compress
    [System.IO.File]::WriteAllText(
        $Path,
        $json,
        [System.Text.UTF8Encoding]::new($false))
}

function ConvertTo-AgenTallyStableIdentityPath {
    [CmdletBinding()]
    param([Parameter(Mandatory)] [string] $Path)

    return [System.IO.Path]::GetFullPath($Path).
        TrimEnd('\', '/').
        Replace('/', '\').
        ToUpperInvariant()
}

function Test-AgenTallyStablePathWithin {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] [string] $Candidate,
        [Parameter(Mandatory)] [string] $Root
    )

    $fullCandidate = [System.IO.Path]::GetFullPath($Candidate).TrimEnd('\', '/')
    $fullRoot = [System.IO.Path]::GetFullPath($Root).TrimEnd('\', '/')
    return $fullCandidate.Equals(
        $fullRoot,
        [System.StringComparison]::OrdinalIgnoreCase) -or
        $fullCandidate.StartsWith(
            $fullRoot + [System.IO.Path]::DirectorySeparatorChar,
            [System.StringComparison]::OrdinalIgnoreCase)
}

function Get-AgenTallyStableRelativePath {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] [string] $Path,
        [Parameter(Mandatory)] [string] $Root
    )

    $fullPath = [System.IO.Path]::GetFullPath($Path)
    $fullRoot = [System.IO.Path]::GetFullPath($Root)
    $comparisonRoot = $fullRoot.TrimEnd('\', '/')
    if (-not (Test-AgenTallyStablePathWithin -Candidate $fullPath -Root $fullRoot) -or
        $fullPath.TrimEnd('\', '/').Equals(
            $comparisonRoot,
            [System.StringComparison]::OrdinalIgnoreCase)) {
        throw 'The path cannot be represented below the approved Stable root.'
    }

    return $fullPath.Substring($comparisonRoot.Length).
        TrimStart('\', '/').
        Replace('\', '/')
}

function Assert-AgenTallyStableNoReparsePoint {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] [string] $Path,
        [Parameter(Mandatory)] [string] $Boundary
    )

    $fullPath = [System.IO.Path]::GetFullPath($Path)
    $fullBoundary = [System.IO.Path]::GetFullPath($Boundary)
    $comparisonBoundary = $fullBoundary.TrimEnd('\', '/')
    if (-not (Test-AgenTallyStablePathWithin -Candidate $fullPath -Root $fullBoundary)) {
        throw 'The Stable path escaped its approved boundary.'
    }

    $current = $fullPath
    while (Test-AgenTallyStablePathWithin -Candidate $current -Root $fullBoundary) {
        if (Test-Path -LiteralPath $current) {
            $item = Get-Item -LiteralPath $current -Force
            if (($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw 'A Stable owned path traverses a reparse point.'
            }
        }

        if ($current.TrimEnd('\', '/').Equals(
                $comparisonBoundary,
                [System.StringComparison]::OrdinalIgnoreCase)) {
            break
        }

        $current = Split-Path -Parent $current
    }
}

function Get-AgenTallyStableProcess {
    [CmdletBinding()]
    param([string] $InstallRoot)

    $paths = Get-AgenTallyStablePaths -InstallRoot $InstallRoot
    foreach ($process in @(Get-Process -ErrorAction Stop)) {
        try {
            try {
                if ($process.HasExited) {
                    continue
                }

                $processName = $process.ProcessName
                if (@('AgenTally.Core', 'AgenTally.UI') -notcontains $processName) {
                    continue
                }

                $path = $process.Path
                if ([string]::IsNullOrWhiteSpace($path)) {
                    if ($process.HasExited) {
                        continue
                    }

                    throw 'AgenTally process identity is inaccessible.'
                }

                $startTicks = $process.StartTime.ToUniversalTime().Ticks
                if ($process.HasExited) {
                    continue
                }
            }
            catch [System.ArgumentException] {
                # The process exited after Get-Process returned its snapshot.
                continue
            }
            catch [System.InvalidOperationException] {
                # The process exited after Get-Process returned its snapshot.
                continue
            }
            catch [System.ComponentModel.Win32Exception] {
                if ($process.HasExited) {
                    continue
                }

                throw 'AgenTally process identity is inaccessible.'
            }
            catch [System.UnauthorizedAccessException] {
                if ($process.HasExited) {
                    continue
                }

                throw 'AgenTally process identity is inaccessible.'
            }

            $expectedPath = Join-Path $paths.InstallRoot (
                $processName + '.exe')
            if ([System.IO.Path]::GetFullPath($path).Equals(
                    [System.IO.Path]::GetFullPath($expectedPath),
                    [System.StringComparison]::OrdinalIgnoreCase)) {
                [pscustomobject]@{
                    Role = if ($processName -eq 'AgenTally.UI') { 'ui' } else { 'core' }
                    ProcessId = $process.Id
                    ProcessStartUtcTicks = $startTicks
                    ExecutablePath = [System.IO.Path]::GetFullPath($path)
                }
            }
        }
        finally {
            $process.Dispose()
        }
    }
}

function Get-AgenTallyStableRunState {
    [CmdletBinding()]
    param([string] $InstallRoot)

    $processes = @(Get-AgenTallyStableProcess -InstallRoot $InstallRoot)
    $roles = @($processes | ForEach-Object { $_.Role })
    if ($roles -contains 'ui') {
        return 'ui'
    }

    if ($roles -contains 'core') {
        return 'background'
    }

    return 'none'
}

function Get-AgenTallyStableShortcutState {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] [string] $ShortcutPath,
        [Parameter(Mandatory)] [string] $ExpectedTarget
    )

    if (-not (Test-Path -LiteralPath $ShortcutPath -PathType Leaf)) {
        return 'missing'
    }
    $shortcutItem = Get-Item -LiteralPath $ShortcutPath -Force -ErrorAction Stop
    if (($shortcutItem.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
        return 'reparse'
    }

    $shell = $null
    $shortcut = $null
    try {
        $shell = New-Object -ComObject WScript.Shell
        $shortcut = $shell.CreateShortcut($ShortcutPath)
        $target = $shortcut.TargetPath
        if (-not [string]::IsNullOrWhiteSpace($target) -and
            [System.IO.Path]::GetFullPath($target).Equals(
                [System.IO.Path]::GetFullPath($ExpectedTarget),
                [System.StringComparison]::OrdinalIgnoreCase)) {
            return 'owned'
        }

        return 'foreign'
    }
    catch {
        return 'foreign'
    }
    finally {
        if ($null -ne $shortcut) {
            [void] [System.Runtime.InteropServices.Marshal]::FinalReleaseComObject($shortcut)
        }
        if ($null -ne $shell) {
            [void] [System.Runtime.InteropServices.Marshal]::FinalReleaseComObject($shell)
        }
    }
}

function Assert-AgenTallyStableInstalledIdentity {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] [string] $InstallRoot,
        [switch] $AllowLegacyMissingMarker
    )

    foreach ($fileName in @(
        'AgenTally.UI.exe',
        'AgenTally.Core.exe')) {
        $candidate = Join-Path $InstallRoot $fileName
        if (-not (Test-Path -LiteralPath $candidate -PathType Leaf)) {
            throw "The registered AgenTally installation is missing $fileName."
        }
        $candidateItem = Get-Item -LiteralPath $candidate -Force -ErrorAction Stop
        if (($candidateItem.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "The registered AgenTally installation identity is a reparse point: $fileName"
        }
    }

    $markerPath = Join-Path $InstallRoot 'AgenTally.InstallIdentity.json'
    if (-not (Test-Path -LiteralPath $markerPath -PathType Leaf)) {
        if ($AllowLegacyMissingMarker) {
            return
        }

        throw 'The registered AgenTally installation is missing AgenTally.InstallIdentity.json.'
    }
    $markerItem = Get-Item -LiteralPath $markerPath -Force -ErrorAction Stop
    if (($markerItem.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw 'The registered AgenTally installation marker is a reparse point.'
    }

    try {
        $identity = Get-Content `
            -LiteralPath $markerPath `
            -Raw | ConvertFrom-Json
    }
    catch {
        throw 'The registered AgenTally installation identity marker is invalid.'
    }

    if ($identity.schemaVersion -ne 1 -or
        $identity.channel -ne 'Stable' -or
        $identity.appId -ne '{A59B3C1C-D735-4D8E-9357-4DF501455822}') {
        throw 'The registered AgenTally installation identity marker does not match Stable.'
    }
}

function Get-AgenTallyStableRegisteredInstallRoot {
    [CmdletBinding()]
    param([Parameter(Mandatory)] $Paths)

    $registeredRoot = $null
    foreach ($registryPath in @(
        $Paths.InnoUninstallRegistryPath,
        $Paths.UninstallRegistryPath,
        $Paths.InstallRecordRegistryPath)) {
        if (-not (Test-Path -LiteralPath $registryPath)) {
            continue
        }

        $registered = Get-AgenTallyStableRegistryValue `
            -RegistryPath $registryPath `
            -ValueName 'InstallLocation'
        if ($registered -isnot [string] -or
            [string]::IsNullOrWhiteSpace($registered) -or
            -not [System.IO.Path]::IsPathRooted($registered) -or
            $registered.StartsWith('\\', [System.StringComparison]::Ordinal)) {
            throw 'An AgenTally installation record has no valid absolute local InstallLocation.'
        }

        $candidate = [System.IO.Path]::GetFullPath($registered).TrimEnd('\', '/')
        if ($null -eq $registeredRoot) {
            $registeredRoot = $candidate
        }
        elseif (-not $registeredRoot.Equals(
                $candidate,
                [System.StringComparison]::OrdinalIgnoreCase)) {
            throw 'AgenTally installation records disagree about the installed directory.'
        }
    }

    return $registeredRoot
}

function Assert-AgenTallyStableInstallRootDisjointFromOwnedData {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] [string] $InstallRoot,
        [Parameter(Mandatory)] [string] $StableRoot
    )

    if ((Test-AgenTallyStablePathWithin `
            -Candidate $InstallRoot `
            -Root $StableRoot) -or
        (Test-AgenTallyStablePathWithin `
            -Candidate $StableRoot `
            -Root $InstallRoot)) {
        throw 'The AgenTally install directory must not contain or be contained by the AgenTally Stable data directory.'
    }
}

function Test-AgenTallyStableLegacyDefaultInstallation {
    [CmdletBinding()]
    param([Parameter(Mandatory)] $Paths)

    if (-not $Paths.InstallRoot.Equals(
            $Paths.DefaultInstallRoot,
            [System.StringComparison]::OrdinalIgnoreCase)) {
        return $false
    }

    foreach ($fileName in @(
        'AgenTally.UI.exe',
        'AgenTally.Core.exe',
        'unins000.exe',
        'unins000.dat',
        'StableMaintenance.ps1',
        'Invoke-AgenTallyStableMaintenance.ps1')) {
        $candidate = Join-Path $Paths.InstallRoot $fileName
        if (-not (Test-Path -LiteralPath $candidate -PathType Leaf)) {
            return $false
        }
        $candidateItem = Get-Item -LiteralPath $candidate -Force -ErrorAction Stop
        if (($candidateItem.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
            return $false
        }
    }

    return $true
}

function Assert-AgenTallyStableInstallTarget {
    [CmdletBinding()]
    param([Parameter(Mandatory)] $Paths)

    Assert-AgenTallyStableInstallRootDisjointFromOwnedData `
        -InstallRoot $Paths.InstallRoot `
        -StableRoot $Paths.StableRoot

    $volumeRoot = [System.IO.Path]::GetPathRoot($Paths.InstallRoot)
    Assert-AgenTallyStableNoReparsePoint `
        -Path $Paths.InstallRoot `
        -Boundary $volumeRoot

    $registeredRoot = Get-AgenTallyStableRegisteredInstallRoot -Paths $Paths
    if ($null -ne $registeredRoot) {
        if (-not $registeredRoot.Equals(
                $Paths.InstallRoot,
                [System.StringComparison]::OrdinalIgnoreCase)) {
            throw 'An existing AgenTally upgrade must use its registered installation directory.'
        }

        Assert-AgenTallyStableTreeContainsNoReparsePoint `
            -Path $Paths.InstallRoot `
            -Boundary $volumeRoot
        Assert-AgenTallyStableInstalledIdentity `
            -InstallRoot $Paths.InstallRoot `
            -AllowLegacyMissingMarker
        return 'upgrade'
    }

    if (Test-Path -LiteralPath $Paths.InstallRoot -PathType Leaf) {
        throw 'The selected AgenTally installation directory is an existing file.'
    }
    if (Test-Path -LiteralPath $Paths.InstallRoot -PathType Container) {
        $existing = @(Get-ChildItem -LiteralPath $Paths.InstallRoot -Force -ErrorAction Stop)
        if ($existing.Count -ne 0) {
            Assert-AgenTallyStableTreeContainsNoReparsePoint `
                -Path $Paths.InstallRoot `
                -Boundary $volumeRoot
            $markerPath = Join-Path $Paths.InstallRoot 'AgenTally.InstallIdentity.json'
            if (Test-Path -LiteralPath $markerPath -PathType Leaf) {
                Assert-AgenTallyStableInstalledIdentity -InstallRoot $Paths.InstallRoot
                return 'upgrade'
            }
            if (Test-AgenTallyStableLegacyDefaultInstallation -Paths $Paths) {
                Assert-AgenTallyStableInstalledIdentity `
                    -InstallRoot $Paths.InstallRoot `
                    -AllowLegacyMissingMarker
                return 'upgrade'
            }

            throw 'A first AgenTally installation requires a new or empty directory.'
        }
    }

    return 'first'
}

function Assert-AgenTallyStableTreeContainsNoReparsePoint {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] [string] $Path,
        [Parameter(Mandatory)] [string] $Boundary
    )

    Assert-AgenTallyStableNoReparsePoint -Path $Path -Boundary $Boundary
    if (-not (Test-Path -LiteralPath $Path)) {
        return
    }

    foreach ($item in @(Get-ChildItem -LiteralPath $Path -Force -Recurse -ErrorAction Stop)) {
        if (($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "AgenTally cleanup refused the reparse point: $($item.FullName)"
        }
    }
}

function Remove-AgenTallyStableOwnedTree {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] [string] $Path,
        [Parameter(Mandatory)] [string] $Boundary
    )

    if (-not (Test-Path -LiteralPath $Path)) {
        return
    }
    if (-not (Test-Path -LiteralPath $Path -PathType Container)) {
        throw "The AgenTally cleanup target is not a directory: $Path"
    }

    Assert-AgenTallyStableTreeContainsNoReparsePoint -Path $Path -Boundary $Boundary
    Remove-Item -LiteralPath $Path -Recurse -Force -ErrorAction Stop
    if (Test-Path -LiteralPath $Path) {
        throw "AgenTally cleanup left a residual directory: $Path"
    }
}

function Remove-AgenTallyStableOwnedData {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] $Paths,
        [switch] $RemoveData
    )

    $stableVolumeRoot = [System.IO.Path]::GetPathRoot($Paths.StableRoot)
    $targets = @($Paths.RuntimeRoot, $Paths.LogRoot, $Paths.TempRoot)
    if ($RemoveData) {
        $targets += $Paths.DataRoot
    }

    foreach ($target in $targets) {
        if (-not (Test-AgenTallyStablePathWithin `
                -Candidate $target `
                -Root $Paths.StableRoot)) {
            throw 'A Stable cleanup target escaped the explicit allow-list.'
        }

        Remove-AgenTallyStableOwnedTree `
            -Path $target `
            -Boundary $stableVolumeRoot
    }

    if (-not (Test-Path -LiteralPath $Paths.StableRoot)) {
        return
    }

    Assert-AgenTallyStableTreeContainsNoReparsePoint `
        -Path $Paths.StableRoot `
        -Boundary $stableVolumeRoot
    $remaining = @(Get-ChildItem -LiteralPath $Paths.StableRoot -Force -ErrorAction Stop)
    if ($RemoveData -and $remaining.Count -ne 0) {
        $residuals = ($remaining | Select-Object -First 5 -ExpandProperty FullName) -join '; '
        throw "AgenTally data cleanup found unexpected residual content: $residuals"
    }

    if ($remaining.Count -eq 0) {
        Remove-Item -LiteralPath $Paths.StableRoot -Force -ErrorAction Stop
        if (Test-Path -LiteralPath $Paths.StableRoot) {
            throw "AgenTally cleanup left a residual Stable directory: $($Paths.StableRoot)"
        }
    }
}

function Test-AgenTallyStableProcessAlive {
    [CmdletBinding()]
    param([Parameter(Mandatory)] $Identity)

    try {
        $process = [System.Diagnostics.Process]::GetProcessById($Identity.ProcessId)
    }
    catch [System.ArgumentException] {
        return $false
    }

    try {
        # The executable path was verified when this identity was captured.
        # PID plus start time uniquely tracks that same process while it exits;
        # re-reading MainModule here creates a race where an already-exited
        # process can expose a null module and be misreported as inaccessible.
        return $process.StartTime.ToUniversalTime().Ticks -eq
            $Identity.ProcessStartUtcTicks
    }
    catch [System.InvalidOperationException] {
        return $false
    }
    catch [System.ComponentModel.Win32Exception] {
        throw 'Stable AgenTally identity became inaccessible while waiting.'
    }
    catch [System.UnauthorizedAccessException] {
        throw 'Stable AgenTally identity became inaccessible while waiting.'
    }
    finally {
        $process.Dispose()
    }
}

function Stop-AgenTallyStableGracefully {
    [CmdletBinding()]
    param(
        [string] $InstallRoot,
        [ValidateRange(1, 120)] [int] $TimeoutSeconds = 20
    )

    $targets = @(Get-AgenTallyStableProcess -InstallRoot $InstallRoot)
    if ($targets.Count -eq 0) {
        return
    }

    $paths = Get-AgenTallyStablePaths -InstallRoot $InstallRoot
    Assert-AgenTallyStableNoReparsePoint `
        -Path $paths.RuntimeRoot `
        -Boundary (Split-Path -Parent $paths.StableRoot)
    New-Item -ItemType Directory -Force -Path $paths.RuntimeRoot | Out-Null
    $request = [ordered]@{
        profileId = $paths.ProfileId
        requestedAtUtcTicks = [DateTime]::UtcNow.Ticks
    }
    $temporaryPath = $paths.ShutdownRequestPath + '.' +
        [Guid]::NewGuid().ToString('N') + '.tmp'
    $markerWritten = $false
    try {
        Write-AgenTallyStableUtf8JsonFile `
            -Path $temporaryPath `
            -Value $request
        Move-Item `
            -LiteralPath $temporaryPath `
            -Destination $paths.ShutdownRequestPath `
            -Force
        $markerWritten = $true
    }
    finally {
        Remove-Item -LiteralPath $temporaryPath -Force -ErrorAction SilentlyContinue
    }

    $signal = $null
    $semaphoreBroadcast = $false
    if ([System.Threading.Semaphore]::TryOpenExisting(
            $paths.ShutdownEventName,
            [ref] $signal)) {
        try {
            try {
                $signal.Release(64) | Out-Null
                $semaphoreBroadcast = $true
            }
            catch [System.Threading.SemaphoreFullException] {
                $semaphoreBroadcast = $true
            }
        }
        finally {
            $signal.Dispose()
        }
    }

    if (-not $markerWritten -and -not $semaphoreBroadcast) {
        throw 'Stable AgenTally has no compatible graceful shutdown transport.'
    }

    $deadline = [DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds)
    do {
        $remaining = @($targets | Where-Object {
            Test-AgenTallyStableProcessAlive -Identity $_
        })
        if ($remaining.Count -eq 0) {
            return
        }

        Start-Sleep -Milliseconds 100
    } while ([DateTimeOffset]::UtcNow -lt $deadline)

    throw 'Stable AgenTally did not stop gracefully; no forced termination was attempted.'
}
