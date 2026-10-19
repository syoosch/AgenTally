#requires -Version 5.1

[CmdletBinding()]
param(
    [string] $Root = (Split-Path -Parent $PSScriptRoot),
    [string] $FallbackRoot
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Get-NormalizedRoot {
    param([Parameter(Mandatory)] [string] $Path)

    $fullPath = [System.IO.Path]::GetFullPath($Path).TrimEnd(
        [System.IO.Path]::DirectorySeparatorChar,
        [System.IO.Path]::AltDirectorySeparatorChar)
    if (-not (Test-Path -LiteralPath $fullPath -PathType Container)) {
        throw "Documentation root does not exist: $fullPath"
    }

    return $fullPath
}

function Get-RelativePath {
    param(
        [Parameter(Mandatory)] [string] $BasePath,
        [Parameter(Mandatory)] [string] $FullName
    )

    $prefix = $BasePath + [System.IO.Path]::DirectorySeparatorChar
    if (-not $FullName.StartsWith(
            $prefix,
            [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Path escaped the documentation root: $FullName"
    }

    return $FullName.Substring($prefix.Length).Replace('\', '/')
}

function Get-RootedTarget {
    param(
        [Parameter(Mandatory)] [string] $BasePath,
        [Parameter(Mandatory)] [string] $SourceRelativePath,
        [Parameter(Mandatory)] [string] $Target
    )

    $sourceDirectory = Split-Path -Parent $SourceRelativePath
    $candidate = if ([string]::IsNullOrWhiteSpace($sourceDirectory)) {
        Join-Path $BasePath $Target.Replace('/', '\')
    }
    else {
        Join-Path (Join-Path $BasePath $sourceDirectory.Replace('/', '\')) `
            $Target.Replace('/', '\')
    }

    $fullPath = [System.IO.Path]::GetFullPath($candidate)
    $prefix = $BasePath + [System.IO.Path]::DirectorySeparatorChar
    if (-not $fullPath.StartsWith(
            $prefix,
            [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Markdown link escaped the documentation root: $SourceRelativePath -> $Target"
    }

    return $fullPath
}

function Get-DocumentationFiles {
    param([Parameter(Mandatory)] [string] $BasePath)

    # Match the public boundary's source-file scope. A built worktree also
    # contains ignored runtime/tooling output, whose third-party Markdown is
    # not part of the public snapshot. New untracked source docs remain checked.
    if (Test-Path -LiteralPath (Join-Path $BasePath '.git') -PathType Container) {
        $paths = @(& git -c "safe.directory=$BasePath" -C $BasePath `
            -c core.quotePath=false ls-files --cached --others --exclude-standard)
        if ($LASTEXITCODE -ne 0) {
            throw 'Could not enumerate public documentation paths.'
        }

        foreach ($relativePath in $paths) {
            $path = [System.IO.Path]::GetFullPath((Join-Path $BasePath $relativePath))
            if (-not $path.StartsWith(
                    $BasePath + [System.IO.Path]::DirectorySeparatorChar,
                    [System.StringComparison]::OrdinalIgnoreCase)) {
                throw "Documentation file escaped the root: $relativePath"
            }
            if (-not (Test-Path -LiteralPath $path)) { continue }
            if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
                throw "Public documentation path is not a regular file: $relativePath"
            }
            Get-Item -LiteralPath $path -Force
        }
        return
    }

    # Export candidates and overlays have no Git directory and contain only
    # their explicitly supplied file tree; validate every file there.
    Get-ChildItem -LiteralPath $BasePath -Recurse -File -Force
}

function Get-FileSet {
    param([Parameter(Mandatory)] [string] $BasePath)

    $set = [System.Collections.Generic.HashSet[string]]::new(
        [System.StringComparer]::Ordinal)
    foreach ($file in Get-DocumentationFiles -BasePath $BasePath) {
        [void]$set.Add((Get-RelativePath -BasePath $BasePath -FullName $file.FullName))
    }

    return ,$set
}

function Assert-Contains {
    param(
        [Parameter(Mandatory)] [string] $Path,
        [Parameter(Mandatory)] [string] $Content,
        [Parameter(Mandatory)] [string] $Expected
    )

    if (-not $Content.Contains($Expected)) {
        throw "$Path is missing required content: $Expected"
    }
}

function Assert-IssueForm {
    param(
        [Parameter(Mandatory)] [string] $BasePath,
        [Parameter(Mandatory)] [string] $RelativePath,
        [Parameter(Mandatory)] [string] $ExpectedLabel,
        [Parameter(Mandatory)] [string[]] $RequiredIds
    )

    $path = Join-Path $BasePath $RelativePath.Replace('/', '\')
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Required Issue Form is missing: $RelativePath"
    }

    $content = Get-Content -LiteralPath $path -Raw -Encoding UTF8
    $labelBlock = [regex]::Match(
        $content,
        '(?ms)^labels:\s*\r?\n(?<items>(?:  - .+\r?\n?)+)')
    if (-not $labelBlock.Success) {
        throw "$RelativePath does not define top-level labels."
    }

    $labels = @([regex]::Matches(
        $labelBlock.Groups['items'].Value,
        '(?m)^  - (?<label>.+?)\s*$') | ForEach-Object {
            $_.Groups['label'].Value.Trim()
        })
    if ($labels -notcontains $ExpectedLabel) {
        throw "$RelativePath does not apply the required '$ExpectedLabel' label."
    }

    $idMatches = @([regex]::Matches(
        $content,
        '(?m)^[ \t]+id:[ \t]*(?<id>[A-Za-z0-9_-]+)[ \t]*\r?$'))
    $ids = @($idMatches | ForEach-Object { $_.Groups['id'].Value })
    $duplicateIds = @($ids | Group-Object | Where-Object { $_.Count -gt 1 })
    if ($duplicateIds.Count -gt 0) {
        throw (
            "$RelativePath contains duplicate field IDs: " +
            (($duplicateIds | ForEach-Object { $_.Name }) -join ', '))
    }

    $blocks = @([regex]::Matches(
        $content,
        '(?ms)^  - type:[ \t]*\S+[ \t]*\r?\n(?<body>.*?)(?=^  - type:|\z)'))
    foreach ($requiredId in $RequiredIds) {
        if ($ids -notcontains $requiredId) {
            throw "$RelativePath is missing required field ID '$requiredId'."
        }

        $matchingBlocks = @($blocks | Where-Object {
            $_.Groups['body'].Value -match (
                '(?m)^[ \t]+id:[ \t]*' +
                [regex]::Escape($requiredId) + '[ \t]*\r?$')
        })
        if ($matchingBlocks.Count -ne 1) {
            throw "$RelativePath does not contain exactly one block for '$requiredId'."
        }
        if ($matchingBlocks[0].Groups['body'].Value -notmatch
            '(?m)^[ \t]+required:[ \t]*true[ \t]*\r?$') {
            throw "$RelativePath field '$requiredId' is not required."
        }
    }

    Assert-Contains -Path $RelativePath -Content $content `
        -Expected 'https://github.com/syoosch/AgenTally/issues'
}

$rootPath = Get-NormalizedRoot -Path $Root
$fallbackPath = $null
if (-not [string]::IsNullOrWhiteSpace($FallbackRoot)) {
    $fallbackPath = Get-NormalizedRoot -Path $FallbackRoot
}

$availableFiles = Get-FileSet -BasePath $rootPath
if ($null -ne $fallbackPath) {
    $fallbackFiles = Get-FileSet -BasePath $fallbackPath
    foreach ($relativePath in $fallbackFiles) {
        [void]$availableFiles.Add($relativePath)
    }
}

$markdownFiles = @(Get-DocumentationFiles -BasePath $rootPath | Where-Object {
    $_.Extension -ieq '.md'
})
$inlineLinkPattern = @'
!?\[[^\]]*\]\((?<target><[^>]+>|[^\s)]+)(?:\s+["'][^"']*["'])?\)
'@.Trim()
$referenceLinkPattern = @'
(?m)^\s*\[[^\]]+\]:\s*(?<target><[^>]+>|[^\s]+)
'@.Trim()
$checkedLinkCount = 0

foreach ($markdownFile in $markdownFiles) {
    $sourceRelative = Get-RelativePath -BasePath $rootPath `
        -FullName $markdownFile.FullName
    $content = Get-Content -LiteralPath $markdownFile.FullName -Raw -Encoding UTF8
    $matches = @([regex]::Matches($content, $inlineLinkPattern)) +
        @([regex]::Matches($content, $referenceLinkPattern))

    foreach ($match in $matches) {
        $target = $match.Groups['target'].Value.Trim().Trim('<', '>')
        if ([string]::IsNullOrWhiteSpace($target) -or
            $target.StartsWith('#', [System.StringComparison]::Ordinal) -or
            $target.StartsWith('//', [System.StringComparison]::Ordinal) -or
            $target -match '^[A-Za-z][A-Za-z0-9+.-]*:') {
            continue
        }

        $target = [System.Uri]::UnescapeDataString(
            (($target -split '#', 2)[0] -split '\?', 2)[0])
        if ([string]::IsNullOrWhiteSpace($target)) {
            continue
        }

        $fullTarget = Get-RootedTarget -BasePath $rootPath `
            -SourceRelativePath $sourceRelative -Target $target
        $targetRelative = Get-RelativePath -BasePath $rootPath `
            -FullName $fullTarget
        if (-not $availableFiles.Contains($targetRelative)) {
            throw "Broken or incorrectly cased Markdown link: $sourceRelative -> $target"
        }

        $checkedLinkCount++
    }
}

$documentPairs = @(
    @{
        English = 'README.md'
        Chinese = 'README.zh-CN.md'
        EnglishLink = '(README.zh-CN.md)'
        ChineseLink = '[English](README.md)'
    },
    @{
        English = 'SUPPORT.md'
        Chinese = 'SUPPORT.zh-CN.md'
        EnglishLink = '(SUPPORT.zh-CN.md)'
        ChineseLink = '[English](SUPPORT.md)'
    },
    @{
        English = 'docs/USER_GUIDE.md'
        Chinese = 'docs/USER_GUIDE.zh-CN.md'
        EnglishLink = '(USER_GUIDE.zh-CN.md)'
        ChineseLink = '[English](USER_GUIDE.md)'
    },
    @{
        English = 'PRIVACY.md'
        Chinese = 'PRIVACY.zh-CN.md'
        EnglishLink = '(PRIVACY.zh-CN.md)'
        ChineseLink = '[English](PRIVACY.md)'
    }
)

foreach ($pair in $documentPairs) {
    $englishPath = Join-Path $rootPath $pair.English.Replace('/', '\')
    $chinesePath = Join-Path $rootPath $pair.Chinese.Replace('/', '\')
    if (-not (Test-Path -LiteralPath $englishPath -PathType Leaf) -or
        -not (Test-Path -LiteralPath $chinesePath -PathType Leaf)) {
        throw "Bilingual document pair is incomplete: $($pair.English), $($pair.Chinese)"
    }

    $englishContent = Get-Content -LiteralPath $englishPath -Raw -Encoding UTF8
    $chineseContent = Get-Content -LiteralPath $chinesePath -Raw -Encoding UTF8
    Assert-Contains -Path $pair.English -Content $englishContent `
        -Expected $pair.EnglishLink
    Assert-Contains -Path $pair.Chinese -Content $chineseContent `
        -Expected $pair.ChineseLink
    Assert-Contains -Path $pair.English -Content $englishContent `
        -Expected '**English**'
}

$semanticVersionPattern = '(?<![A-Za-z0-9])v?\d+\.\d+\.\d+(?![A-Za-z0-9])'
foreach ($pair in $documentPairs) {
    foreach ($relativePath in @($pair.English, $pair.Chinese)) {
        $path = Join-Path $rootPath $relativePath.Replace('/', '\')
        if ((Get-Content -LiteralPath $path -Raw -Encoding UTF8) -match
            $semanticVersionPattern) {
            throw "Evergreen user documentation contains a fixed semantic version: $relativePath"
        }
    }
}

Assert-IssueForm -BasePath $rootPath `
    -RelativePath '.github/ISSUE_TEMPLATE/bug_report.yml' `
    -ExpectedLabel 'bug' `
    -RequiredIds @(
        'version',
        'windows',
        'install',
        'agents',
        'compatibility',
        'steps',
        'expected',
        'actual',
        'privacy')
Assert-IssueForm -BasePath $rootPath `
    -RelativePath '.github/ISSUE_TEMPLATE/feature_request.yml' `
    -ExpectedLabel 'enhancement' `
    -RequiredIds @('problem', 'outcome', 'privacy')

$bugForm = Get-Content -LiteralPath (
    Join-Path $rootPath '.github\ISSUE_TEMPLATE\bug_report.yml') `
    -Raw -Encoding UTF8
foreach ($compatibilityOption in @(
    'Fully compatible /',
    'Partially compatible /',
    'Not currently compatible /',
    'Source not listed /',
    'Not Agent-specific /',
    'Unsure /')) {
    Assert-Contains -Path '.github/ISSUE_TEMPLATE/bug_report.yml' `
        -Content $bugForm -Expected $compatibilityOption
}

$chooserPath = Join-Path $rootPath '.github\ISSUE_TEMPLATE\config.yml'
if (-not (Test-Path -LiteralPath $chooserPath -PathType Leaf)) {
    throw 'Issue chooser configuration is missing.'
}
$chooser = Get-Content -LiteralPath $chooserPath -Raw -Encoding UTF8
Assert-Contains -Path '.github/ISSUE_TEMPLATE/config.yml' -Content $chooser `
    -Expected 'blank_issues_enabled: false'
Assert-Contains -Path '.github/ISSUE_TEMPLATE/config.yml' -Content $chooser `
    -Expected 'https://github.com/syoosch/AgenTally/blob/main/SUPPORT.md'
Assert-Contains -Path '.github/ISSUE_TEMPLATE/config.yml' -Content $chooser `
    -Expected 'https://github.com/syoosch/AgenTally/security/advisories/new'

$pullRequestTemplate = Join-Path $rootPath '.github\pull_request_template.md'
if (-not (Test-Path -LiteralPath $pullRequestTemplate -PathType Leaf)) {
    throw 'Pull request template is missing.'
}

Write-Host (
    "AgenTally public documentation passed: " +
    "$($markdownFiles.Count) Markdown files, $checkedLinkCount relative links, " +
    "$($documentPairs.Count) bilingual pairs, and 2 Issue Forms.") `
    -ForegroundColor Green
