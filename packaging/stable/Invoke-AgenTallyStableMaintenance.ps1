#requires -Version 5.1

[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet(
        'InspectInstall',
        'PrepareInstall',
        'InspectUninstall',
        'PrepareUninstall')]
    [string] $Mode,

    [Parameter(Mandatory)]
    [string] $InstallRoot,

    [string] $StatePath,

    [string] $ResultPath,

    [switch] $DesktopShortcutRequested,

    [switch] $RemoveData,

    [ValidateRange(1, 120)]
    [int] $StopTimeoutSeconds = 20
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'StableMaintenance.ps1')

function Write-AgenTallyMaintenanceState {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] [string] $Path,
        [Parameter(Mandatory)] [hashtable] $Values
    )

    if ([string]::IsNullOrWhiteSpace($Path)) {
        throw 'A maintenance inspection requires a state output path.'
    }

    $lines = foreach ($key in $Values.Keys | Sort-Object) {
        '{0}={1}' -f $key, $Values[$key]
    }
    Set-Content -LiteralPath $Path -Value $lines -Encoding Ascii -ErrorAction Stop
}

function Assert-AgenTallyMaintenancePaths {
    [CmdletBinding()]
    param([Parameter(Mandatory)] $Paths)

    $installVolumeRoot = [System.IO.Path]::GetPathRoot($Paths.InstallRoot)
    Assert-AgenTallyStableNoReparsePoint `
        -Path $Paths.InstallRoot `
        -Boundary $installVolumeRoot

    $stableVolumeRoot = [System.IO.Path]::GetPathRoot($Paths.StableRoot)
    foreach ($target in @(
        $Paths.RuntimeRoot,
        $Paths.LogRoot,
        $Paths.TempRoot,
        $Paths.DataRoot)) {
        if (-not (Test-AgenTallyStablePathWithin `
                -Candidate $target `
                -Root $Paths.StableRoot)) {
            throw 'A Stable maintenance target escaped the explicit allow-list.'
        }

        Assert-AgenTallyStableNoReparsePoint `
            -Path $target `
            -Boundary $stableVolumeRoot
    }
}

function Get-AgenTallyShortcutStates {
    [CmdletBinding()]
    param([Parameter(Mandatory)] $Paths)

    $expectedTarget = Join-Path $Paths.InstallRoot 'AgenTally.UI.exe'
    return [pscustomobject]@{
        Desktop = Get-AgenTallyStableShortcutState `
            -ShortcutPath $Paths.DesktopShortcutPath `
            -ExpectedTarget $expectedTarget
        StartMenu = Get-AgenTallyStableShortcutState `
            -ShortcutPath $Paths.InnoStartMenuShortcutPath `
            -ExpectedTarget $expectedTarget
    }
}

function Assert-AgenTallyInstallShortcutStates {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] $Shortcuts,
        [switch] $DesktopRequested
    )

    if ($DesktopRequested -and
        $Shortcuts.Desktop -in @('foreign', 'reparse')) {
        throw 'The desktop already contains a foreign AgenTally shortcut; uncheck the desktop shortcut task or move that shortcut first.'
    }
    if ($Shortcuts.StartMenu -in @('foreign', 'reparse')) {
        throw 'The Start Menu already contains a foreign AgenTally shortcut; move that shortcut before installing.'
    }
}

function Invoke-AgenTallyStableMaintenance {
    [CmdletBinding()]
    param()

    $paths = Get-AgenTallyStablePaths -InstallRoot $InstallRoot
    Assert-AgenTallyMaintenancePaths -Paths $paths

    if ($Mode -eq 'InspectInstall') {
        $installMode = Assert-AgenTallyStableInstallTarget -Paths $paths
        $shortcuts = Get-AgenTallyShortcutStates -Paths $paths
        Assert-AgenTallyInstallShortcutStates `
            -Shortcuts $shortcuts `
            -DesktopRequested:$DesktopShortcutRequested

        Write-AgenTallyMaintenanceState -Path $StatePath -Values @{
            desktopShortcut = $shortcuts.Desktop
            installMode = $installMode
            runState = Get-AgenTallyStableRunState -InstallRoot $paths.InstallRoot
            startMenuShortcut = $shortcuts.StartMenu
        }
        return
    }

    if ($Mode -eq 'PrepareInstall') {
        [void] (Assert-AgenTallyStableInstallTarget -Paths $paths)
        $shortcuts = Get-AgenTallyShortcutStates -Paths $paths
        Assert-AgenTallyInstallShortcutStates `
            -Shortcuts $shortcuts `
            -DesktopRequested:$DesktopShortcutRequested
        Stop-AgenTallyStableGracefully `
            -InstallRoot $paths.InstallRoot `
            -TimeoutSeconds $StopTimeoutSeconds
        Write-Host 'AgenTally Stable is ready for installation or upgrade.'
        return
    }

    Assert-AgenTallyStableInstalledIdentity -InstallRoot $paths.InstallRoot
    Assert-AgenTallyStableTreeContainsNoReparsePoint `
        -Path $paths.InstallRoot `
        -Boundary ([System.IO.Path]::GetPathRoot($paths.InstallRoot))
    if ($Mode -eq 'InspectUninstall') {
        $shortcuts = Get-AgenTallyShortcutStates -Paths $paths
        if ($shortcuts.Desktop -eq 'reparse' -or $shortcuts.StartMenu -eq 'reparse') {
            throw 'A same-name AgenTally shortcut is a reparse point; uninstall refused to follow or delete it.'
        }
        Write-AgenTallyMaintenanceState -Path $StatePath -Values @{
            desktopShortcut = $shortcuts.Desktop
            runState = Get-AgenTallyStableRunState -InstallRoot $paths.InstallRoot
            startMenuShortcut = $shortcuts.StartMenu
        }
        return
    }

    Stop-AgenTallyStableGracefully `
        -InstallRoot $paths.InstallRoot `
        -TimeoutSeconds $StopTimeoutSeconds

    $runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
    $runValueName = 'AgenTally'
    $expectedRunCommand = '"{0}" --background' -f (
        Join-Path $paths.InstallRoot 'AgenTally.UI.exe')
    $actualRunCommand = Get-AgenTallyStableRegistryValue `
        -RegistryPath $runKey `
        -ValueName $runValueName
    if ($actualRunCommand -is [string] -and
        $actualRunCommand.Equals(
            $expectedRunCommand,
            [System.StringComparison]::Ordinal)) {
        Remove-ItemProperty `
            -LiteralPath $runKey `
            -Name $runValueName `
            -ErrorAction Stop
    }

    Remove-AgenTallyStableOwnedData -Paths $paths -RemoveData:$RemoveData
    Write-Host $(if ($RemoveData) {
        'AgenTally Stable runtime and all application-owned data are ready for removal.'
    } else {
        'AgenTally Stable runtime is ready for removal; application data was kept.'
    })
}

try {
    Invoke-AgenTallyStableMaintenance
}
catch {
    if (-not [string]::IsNullOrWhiteSpace($ResultPath)) {
        try {
            Set-Content `
                -LiteralPath $ResultPath `
                -Value $_.Exception.Message `
                -Encoding UTF8 `
                -ErrorAction Stop
        }
        catch {
        }
    }

    Write-Error -ErrorRecord $_
    exit 1
}
