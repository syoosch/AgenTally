#requires -Version 5.1

[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string] $Version,

    [ValidateSet('Release')]
    [string] $Configuration = 'Release',

    [string] $InnoCompilerPath,

    [switch] $AllowDirtyWorktree
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '..\packaging\stable\StableMaintenance.ps1')

function Get-AgenTallyRepositoryRoot {
    [CmdletBinding()]
    param()

    $root = [System.IO.Path]::GetFullPath(
        (Join-Path -Path $PSScriptRoot -ChildPath '..'))
    foreach ($marker in @('AgenTally.sln', '.agentally-root')) {
        if (-not (Test-Path -LiteralPath (Join-Path $root $marker) -PathType Leaf)) {
            throw 'The AgenTally repository root cannot be verified.'
        }
    }

    $current = Get-Item -LiteralPath $root
    while ($null -ne $current) {
        if (($current.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw 'The AgenTally repository path cannot traverse a reparse point.'
        }
        $current = $current.Parent
    }

    return $root
}

function Test-AgenTallyPathWithin {
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

function Invoke-CheckedCommand {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] [string] $FilePath,
        [Parameter(Mandatory)] [string[]] $Arguments,
        [Parameter(Mandatory)] [string] $FailureMessage
    )

    & $FilePath @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "$FailureMessage Exit code: $LASTEXITCODE."
    }
}

function Resolve-InnoCompiler {
    [CmdletBinding()]
    param([string] $RequestedPath)

    $command = Get-Command 'ISCC.exe' -ErrorAction SilentlyContinue
    $candidates = @(
        $RequestedPath,
        $(if ($null -ne $command) { $command.Source }),
        $(if ($env:ProgramFiles) {
            Join-Path $env:ProgramFiles 'Inno Setup 7\ISCC.exe'
        }),
        $(if ($env:LOCALAPPDATA) {
            Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 7\ISCC.exe'
        }),
        $(if ($env:ProgramFiles) {
            Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe'
        }),
        $(if (${env:ProgramFiles(x86)}) {
            Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'
        }),
        $(if ($env:LOCALAPPDATA) {
            Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'
        })
    )

    foreach ($candidate in $candidates) {
        if (-not [string]::IsNullOrWhiteSpace($candidate) -and
            (Test-Path -LiteralPath $candidate -PathType Leaf)) {
            return [System.IO.Path]::GetFullPath($candidate)
        }
    }

    throw (
        'Inno Setup compiler was not found. Install the official current-user ' +
        'tool or pass -InnoCompilerPath with the full path to ISCC.exe.')
}

function Merge-PublishOutput {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] [string] $Source,
        [Parameter(Mandatory)] [string] $Destination
    )

    foreach ($item in @(Get-ChildItem -LiteralPath $Source -Recurse -Force)) {
        if (($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw 'A Stable publish output contains a reparse point.'
        }
        if ($item.PSIsContainer) {
            continue
        }

        $relativePath = [System.IO.Path]::GetRelativePath($Source, $item.FullName)
        $target = Join-Path $Destination $relativePath
        New-Item -ItemType Directory -Force -Path (Split-Path -Parent $target) | Out-Null
        if (-not (Test-Path -LiteralPath $target -PathType Leaf)) {
            Copy-Item -LiteralPath $item.FullName -Destination $target
            continue
        }

        $sourceHash = (Get-FileHash -LiteralPath $item.FullName -Algorithm SHA256).Hash
        $targetHash = (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash
        if ($sourceHash -ne $targetHash) {
            throw "UI/Core publish collision is not byte-identical: $relativePath"
        }
    }
}

$repositoryRoot = Get-AgenTallyRepositoryRoot
$dirty = @(& git -C $repositoryRoot status --porcelain=v1 --untracked-files=all)
if ($LASTEXITCODE -ne 0) {
    throw 'Stable package Git status cannot be determined.'
}
if ($dirty.Count -ne 0 -and -not $AllowDirtyWorktree) {
    throw (
        'Stable release publication requires a clean, versioned Git worktree. ' +
        'Use -AllowDirtyWorktree only to compile an explicitly marked local validation package.')
}

$commit = (& git -C $repositoryRoot rev-parse --short=12 HEAD).Trim()
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($commit)) {
    throw 'Stable package Git identity cannot be determined.'
}

