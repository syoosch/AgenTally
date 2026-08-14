#requires -Version 5.1

[CmdletBinding()]
param(
    [string] $RepositoryRoot
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($RepositoryRoot)) {
    $RepositoryRoot = Split-Path -Parent $PSScriptRoot
}

function Get-NormalizedRoot {
    param([Parameter(Mandatory)] [string] $Path)

    $resolved = [System.IO.Path]::GetFullPath($Path)
    if (-not (Test-Path -LiteralPath $resolved -PathType Container)) {
        throw "Public repository root does not exist: $resolved"
    }

    return $resolved.TrimEnd(
        [System.IO.Path]::DirectorySeparatorChar,
        [System.IO.Path]::AltDirectorySeparatorChar)
}

function Get-RelativeFilePath {
    param(
        [Parameter(Mandatory)] [string] $Root,
        [Parameter(Mandatory)] [string] $FullName
    )

    $prefix = $Root + [System.IO.Path]::DirectorySeparatorChar
    if (-not $FullName.StartsWith(
            $prefix,
            [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "File escaped the public repository root: $FullName"
    }

    return $FullName.Substring($prefix.Length).Replace('\', '/')
}

function Get-OwnedPublicPath {
    param(
        [Parameter(Mandatory)] [string] $Root,
        [Parameter(Mandatory)] [string] $RelativePath
    )

    $normalized = $RelativePath.Trim().Replace('\', '/')
    if ([string]::IsNullOrWhiteSpace($normalized) -or
        [System.IO.Path]::IsPathRooted($normalized) -or
        $normalized.StartsWith('/', [System.StringComparison]::Ordinal) -or
        $normalized.Contains('//') -or
        $normalized -match '(^|/)\.\.(/|$)' -or
        $normalized -match '(^|/)\.(/|$)') {
        throw "Invalid Git public path: $RelativePath"
    }

    $fullPath = [System.IO.Path]::GetFullPath(
        (Join-Path $Root $normalized.Replace('/', '\')))
    $prefix = $Root + [System.IO.Path]::DirectorySeparatorChar
    if (-not $fullPath.StartsWith(
            $prefix,
            [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Git public path escaped the repository root: $RelativePath"
    }

    return $fullPath
}

function Get-OwnedPublicFile {
    param(
        [Parameter(Mandatory)] [string] $Root,
        [Parameter(Mandatory)] [string] $RelativePath
    )

    $fullPath = Get-OwnedPublicPath -Root $Root -RelativePath $RelativePath
    if (-not (Test-Path -LiteralPath $fullPath -PathType Leaf)) {
        throw "Git public file is missing from the worktree: $RelativePath"
    }

    return Get-Item -LiteralPath $fullPath -Force
}

function Assert-NoReparsePathChain {
    param(
        [Parameter(Mandatory)] [string] $Root,
        [Parameter(Mandatory)] $Item
    )

    $current = $Item
    while ($null -ne $current) {
        if (($current.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Public tree contains a reparse point: $($current.FullName)"
        }
        if ($current.FullName.Equals(
                $Root,
                [System.StringComparison]::OrdinalIgnoreCase)) {
            return
        }
        $current = if ($current -is [System.IO.DirectoryInfo]) {
            $current.Parent
        }
        else {
            $current.Directory
        }
    }

    throw "Could not verify the public path chain for: $($Item.FullName)"
}

$root = Get-NormalizedRoot -Path $RepositoryRoot
$gitRoot = Join-Path $root '.git'
if (Test-Path -LiteralPath $gitRoot -PathType Container) {
    $gitPaths = @(& git -c "safe.directory=$root" -C $root `
        -c core.quotePath=false ls-files --cached --others --exclude-standard)
    if ($LASTEXITCODE -ne 0) {
        throw 'Could not enumerate Git public paths.'
    }
    $files = @($gitPaths | ForEach-Object {
        $relativePath = $_
        $fullPath = Get-OwnedPublicPath -Root $root -RelativePath $relativePath
        if (-not (Test-Path -LiteralPath $fullPath)) {
            return
        }
        if (-not (Test-Path -LiteralPath $fullPath -PathType Leaf)) {
            throw "Git public path is not a regular file: $relativePath"
        }

        Get-Item -LiteralPath $fullPath -Force
    })
    $items = $files
}
else {
    $items = @(Get-ChildItem -LiteralPath $root -Recurse -Force)
    $files = @($items | Where-Object { -not $_.PSIsContainer })
}

foreach ($item in $items) {
    Assert-NoReparsePathChain -Root $root -Item $item
}

$relativeFiles = @($files | ForEach-Object {
    Get-RelativeFilePath -Root $root -FullName $_.FullName
})
$relativeSet = [System.Collections.Generic.HashSet[string]]::new(
    [System.StringComparer]::OrdinalIgnoreCase)
foreach ($relativePath in $relativeFiles) {
    if (-not $relativeSet.Add($relativePath)) {
        throw "Duplicate public path after case-insensitive normalization: $relativePath"
    }
}

$requiredPaths = @(
    '.agentally-public',
    '.agentally-root',
    '.github/workflows/ci.yml',
    'AgenTally.sln',
    'CHANGELOG.md',
    'CONTRIBUTING.md',
    'Directory.Build.props',
    'docs/PACKAGING.md',
    'global.json',
    'LICENSE',
    'README.md',
    'SECURITY.md',
    'THIRD_PARTY_NOTICES.md',
    'scripts/Test-AgenTallyPublicBoundary.ps1',
    'src/AgenTally.Core/AgenTally.Core.csproj',
    'src/AgenTally.Domain/AgenTally.Domain.csproj',
    'src/AgenTally.Storage/AgenTally.Storage.csproj',
    'src/AgenTally.UI/AgenTally.UI.csproj',
    'tests/AgenTally.Tests/AgenTally.Tests.csproj'
)
foreach ($requiredPath in $requiredPaths) {
    if (-not $relativeSet.Contains($requiredPath)) {
        throw "Required public file is missing: $requiredPath"
    }
}

$allowedRootFiles = @(
    '.agentally-public',
    '.agentally-root',
    '.editorconfig',
    '.gitignore',
    'AgenTally.sln',
    'CHANGELOG.md',
    'CONTRIBUTING.md',
    'Directory.Build.props',
    'LICENSE',
    'README.md',
    'SECURITY.md',
    'THIRD_PARTY_NOTICES.md',
    'global.json'
)
$allowedExactPaths = @(
    '.github/workflows/ci.yml',
    'assets/icon/AgenTally.ico',
    'docs/PACKAGING.md',
    'packaging/stable/AgenTally.InstallIdentity.json',
    'packaging/stable/AgenTally.iss',
    'packaging/stable/Invoke-AgenTallyStableMaintenance.ps1',
    'packaging/stable/StableMaintenance.ps1',
    'scripts/Build-AgenTallyStableInstaller.ps1',
    'scripts/Publish-AgenTallyStablePackage.ps1',
    'scripts/Test-AgenTallyFocused.ps1',
    'scripts/Test-AgenTallyPrepackageSecurity.ps1',
    'scripts/Test-AgenTallyPublicBoundary.ps1',
    'scripts/Update-AgenTallyModelCatalog.ps1',
    'scripts/Update-AgenTallyPriceCatalog.ps1'
)
$allowedPrefixes = @(
    'src/AgenTally.Core/',
    'src/AgenTally.Domain/',
    'src/AgenTally.Storage/',
    'src/AgenTally.UI/',
    'tests/AgenTally.Tests/'
)
foreach ($relativePath in $relativeFiles) {
    $allowed = $allowedRootFiles -contains $relativePath -or
        $allowedExactPaths -contains $relativePath
    if (-not $allowed) {
        foreach ($prefix in $allowedPrefixes) {
            if ($relativePath.StartsWith(
                    $prefix,
                    [System.StringComparison]::OrdinalIgnoreCase)) {
                $allowed = $true
                break
            }
        }
    }
    if (-not $allowed) {
        throw "Path is outside the positive public layout: $relativePath"
    }

    if ($relativePath.StartsWith(
            'src/AgenTally.Core/Collectors/Mock/',
            [System.StringComparison]::OrdinalIgnoreCase) -or
        $relativePath.StartsWith(
            'tests/AgenTally.Tests/Benchmark/',
            [System.StringComparison]::OrdinalIgnoreCase) -or
        $relativePath -match
            '^tests/AgenTally\.Tests/(Runtime|UI)/Development[^/]*\.cs$') {
        throw "Development-only or test-support code escaped into the public product layout: $relativePath"
    }
}

$allowedSpecialNames = @(
    '.agentally-public',
    '.agentally-root',
    '.editorconfig',
    '.gitignore',
    'LICENSE'
)
$allowedExtensions = @(
    '.cs', '.csproj', '.ico', '.iss', '.json', '.jsonl', '.md', '.props',
    '.ps1', '.runsettings', '.sln', '.xaml', '.yml'
)
foreach ($relativePath in $relativeFiles) {
    $leafName = [System.IO.Path]::GetFileName($relativePath)
    $extension = [System.IO.Path]::GetExtension($relativePath).ToLowerInvariant()
    if (($allowedSpecialNames -notcontains $leafName) -and
        ($allowedExtensions -notcontains $extension)) {
        throw "Unapproved public file type: $relativePath"
    }
}

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
$privacyPatterns = @(
    '(?i)github\.com[/:][^/\s]+/AgenTally[_-]internal(?:\.git)?(?:\s|$)',
    '(?i)[A-Z]:\\Users\\(?!fixture(?:\\|$)|test(?:\\|$))[^\\\r\n]+\\',
    '(?i)[A-Z]:\\Projects\\codex\\AgenTally(?:\\|["''\s]|$)'
)
$textExtensions = @(
    '.cs', '.csproj', '.iss', '.json', '.jsonl', '.md', '.props', '.ps1',
    '.runsettings', '.sln', '.xaml', '.yml'
)
$sensitiveFiles = [System.Collections.Generic.HashSet[string]]::new(
    [System.StringComparer]::OrdinalIgnoreCase)
foreach ($file in $files) {
    $extension = $file.Extension.ToLowerInvariant()
    $isSpecialText = $allowedSpecialNames -contains $file.Name
    if (($textExtensions -notcontains $extension) -and (-not $isSpecialText)) {
        continue
    }

    $containsSecret = Select-String -LiteralPath $file.FullName `
        -Pattern $secretPatterns -Quiet
    $containsPrivateContent = Select-String -LiteralPath $file.FullName `
        -Pattern $privacyPatterns -Quiet
    if ($containsSecret -or $containsPrivateContent) {
        [void]$sensitiveFiles.Add(
            (Get-RelativeFilePath -Root $root -FullName $file.FullName))
    }
}
if ($sensitiveFiles.Count -gt 0) {
    throw (
        'Sensitive or internal-only content was found in public paths: ' +
        ((@($sensitiveFiles) | Sort-Object) -join ', '))
}

$solution = Get-Content -LiteralPath (Join-Path $root 'AgenTally.sln') -Raw
$testProject = Get-Content -LiteralPath (
    Join-Path $root 'tests\AgenTally.Tests\AgenTally.Tests.csproj') -Raw
$expectedSolutionProjects = @(
    'src\AgenTally.Core\AgenTally.Core.csproj',
    'src\AgenTally.Domain\AgenTally.Domain.csproj',
    'src\AgenTally.Storage\AgenTally.Storage.csproj',
    'src\AgenTally.UI\AgenTally.UI.csproj',
    'tests\AgenTally.Tests\AgenTally.Tests.csproj'
)
$solutionProjects = @([regex]::Matches(
    $solution,
    '"(?<path>[^"\r\n]+\.csproj)"') | ForEach-Object {
        $_.Groups['path'].Value.Replace('/', '\')
    })
if ($solutionProjects.Count -ne $expectedSolutionProjects.Count) {
    throw 'The public solution does not contain the exact approved project set.'
}
foreach ($expectedProject in $expectedSolutionProjects) {
    if ($solutionProjects -notcontains $expectedProject) {
        throw "The public solution is missing its approved project: $expectedProject"
    }
}

$expectedTestReferences = @(
    '..\..\src\AgenTally.Core\AgenTally.Core.csproj',
    '..\..\src\AgenTally.Domain\AgenTally.Domain.csproj',
    '..\..\src\AgenTally.Storage\AgenTally.Storage.csproj',
    '..\..\src\AgenTally.UI\AgenTally.UI.csproj'
)
$testReferences = @([regex]::Matches(
    $testProject,
    '<ProjectReference\s+Include="(?<path>[^"]+)"') | ForEach-Object {
        $_.Groups['path'].Value.Replace('/', '\')
    })
if ($testReferences.Count -ne $expectedTestReferences.Count) {
    throw 'The public test project does not contain the exact approved references.'
}
foreach ($expectedReference in $expectedTestReferences) {
    if ($testReferences -notcontains $expectedReference) {
        throw "The public test project is missing its approved reference: $expectedReference"
    }
}

Write-Host (
    "AgenTally public boundary passed for $($relativeFiles.Count) files.") `
    -ForegroundColor Green
