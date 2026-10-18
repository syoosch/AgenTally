#requires -Version 5.1

[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string] $Version,

    [Parameter(Mandatory)]
    [ValidatePattern('^[0-9A-Fa-f]{7,40}$')]
    [string] $ExpectedCommit,

    [ValidateSet('Unsigned', 'Signed')]
    [string] $SignaturePolicy = 'Unsigned'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Get-AgenTallyPublicRepositoryRoot {
    [CmdletBinding()]
    param()

    $root = [System.IO.Path]::GetFullPath(
        (Join-Path -Path $PSScriptRoot -ChildPath '..'))
    foreach ($marker in @('AgenTally.sln', '.agentally-root', '.agentally-public')) {
        if (-not (Test-Path -LiteralPath (Join-Path $root $marker) -PathType Leaf)) {
            throw 'The AgenTally public repository root cannot be verified.'
        }
    }

    $current = Get-Item -LiteralPath $root
    while ($null -ne $current) {
        if (($current.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw 'The AgenTally public repository path cannot traverse a reparse point.'
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

function Invoke-AgenTallyGitText {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] [string] $RepositoryRoot,
        [Parameter(Mandatory)] [string[]] $Arguments
    )

    $output = @(& git -C $RepositoryRoot @Arguments 2>&1)
    if ($LASTEXITCODE -ne 0) {
        throw "Git command failed: git $($Arguments -join ' ')"
    }

    return (($output | ForEach-Object { [string] $_ }) -join "`n").Trim()
}

function Assert-AgenTallyReportProperty {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] [object] $Report,
        [Parameter(Mandatory)] [string] $Name
    )

    if ($Report.PSObject.Properties.Name -notcontains $Name) {
        throw "The Stable package report is missing required property '$Name'."
    }
}

function Get-AgenTallySha256 {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] [string] $Path
    )

    $stream = [System.IO.File]::OpenRead($Path)
    $algorithm = [System.Security.Cryptography.SHA256]::Create()
    try {
        return ([System.BitConverter]::ToString(
                $algorithm.ComputeHash($stream))).Replace('-', '')
    }
    finally {
        $algorithm.Dispose()
        $stream.Dispose()
    }
}

$repositoryRoot = Get-AgenTallyPublicRepositoryRoot
$branch = Invoke-AgenTallyGitText -RepositoryRoot $repositoryRoot -Arguments @(
    'branch', '--show-current')
if (-not $branch.Equals('main', [System.StringComparison]::Ordinal)) {
    throw 'Formal Stable artifact verification requires public branch main.'
}

$origin = Invoke-AgenTallyGitText -RepositoryRoot $repositoryRoot -Arguments @(
    'remote', 'get-url', 'origin')
$normalizedOrigin = $origin.Trim().TrimEnd('/') -replace '\.git$', ''
if (-not $normalizedOrigin.Equals(
        'https://github.com/syoosch/AgenTally',
        [System.StringComparison]::OrdinalIgnoreCase)) {
    throw 'Formal Stable artifact verification requires the official public origin.'
}

$dirty = Invoke-AgenTallyGitText -RepositoryRoot $repositoryRoot -Arguments @(
    'status', '--porcelain=v1', '--untracked-files=all')
if (-not [string]::IsNullOrWhiteSpace($dirty)) {
    throw 'Formal Stable artifact verification requires a clean public worktree.'
}

$resolvedExpectedCommit = Invoke-AgenTallyGitText `
    -RepositoryRoot $repositoryRoot `
    -Arguments @('rev-parse', '--verify', "$ExpectedCommit^{commit}")
$currentCommit = Invoke-AgenTallyGitText `
    -RepositoryRoot $repositoryRoot `
    -Arguments @('rev-parse', 'HEAD')
if (-not $currentCommit.Equals(
        $resolvedExpectedCommit,
        [System.StringComparison]::OrdinalIgnoreCase)) {
    throw 'The public HEAD does not match ExpectedCommit.'
}
$currentShortCommit = Invoke-AgenTallyGitText `
    -RepositoryRoot $repositoryRoot `
    -Arguments @('rev-parse', '--short=12', 'HEAD')

$artifactsRoot = [System.IO.Path]::GetFullPath(
    (Join-Path $repositoryRoot 'artifacts'))
$packageRoot = [System.IO.Path]::GetFullPath(
    (Join-Path $artifactsRoot 'stable-package'))
if (-not (Test-AgenTallyPathWithin -Candidate $packageRoot -Root $artifactsRoot) -or
    -not (Test-Path -LiteralPath $packageRoot -PathType Container)) {
    throw 'The Stable package directory is missing or escaped the artifacts root.'
}

