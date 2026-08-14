#requires -Version 5.1

[CmdletBinding()]
param(
    [switch] $AllowDirtyWorktree
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$solution = Join-Path $repositoryRoot 'AgenTally.sln'
$publicMarker = Join-Path $repositoryRoot '.agentally-public'
$publicBoundary = Join-Path $PSScriptRoot 'Test-AgenTallyPublicBoundary.ps1'
$secretPatterns = @(
    '-----BEGIN ([A-Z0-9]+ )?PRIVATE KEY-----',
    'AKIA[0-9A-Z]{16}',
    'ASIA[0-9A-Z]{16}',
    'AIza[0-9A-Za-z_-]{35}',
    'github_pat_[A-Za-z0-9_]{80,255}',
    'gh[pousr]_[A-Za-z0-9]{36,255}',
    'sk-(proj-|svcacct-)?[A-Za-z0-9_-]{20,}',
    'xox[baprs]-[A-Za-z0-9-]{20,}'
)
$textExtensions = @(
    '.bat', '.cmd', '.config', '.cs', '.csproj', '.env', '.ini', '.json',
    '.md', '.props', '.ps1', '.resx', '.sh', '.sln', '.targets', '.toml',
    '.txt', '.xaml', '.xml', '.yaml', '.yml'
)
$textFileNames = @(
    '.editorconfig', '.gitattributes', '.gitignore', 'dockerfile'
)

function Invoke-Checked {
    param(
        [Parameter(Mandatory)]
        [string] $FilePath,

        [Parameter(Mandatory)]
        [string[]] $Arguments
    )

    & $FilePath @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "$FilePath failed with exit code $LASTEXITCODE."
    }
}