$compiler = Resolve-InnoCompiler -RequestedPath $InnoCompilerPath
$compilerHelp = (& $compiler '/?' 2>&1 | Out-String)
if ($compilerHelp -notmatch '(?m)^Inno Setup (?<major>\d+) Command-Line Compiler' -or
    [int]$Matches.major -lt 6) {
    throw 'AgenTally Stable packaging requires Inno Setup 6 or newer.'
}
$compilerMajorVersion = [int]$Matches.major
$compilerSignature = Get-AuthenticodeSignature -LiteralPath $compiler
if ($compilerSignature.Status -ne [System.Management.Automation.SignatureStatus]::Valid -or
    $null -eq $compilerSignature.SignerCertificate -or
    -not $compilerSignature.SignerCertificate.Subject.StartsWith(
        'CN=Pyrsys B.V.',
        [System.StringComparison]::Ordinal)) {
    throw 'The Inno Setup compiler is not validly signed by Pyrsys B.V.'
}

$defaultStablePaths = Get-AgenTallyStablePaths
$registeredInstallRoot = Get-AgenTallyStableRegisteredInstallRoot `
    -Paths $defaultStablePaths
$stableInstallRoot = if ([string]::IsNullOrWhiteSpace($registeredInstallRoot)) {
    $defaultStablePaths.DefaultInstallRoot
}
else {
    $registeredInstallRoot
}
$stableProcesses = @(Get-AgenTallyStableProcess -InstallRoot $stableInstallRoot)
$namedProcesses = @(Get-Process `
    -Name 'AgenTally.Core', 'AgenTally.UI' `
    -ErrorAction SilentlyContinue)
try {
    $stableProcessIds = [System.Collections.Generic.HashSet[int]]::new()
    foreach ($stableProcess in $stableProcesses) {
        [void] $stableProcessIds.Add([int] $stableProcess.ProcessId)
    }
    $nonStableProcesses = @($namedProcesses | Where-Object {
        -not $stableProcessIds.Contains([int] $_.Id)
    })
    if ($nonStableProcesses.Count -ne 0) {
        throw (
            'A source-build or unverified AgenTally process is running. ' +
            'Exit it through its owning workflow before Stable publication.')
    }
}
finally {
    foreach ($namedProcess in $namedProcesses) {
        $namedProcess.Dispose()
    }
}

Stop-AgenTallyStableGracefully `
    -InstallRoot $stableInstallRoot `
    -TimeoutSeconds 20
if (@(Get-AgenTallyStableProcess -InstallRoot $stableInstallRoot).Count -ne 0) {
    throw 'Stable AgenTally processes remain after publication preflight.'
}

$sourceGate = Join-Path $repositoryRoot 'scripts\Test-AgenTallyPrepackageSecurity.ps1'
if ($AllowDirtyWorktree) {
    & $sourceGate -AllowDirtyWorktree
}
else {
    & $sourceGate
}
if ($LASTEXITCODE -ne 0) {
    throw 'The pre-package source security gate failed.'
}

$packageRoot = Join-Path $repositoryRoot 'artifacts\stable-package'
$stagingRoot = Join-Path $packageRoot ('staging-' + [Guid]::NewGuid().ToString('N'))
$uiPublish = Join-Path $stagingRoot 'ui'
$corePublish = Join-Path $stagingRoot 'core'
$payloadRoot = Join-Path $stagingRoot 'payload'
$compilerOutput = Join-Path $stagingRoot 'installer'
$ridLockRoot = Join-Path $stagingRoot 'rid-locks'
$artifactsRoot = Join-Path $repositoryRoot 'artifacts'
foreach ($path in @(
    $packageRoot,
    $stagingRoot,
    $uiPublish,
    $corePublish,
    $payloadRoot,
    $compilerOutput,
    $ridLockRoot)) {
    if (-not (Test-AgenTallyPathWithin -Candidate $path -Root $artifactsRoot)) {
        throw 'Stable package staging target escaped the repository artifacts root.'
    }
}

$oldDotnetHome = $env:DOTNET_CLI_HOME
$oldTemp = $env:TEMP
$oldTmp = $env:TMP
$oldTelemetry = $env:DOTNET_CLI_TELEMETRY_OPTOUT
$oldNoLogo = $env:DOTNET_NOLOGO
$oldFirstTime = $env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE
try {
    New-Item -ItemType Directory -Force -Path @(
        $packageRoot,
        $uiPublish,
        $corePublish,
        $payloadRoot,
        $compilerOutput,
        $ridLockRoot) | Out-Null
    $env:DOTNET_CLI_HOME = Join-Path $packageRoot 'tooling\dotnet-home'
    $env:TEMP = Join-Path $packageRoot 'temp'
    $env:TMP = $env:TEMP
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    $env:DOTNET_NOLOGO = '1'
    $env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
    New-Item -ItemType Directory -Force -Path $env:DOTNET_CLI_HOME, $env:TEMP | Out-Null
    Invoke-CheckedCommand -FilePath 'dotnet' -Arguments @('--info') `
        -FailureMessage 'The isolated .NET packaging environment initialization failed.'

    $uiProject = Join-Path $repositoryRoot 'src\AgenTally.UI\AgenTally.UI.csproj'
    $coreProject = Join-Path $repositoryRoot 'src\AgenTally.Core\AgenTally.Core.csproj'
    $domainProject = Join-Path $repositoryRoot 'src\AgenTally.Domain\AgenTally.Domain.csproj'
    $storageProject = Join-Path $repositoryRoot 'src\AgenTally.Storage\AgenTally.Storage.csproj'
    foreach ($project in @(
        $domainProject,
        $storageProject,
        $uiProject,
        $coreProject)) {
        $projectName = [System.IO.Path]::GetFileNameWithoutExtension($project)
        $ridLockFileProperty = '-p:NuGetLockFilePath={0}' -f (
            Join-Path $ridLockRoot "$projectName.packages.lock.json")
        Invoke-CheckedCommand -FilePath 'dotnet' -Arguments @(
            'restore', $project,
            '--runtime', 'win-x64',
            '--no-dependencies',
            '--force-evaluate',
            $ridLockFileProperty,
            '-p:NuGetAudit=true',
            '-p:NuGetAuditMode=all',
            '-p:NuGetAuditLevel=low',
            '-p:TreatWarningsAsErrors=true'
        ) -FailureMessage 'Audited win-x64 lock generation failed.'
        Invoke-CheckedCommand -FilePath 'dotnet' -Arguments @(
            'restore', $project,
            '--runtime', 'win-x64',
            '--no-dependencies',
            '--locked-mode',
            $ridLockFileProperty,
            '-p:NuGetAudit=true',
            '-p:NuGetAuditMode=all',
            '-p:NuGetAuditLevel=low',
            '-p:TreatWarningsAsErrors=true'
        ) -FailureMessage 'Locked win-x64 restore replay failed.'
    }

    $commonPublishArguments = @(
        '-c', $Configuration,
        '--runtime', 'win-x64',
        '--self-contained', 'true',
        '--no-restore',
        '-p:AgenTallyChannel=Stable',
        "-p:Version=$Version",
        "-p:InformationalVersion=$Version+$commit",
        '-p:DebugSymbols=false',
        '-p:DebugType=None',
        '-p:PublishSingleFile=true',
        '-p:IncludeNativeLibrariesForSelfExtract=false'
    )
    Invoke-CheckedCommand -FilePath 'dotnet' -Arguments (@(
        'publish', $uiProject,
        '--output', $uiPublish
    ) + $commonPublishArguments) -FailureMessage 'Stable UI publish failed.'
    Invoke-CheckedCommand -FilePath 'dotnet' -Arguments (@(
        'publish', $coreProject,
        '--output', $corePublish
    ) + $commonPublishArguments) -FailureMessage 'Stable Core publish failed.'

    Merge-PublishOutput -Source $uiPublish -Destination $payloadRoot
    Merge-PublishOutput -Source $corePublish -Destination $payloadRoot
    Get-ChildItem -LiteralPath $payloadRoot -Recurse -File |
        Where-Object { $_.Extension -in @('.pdb', '.xml') } |
        Remove-Item -Force

    foreach ($maintenanceFile in @(
        'AgenTally.InstallIdentity.json',
        'StableMaintenance.ps1',
        'Invoke-AgenTallyStableMaintenance.ps1')) {
        Copy-Item `
            -LiteralPath (Join-Path $repositoryRoot "packaging\stable\$maintenanceFile") `
            -Destination $payloadRoot
    }

    foreach ($requiredFile in @(
        'AgenTally.UI.exe',
        'AgenTally.Core.exe',
        'AgenTally.InstallIdentity.json',
        'StableMaintenance.ps1',
        'Invoke-AgenTallyStableMaintenance.ps1')) {
        if (-not (Test-Path -LiteralPath (Join-Path $payloadRoot $requiredFile) -PathType Leaf)) {
            throw "Stable payload is missing $requiredFile."
        }
    }

    $foreignNativeAssets = @(Get-ChildItem -LiteralPath $payloadRoot -Recurse -File |
        Where-Object {
            $_.FullName -match '[\\/]runtimes[\\/]' -and
            $_.FullName -notmatch '[\\/]runtimes[\\/]win-x64[\\/]'
        })
    if ($foreignNativeAssets.Count -ne 0) {
        throw 'Stable payload contains native assets outside win-x64.'
    }
    $sqliteNativeAssets = @(Get-ChildItem -LiteralPath $payloadRoot -Recurse -File |
        Where-Object {
            $_.Name -in @(
                'e_sqlite3.dll',
                'libe_sqlite3.so',
                'libe_sqlite3.dylib',
                'e_sqlite3.a')
        })
    if ($sqliteNativeAssets.Count -ne 1 -or
        $sqliteNativeAssets[0].Name -ne 'e_sqlite3.dll') {
        throw 'Stable payload must contain exactly one win-x64 SQLite native library.'
    }

    $innoScript = Join-Path $repositoryRoot 'packaging\stable\AgenTally.iss'
    Invoke-CheckedCommand -FilePath $compiler -Arguments @(
        '/Qp',
        "/DMyAppVersion=$Version",
        "/DMyAppCommit=$commit",
        "/DSourceDir=$payloadRoot",
        "/DOutputDir=$compilerOutput",
        $innoScript
    ) -FailureMessage 'Inno Setup compilation failed.'

    $compiledInstaller = Join-Path $compilerOutput "AgenTally-$Version-win-x64-setup.exe"
    if (-not (Test-Path -LiteralPath $compiledInstaller -PathType Leaf)) {
        throw 'Inno Setup did not produce the expected single EXE installer.'
    }

    $outputSuffix = if ($AllowDirtyWorktree) { '-UNCOMMITTED' } else { '' }
    $outputBaseName = "AgenTally-$Version-win-x64-setup$outputSuffix"
    $installerPath = Join-Path $packageRoot ($outputBaseName + '.exe')
    $reportPath = Join-Path $packageRoot ($outputBaseName + '.json')
    $hashPath = Join-Path $packageRoot ($outputBaseName + '.sha256')
    foreach ($outputPath in @($installerPath, $reportPath, $hashPath)) {
        if (-not (Test-AgenTallyPathWithin -Candidate $outputPath -Root $packageRoot)) {
            throw 'Stable package output escaped the approved package root.'
        }
        Remove-Item -LiteralPath $outputPath -Force -ErrorAction SilentlyContinue
    }
    Copy-Item -LiteralPath $compiledInstaller -Destination $installerPath

    $installerHash = (Get-FileHash -LiteralPath $installerPath -Algorithm SHA256).Hash
    $signature = Get-AuthenticodeSignature -LiteralPath $installerPath
    if ($signature.Status -ne [System.Management.Automation.SignatureStatus]::NotSigned) {
        throw 'The v0.1.0 packaging boundary requires an explicitly unsigned installer.'
    }
    $report = [ordered]@{
        schemaVersion = 1
        product = 'AgenTally'
        channel = 'Stable'
        version = $Version
        commit = $commit
        releaseCandidate = -not $AllowDirtyWorktree
        architecture = 'win-x64'
        selfContained = $true
        uiAndCoreSingleFile = $true
        installer = [System.IO.Path]::GetFileName($installerPath)
        sha256 = $installerHash
        bytes = (Get-Item -LiteralPath $installerPath).Length
        signed = $false
        innoSetupCompilerMajorVersion = $compilerMajorVersion
        innoSetupCompilerSigner = $compilerSignature.SignerCertificate.Subject
    }
    $report | ConvertTo-Json -Depth 4 |
        Set-Content -LiteralPath $reportPath -Encoding UTF8
    "$installerHash  $([System.IO.Path]::GetFileName($installerPath))" |
        Set-Content -LiteralPath $hashPath -Encoding ASCII

    Write-Host "Stable Inno Setup package created at $installerPath"
    if ($AllowDirtyWorktree) {
        Write-Warning 'This package is marked UNCOMMITTED and is not a release candidate.'
    }
}
finally {
    $env:DOTNET_CLI_HOME = $oldDotnetHome
    $env:TEMP = $oldTemp
    $env:TMP = $oldTmp
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = $oldTelemetry
    $env:DOTNET_NOLOGO = $oldNoLogo
    $env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = $oldFirstTime
    if (Test-Path -LiteralPath $stagingRoot) {
        Remove-Item -LiteralPath $stagingRoot -Recurse -Force
    }
}