$outputBaseName = "AgenTally-$Version-win-x64-setup"
$installerName = "$outputBaseName.exe"
$installerPath = Join-Path $packageRoot $installerName
$reportPath = Join-Path $packageRoot "$outputBaseName.json"
$hashPath = Join-Path $packageRoot "$outputBaseName.sha256"
foreach ($path in @($installerPath, $reportPath, $hashPath)) {
    if (-not (Test-AgenTallyPathWithin -Candidate $path -Root $packageRoot) -or
        -not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "The required Stable package file is missing: $([System.IO.Path]::GetFileName($path))"
    }
    $item = Get-Item -LiteralPath $path
    if (($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw 'A Stable package file cannot be a reparse point.'
    }
}

$expectedArtifactNames = @(
    $installerName,
    "$outputBaseName.json",
    "$outputBaseName.sha256")
$unexpectedVersionArtifacts = @(Get-ChildItem -LiteralPath $packageRoot -File -Force |
    Where-Object {
        $_.Name.StartsWith(
            $outputBaseName,
            [System.StringComparison]::OrdinalIgnoreCase) -and
        $expectedArtifactNames -notcontains $_.Name
    })
if ($unexpectedVersionArtifacts.Count -ne 0) {
    throw 'Unexpected same-version Stable package files remain beside the formal artifact.'
}

$stagingDirectories = @(Get-ChildItem -LiteralPath $packageRoot -Directory -Force |
    Where-Object { $_.Name -like 'staging-*' })
if ($stagingDirectories.Count -ne 0) {
    throw 'Stable package staging directories remain after publication.'
}

try {
    $report = Get-Content -LiteralPath $reportPath -Raw | ConvertFrom-Json
}
catch {
    throw 'The Stable package report is not valid JSON.'
}
if ($null -eq $report) {
    throw 'The Stable package report is empty.'
}

$requiredProperties = @(
    'schemaVersion',
    'product',
    'channel',
    'version',
    'commit',
    'releaseCandidate',
    'architecture',
    'selfContained',
    'uiAndCoreSingleFile',
    'installer',
    'sha256',
    'bytes',
    'signed',
    'innoSetupCompilerMajorVersion',
    'innoSetupCompilerSigner')
foreach ($property in $requiredProperties) {
    Assert-AgenTallyReportProperty -Report $report -Name $property
}

if ([int] $report.schemaVersion -ne 1 -or
    [string] $report.product -cne 'AgenTally' -or
    [string] $report.channel -cne 'Stable' -or
    [string] $report.version -cne $Version -or
    [string] $report.architecture -cne 'win-x64' -or
    [string] $report.installer -cne $installerName) {
    throw 'The Stable package report identity does not match the expected artifact.'
}
if ([string] $report.installer -like '*UNCOMMITTED*' -or
    [string] $report.commit -notmatch '^[0-9A-Fa-f]{12}$' -or
    -not ([string] $report.commit).Equals(
        $currentShortCommit,
        [System.StringComparison]::OrdinalIgnoreCase)) {
    throw 'The Stable package report does not match the clean public commit.'
}
if ($report.releaseCandidate -isnot [bool] -or
    -not [bool] $report.releaseCandidate -or
    $report.selfContained -isnot [bool] -or
    -not [bool] $report.selfContained -or
    $report.uiAndCoreSingleFile -isnot [bool] -or
    -not [bool] $report.uiAndCoreSingleFile) {
    throw 'The Stable package report is not a formal self-contained release candidate.'
}

$installer = Get-Item -LiteralPath $installerPath
$installerStream = [System.IO.File]::OpenRead($installerPath)
try {
    if ($installerStream.Length -lt 2 -or
        $installerStream.ReadByte() -ne [int] [char] 'M' -or
        $installerStream.ReadByte() -ne [int] [char] 'Z') {
        throw 'The Stable installer is not a Windows executable.'
    }
}
finally {
    $installerStream.Dispose()
}
$installerHash = Get-AgenTallySha256 -Path $installerPath
if ([string] $report.sha256 -notmatch '^[0-9A-Fa-f]{64}$' -or
    -not ([string] $report.sha256).Equals(
        $installerHash,
        [System.StringComparison]::OrdinalIgnoreCase) -or
    [int64] $report.bytes -ne [int64] $installer.Length) {
    throw 'The Stable installer size or SHA-256 does not match its report.'
}

$expectedSidecar = "$installerHash  $installerName"
$actualSidecarBytes = [System.BitConverter]::ToString(
    [System.IO.File]::ReadAllBytes($hashPath))
$ascii = [System.Text.Encoding]::ASCII
$expectedSidecarBytes = [System.BitConverter]::ToString(
    $ascii.GetBytes($expectedSidecar))
$expectedLfSidecarBytes = [System.BitConverter]::ToString(
    $ascii.GetBytes($expectedSidecar + "`n"))
$expectedCrLfSidecarBytes = [System.BitConverter]::ToString(
    $ascii.GetBytes($expectedSidecar + "`r`n"))
if (-not ($actualSidecarBytes -ceq $expectedSidecarBytes -or
        $actualSidecarBytes -ceq $expectedLfSidecarBytes -or
        $actualSidecarBytes -ceq $expectedCrLfSidecarBytes)) {
    throw 'The Stable SHA-256 sidecar is not the exact expected single entry.'
}

$signature = Get-AuthenticodeSignature -LiteralPath $installerPath
if ($report.signed -isnot [bool]) {
    throw 'The Stable package report signed field is not Boolean.'
}
if ($SignaturePolicy -eq 'Unsigned') {
    if ([bool] $report.signed -or
        $signature.Status -ne [System.Management.Automation.SignatureStatus]::NotSigned) {
        throw 'The Stable installer does not match the expected unsigned policy.'
    }
}
elseif (-not [bool] $report.signed -or
    $signature.Status -ne [System.Management.Automation.SignatureStatus]::Valid) {
    throw 'The Stable installer does not match the expected signed policy.'
}

if ([int] $report.innoSetupCompilerMajorVersion -lt 6 -or
    [string]::IsNullOrWhiteSpace([string] $report.innoSetupCompilerSigner) -or
    -not ([string] $report.innoSetupCompilerSigner).Contains('CN=Pyrsys B.V.')) {
    throw 'The Stable package report does not identify an approved Inno Setup compiler.'
}

Write-Host (
    'Formal Stable release artifact verified: version={0}; commit={1}; bytes={2}; sha256={3}; signature={4}.' -f @(
        $Version,
        $currentShortCommit,
        $installer.Length,
        $installerHash,
        $SignaturePolicy))