function Get-RepositoryTextFiles {
    $paths = @(& git ls-files --cached --others --exclude-standard)
    if ($LASTEXITCODE -ne 0) {
        throw 'git ls-files failed.'
    }

    return @($paths | Where-Object {
        $fullPath = [System.IO.Path]::GetFullPath(
            (Join-Path $repositoryRoot $_))
        $rootPrefix = $repositoryRoot.TrimEnd('\', '/') +
            [System.IO.Path]::DirectorySeparatorChar
        if (-not $fullPath.StartsWith(
                $rootPrefix,
                [System.StringComparison]::OrdinalIgnoreCase)) {
            throw "Git path escaped the repository root: $_"
        }
        if (-not (Test-Path -LiteralPath $fullPath)) {
            return $false
        }
        if (-not (Test-Path -LiteralPath $fullPath -PathType Leaf)) {
            throw "Git path is not a regular file: $_"
        }

        $extension = [System.IO.Path]::GetExtension($_)
        $leafName = [System.IO.Path]::GetFileName($_).ToLowerInvariant()
        $textExtensions -contains $extension.ToLowerInvariant() -or
            $textFileNames -contains $leafName
    })
}

Push-Location $repositoryRoot
try {
    if (-not (Test-Path -LiteralPath $solution -PathType Leaf)) {
        throw 'AgenTally.sln was not found at the repository root.'
    }
    if (Test-Path -LiteralPath $publicMarker -PathType Leaf) {
        if (-not (Test-Path -LiteralPath $publicBoundary -PathType Leaf)) {
            throw 'The public repository boundary gate is missing.'
        }
        & $publicBoundary -RepositoryRoot $repositoryRoot
    }

    $dirty = @(& git status --porcelain=v1 --untracked-files=all)
    if ($LASTEXITCODE -ne 0) {
        throw 'git status failed.'
    }
    if ($dirty.Count -gt 0 -and -not $AllowDirtyWorktree) {
        throw (
            'The prepackage source gate requires a clean worktree. ' +
            'Use -AllowDirtyWorktree only while developing the gate itself.')
    }

    Write-Host '[1/5] Verifying locked dependency restore and NuGet audit...'
    Invoke-Checked dotnet @(
        'restore', $solution,
        '--locked-mode',
        '-p:NuGetAudit=true',
        '-p:NuGetAuditMode=all',
        '-p:NuGetAuditLevel=low',
        '-p:TreatWarningsAsErrors=true'
    )

    $projects = @(Get-ChildItem -LiteralPath $repositoryRoot -Recurse -Filter '*.csproj' |
        Where-Object { $_.FullName -notmatch '[\\/](bin|obj|artifacts)[\\/]' })
    $missingLocks = @($projects | Where-Object {
        -not (Test-Path -LiteralPath (
            Join-Path $_.DirectoryName 'packages.lock.json') -PathType Leaf)
    } | ForEach-Object {
        [System.IO.Path]::GetRelativePath($repositoryRoot, $_.FullName)
    })
    if ($missingLocks.Count -gt 0) {
        throw ('Missing package lock files: ' + ($missingLocks -join ', '))
    }

    Write-Host '[2/5] Listing vulnerable direct and transitive packages...'
    Invoke-Checked dotnet @(
        'list', $solution, 'package',
        '--vulnerable',
        '--include-transitive',
        '--no-restore'
    )

    Write-Host '[3/5] Scanning current repository text without printing matches...'
    $currentSecretFiles = [System.Collections.Generic.HashSet[string]]::new(
        [System.StringComparer]::OrdinalIgnoreCase)
    foreach ($relativePath in Get-RepositoryTextFiles) {
        $fullPath = Join-Path $repositoryRoot $relativePath
        if (Select-String -LiteralPath $fullPath -Pattern $secretPatterns -Quiet) {
            [void]$currentSecretFiles.Add($relativePath)
        }
    }
    if ($currentSecretFiles.Count -gt 0) {
        throw (
            'Potential high-confidence secret formats were found in: ' +
            ((@($currentSecretFiles) | Sort-Object) -join ', '))
    }

    Write-Host '[4/5] Scanning Git history without printing matched content...'
    $historicalSecretPaths = [System.Collections.Generic.HashSet[string]]::new(
        [System.StringComparer]::OrdinalIgnoreCase)
    foreach ($pattern in $secretPatterns) {
        $paths = @(& git log --all --name-only --pretty=format: -G $pattern --)
        if ($LASTEXITCODE -ne 0) {
            throw 'git log secret-history scan failed.'
        }
        foreach ($path in $paths) {
            if (-not [string]::IsNullOrWhiteSpace($path)) {
                [void]$historicalSecretPaths.Add($path.Trim())
            }
        }
    }
    if ($historicalSecretPaths.Count -gt 0) {
        throw (
            'Potential high-confidence secret formats exist in Git history paths: ' +
            ((@($historicalSecretPaths) | Sort-Object) -join ', '))
    }

    Write-Host '[5/5] Enforcing runtime networking and telemetry source boundaries...'
    $runtimeSourceFiles = @(Get-RepositoryTextFiles | Where-Object {
        $_ -like 'src/*' -or $_ -like 'src\*'
    })
    $networkPatterns = @(
        'System\.Net',
        'HttpClient',
        'HttpRequestMessage',
        'SocketsHttpHandler',
        'TcpClient',
        'UdpClient',
        'WebRequest'
    )
    $networkFiles = @($runtimeSourceFiles | Where-Object {
        Select-String -LiteralPath (Join-Path $repositoryRoot $_) `
            -Pattern $networkPatterns -Quiet
    })
    $unexpectedNetworkFiles = @($networkFiles | Where-Object {
        $normalized = $_.Replace('\', '/')
        -not $normalized.StartsWith(
            'src/AgenTally.UI/Updates/',
            [System.StringComparison]::OrdinalIgnoreCase)
    })
    if ($unexpectedNetworkFiles.Count -gt 0) {
        throw (
            'Runtime networking references escaped the approved version-check boundary: ' +
            (($unexpectedNetworkFiles | Sort-Object -Unique) -join ', '))
    }

    $telemetryPatterns = @(
        'ApplicationInsights',
        'OpenTelemetry',
        'PostHog',
        'SentrySdk',
        'GoogleAnalytics'
    )
    $telemetryFiles = @($runtimeSourceFiles | Where-Object {
        Select-String -LiteralPath (Join-Path $repositoryRoot $_) `
            -Pattern $telemetryPatterns -Quiet
    })
    if ($telemetryFiles.Count -gt 0) {
        throw (
            'Telemetry references were found in runtime source files: ' +
            (($telemetryFiles | Sort-Object -Unique) -join ', '))
    }

    Write-Host 'AgenTally prepackage source security gate passed.' -ForegroundColor Green
    Write-Host (
        "Checked $($projects.Count) package locks, " +
        "$($runtimeSourceFiles.Count) runtime source files, " +
        'the current text worktree, and all Git history.')
}
finally {
    Pop-Location
}
