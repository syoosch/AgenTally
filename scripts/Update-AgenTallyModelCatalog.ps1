# Copyright (c) AgenTally contributors.
# Developer-only maintenance command. The shipped application never invokes it.
<#
.SYNOPSIS
Builds and reviews AgenTally's offline multi-source model identity catalog.

.DESCRIPTION
Downloads or reads pinned models.dev and LiteLLM inputs, generates an exact-only
candidate, compares it with the currently shipped catalog, and writes both a
machine-readable JSON diff and a complete Markdown review report. Nothing is
published unless -Apply is supplied. Conflicts always block publishing;
removals and canonical-target changes require separate explicit approval.

.PARAMETER ModelsPath
Optional local models.dev models.json. When omitted, the public file is
downloaded into the ignored Development maintenance directory.

.PARAMETER ProviderCatalogPath
Optional local models.dev catalog.json. When omitted, the public file is
downloaded into the ignored Development maintenance directory.

.PARAMETER LiteLlmCatalogPath
Optional local LiteLLM model_prices_and_context_window.json. When omitted, the
public file is downloaded into the ignored Development maintenance directory.

.PARAMETER CurrentCatalogPath
Optional comparison baseline. It defaults to the currently shipped catalog.
An alternate baseline is review-only and cannot be used with -Apply.

.PARAMETER ReviewedAliasesPath
Optional local reviewed-alias overlay. It defaults to the separately shipped
overlay and is included in conflict checks without being copied into the
generated market catalog.

.PARAMETER CatalogVersion
Candidate version. When omitted, the script proposes the next UTC-date rN
revision after the currently shipped catalog.

.PARAMETER AllowRemovals
Allows reviewed alias removals during -Apply. It does not bypass conflicts,
retargeting approval, baseline validation or catalog-version validation.

.PARAMETER AllowRetargeting
Allows reviewed aliases to change canonical target during -Apply. It does not
bypass conflicts, removal approval, baseline validation or version validation.

.PARAMETER Apply
Replaces the shipped catalog only after every safety gate passes. Unchanged
inputs are a no-op.

.EXAMPLE
.\scripts\Update-AgenTallyModelCatalog.ps1

Generates a candidate, JSON diff and Markdown report without publishing.

.EXAMPLE
.\scripts\Update-AgenTallyModelCatalog.ps1 -Apply

Publishes a changed candidate when it contains no conflict or unapproved
breaking change; otherwise fails after writing the review artifacts.
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [string] $ModelsPath,
    [string] $ProviderCatalogPath,
    [string] $LiteLlmCatalogPath,
    [string] $CurrentCatalogPath,
    [string] $ReviewedAliasesPath,
    [string] $CatalogVersion,
    [switch] $AllowRemovals,
    [switch] $AllowRetargeting,
    [switch] $Apply
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$maintenanceRoot = Join-Path $repoRoot 'artifacts\development\model-catalog-maintenance'
$candidatePath = Join-Path $maintenanceRoot 'market-model-aliases.candidate.json'
$diffPath = Join-Path $maintenanceRoot 'market-model-aliases.diff.json'
$reportPath = Join-Path $maintenanceRoot 'market-model-aliases.report.md'
$sourcePath = Join-Path $repoRoot 'src\AgenTally.Domain\Usage\Catalog\market-model-aliases.json'
$reviewedSourcePath = Join-Path $repoRoot 'src\AgenTally.Domain\Usage\Catalog\local-reviewed-model-aliases.json'

function Normalize-Identifier {
    param([Parameter(Mandatory)] [string] $Value)

    return $Value.Trim().ToLowerInvariant()
}

function Add-AliasTarget {
    param(
        [Parameter(Mandatory)]
        [System.Collections.Generic.Dictionary[string, System.Collections.Generic.HashSet[string]]]
        $Targets,
        [Parameter(Mandatory)]
        [System.Collections.Generic.Dictionary[string, System.Collections.Generic.HashSet[string]]]
        $Evidence,
        [Parameter(Mandatory)] [string] $Alias,
        [Parameter(Mandatory)] [string] $Canonical,
        [Parameter(Mandatory)] [string] $SourceId
    )

    $normalizedAlias = Normalize-Identifier $Alias
    $normalizedCanonical = Normalize-Identifier $Canonical
    if ([string]::IsNullOrWhiteSpace($normalizedAlias) -or
        [string]::IsNullOrWhiteSpace($normalizedCanonical)) {
        throw 'Model aliases and canonical identifiers must not be empty.'
    }

    $targetSet = $null
    if (-not $Targets.TryGetValue($normalizedAlias, [ref] $targetSet)) {
        $targetSet = [System.Collections.Generic.HashSet[string]]::new(
            [System.StringComparer]::Ordinal)
        $Targets.Add($normalizedAlias, $targetSet)
    }

    [void] $targetSet.Add($normalizedCanonical)

    $sourceSet = $null
    if (-not $Evidence.TryGetValue($normalizedAlias, [ref] $sourceSet)) {
        $sourceSet = [System.Collections.Generic.HashSet[string]]::new(
            [System.StringComparer]::Ordinal)
        $Evidence.Add($normalizedAlias, $sourceSet)
    }
    [void] $sourceSet.Add($SourceId)
}

function Get-OrDownloadJson {
    param(
        [string] $ExistingPath,
        [Parameter(Mandatory)] [string] $Uri,
        [Parameter(Mandatory)] [string] $DefaultName
    )

    if (-not [string]::IsNullOrWhiteSpace($ExistingPath)) {
        $resolved = (Resolve-Path -LiteralPath $ExistingPath).Path
        return $resolved
    }

    $downloadPath = Join-Path $maintenanceRoot $DefaultName
    Invoke-WebRequest -Uri $Uri -OutFile $downloadPath
    return $downloadPath
}

function Read-CatalogDocument {
    param([string] $Path)

    if ([string]::IsNullOrWhiteSpace($Path) -or
        -not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        return $null
    }

    $document = Get-Content -Raw -LiteralPath $Path |
        ConvertFrom-Json -AsHashtable
    if (-not $document.Contains('catalogVersion') -or
        -not $document.Contains('aliases') -or
        $null -eq $document.aliases) {
        throw "Existing model identity catalog is invalid: $Path"
    }
    return $document
}

function Add-ReviewedAliasRule {
    param(
        [Parameter(Mandatory)]
        [AllowEmptyCollection()]
        [System.Collections.Generic.List[object]] $Rules,
        [Parameter(Mandatory)]
        [AllowEmptyCollection()]
        [System.Collections.Generic.HashSet[string]] $SeenKeys,
        [Parameter(Mandatory)] [System.Collections.IDictionary] $Rule,
        [Parameter(Mandatory)] [string] $Scope
    )

    if (-not $Rule.Contains('alias') -or
        -not $Rule.Contains('canonical') -or
        -not $Rule.Contains('evidence')) {
        throw 'Reviewed model alias catalog contains an incomplete rule.'
    }

    $sourceAlias = [string] $Rule.alias
    $sourceCanonical = [string] $Rule.canonical
    $alias = Normalize-Identifier $sourceAlias
    $canonical = Normalize-Identifier $sourceCanonical
    $evidence = @($Rule.evidence)
    $uniqueEvidence = @($evidence |
        ForEach-Object { [string] $_ } |
        Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
        Sort-Object -Unique)
    if ([string]::IsNullOrWhiteSpace($alias) -or
        [string]::IsNullOrWhiteSpace($canonical) -or
        -not [string]::Equals(
            $sourceAlias,
            $alias,
            [StringComparison]::Ordinal) -or
        -not [string]::Equals(
            $sourceCanonical,
            $canonical,
            [StringComparison]::Ordinal) -or
        $canonical.Contains('/') -or
        [string]::Equals(
            $alias,
            $canonical,
            [StringComparison]::Ordinal) -or
        $uniqueEvidence.Count -ne $evidence.Count) {
        throw "Reviewed model alias catalog contains an invalid rule for '$sourceAlias'."
    }

    $key = "$Scope`0$alias"
    if (-not $SeenKeys.Add($key)) {
        throw "Reviewed model alias catalog contains duplicate alias '$alias' in scope '$Scope'."
    }
    $Rules.Add([ordered] @{
        scope = $Scope
        alias = $alias
        canonical = $canonical
        evidence = $uniqueEvidence
    })
}

function Read-ReviewedAliasDocument {
    param([Parameter(Mandatory)] [string] $Path)

    $resolvedPath = (Resolve-Path -LiteralPath $Path).Path
    $document = Get-Content -Raw -LiteralPath $resolvedPath |
        ConvertFrom-Json -AsHashtable
    if (-not $document.Contains('schemaVersion') -or
        [int] $document.schemaVersion -ne 1 -or
        -not $document.Contains('catalogVersion') -or
        [string]::IsNullOrWhiteSpace([string] $document.catalogVersion) -or
        -not $document.Contains('globalAliases') -or
        -not $document.Contains('sourceAliases') -or
        $null -eq $document.globalAliases -or
        $null -eq $document.sourceAliases -or
        @($document.globalAliases).Count + @($document.sourceAliases).Count -eq 0) {
        throw "Reviewed model alias catalog is invalid: $resolvedPath"
    }

    $rules = [System.Collections.Generic.List[object]]::new()
    $seenKeys = [System.Collections.Generic.HashSet[string]]::new(
        [System.StringComparer]::Ordinal)
    foreach ($rule in @($document.globalAliases)) {
        Add-ReviewedAliasRule $rules $seenKeys $rule 'global'
    }
    foreach ($rule in @($document.sourceAliases)) {
        if (-not $rule.Contains('agentId')) {
            throw 'Reviewed model alias catalog contains a source rule without agentId.'
        }
        $sourceAgentId = [string] $rule.agentId
        $agentId = Normalize-Identifier $sourceAgentId
        if ([string]::IsNullOrWhiteSpace($agentId) -or
            -not [string]::Equals(
                $sourceAgentId,
                $agentId,
                [StringComparison]::Ordinal)) {
            throw "Reviewed model alias catalog contains invalid agentId '$sourceAgentId'."
        }
        Add-ReviewedAliasRule $rules $seenKeys $rule "agent:$agentId"
    }

    $targetsByAlias =
        [System.Collections.Generic.Dictionary[string, string]]::new(
            [System.StringComparer]::Ordinal)
    foreach ($reviewedRule in $rules) {
        $alias = [string] $reviewedRule['alias']
        $canonical = [string] $reviewedRule['canonical']
        $existing = $null
        if ($targetsByAlias.TryGetValue($alias, [ref] $existing) -and
            -not [string]::Equals(
                $existing,
                $canonical,
                [StringComparison]::Ordinal)) {
            throw "Reviewed model alias '$alias' has conflicting local targets."
        }
        $targetsByAlias[$alias] = $canonical
    }

    return [ordered] @{
        path = $resolvedPath
        catalogVersion = [string] $document.catalogVersion
        rules = @($rules)
    }
}

function Get-NextCatalogVersion {
    param([AllowNull()] [System.Collections.IDictionary] $CurrentCatalog)

    $date = [DateTime]::UtcNow.ToString('yyyy-MM-dd')
    $revision = 1
    if ($null -ne $CurrentCatalog) {
        $currentVersion = [string] $CurrentCatalog.catalogVersion
        $pattern = '^market-models-{0}-r(?<revision>[0-9]+)$' -f
            [Regex]::Escape($date)
        if ($currentVersion -match $pattern) {
            $revision = [int] $Matches.revision + 1
        }
    }

    return "market-models-$date-r$revision"
}

function Get-AliasSources {
    param(
        [AllowNull()] [System.Collections.IDictionary] $Catalog,
        [Parameter(Mandatory)] [string] $Alias
    )

    if ($null -eq $Catalog -or
        -not $Catalog.Contains('aliasSources') -or
        $null -eq $Catalog.aliasSources -or
        -not $Catalog.aliasSources.Contains($Alias)) {
        return @()
    }

    return @($Catalog.aliasSources[$Alias] |
        ForEach-Object { [string] $_ } |
        Sort-Object -Unique)
}

function Get-CanonicalModels {
    param([Parameter(Mandatory)] [System.Collections.IDictionary] $Aliases)

    $models = [System.Collections.Generic.HashSet[string]]::new(
        [System.StringComparer]::Ordinal)
    foreach ($canonical in $Aliases.Values) {
        [void] $models.Add([string] $canonical)
    }
    return ,$models
}

function Get-SourceSnapshots {
    param([AllowNull()] [System.Collections.IDictionary] $Catalog)

    $snapshots = [ordered] @{}
    if ($null -eq $Catalog -or
        -not $Catalog.Contains('dataSources') -or
        $null -eq $Catalog.dataSources) {
        return $snapshots
    }

    foreach ($source in @($Catalog.dataSources)) {
        $sourceId = [string] $source.id
        foreach ($artifact in @($source.artifacts)) {
            $key = "$sourceId/$([string] $artifact.name)"
            $snapshots[$key] = [ordered] @{
                sourceId = $sourceId
                artifact = [string] $artifact.name
                uri = [string] $source.uri
                entryCount = [int] $source.entryCount
                sha256 = [string] $artifact.sha256
            }
        }
    }
    return $snapshots
}

function Get-ReferenceSnapshots {
    param([AllowNull()] [System.Collections.IDictionary] $Catalog)

    $snapshots = [ordered] @{}
    if ($null -eq $Catalog -or
        -not $Catalog.Contains('referenceProjects') -or
        $null -eq $Catalog.referenceProjects) {
        return $snapshots
    }

    foreach ($reference in @($Catalog.referenceProjects)) {
        $id = [string] $reference.id
        $snapshots[$id] = [ordered] @{
            id = $id
            version = [string] $reference.version
            uri = [string] $reference.uri
            role = [string] $reference.role
        }
    }
    return $snapshots
}

function Test-EquivalentObject {
    param(
        [AllowNull()] $Left,
        [AllowNull()] $Right
    )

    if ($null -eq $Left -or $null -eq $Right) {
        return $null -eq $Left -and $null -eq $Right
    }
    $leftJson = $Left | ConvertTo-Json -Depth 8 -Compress
    $rightJson = $Right | ConvertTo-Json -Depth 8 -Compress
    return [string]::Equals($leftJson, $rightJson, [StringComparison]::Ordinal)
}

New-Item -ItemType Directory -Force -Path $maintenanceRoot | Out-Null
$resolvedCurrentCatalogPath = if (
    [string]::IsNullOrWhiteSpace($CurrentCatalogPath)) {
    $sourcePath
}
else {
    (Resolve-Path -LiteralPath $CurrentCatalogPath).Path
}
$currentCatalog = Read-CatalogDocument -Path $resolvedCurrentCatalogPath
$resolvedReviewedAliasesPath = if (
    [string]::IsNullOrWhiteSpace($ReviewedAliasesPath)) {
    $reviewedSourcePath
}
else {
    (Resolve-Path -LiteralPath $ReviewedAliasesPath).Path
}
$reviewedAliasCatalog = Read-ReviewedAliasDocument `
    -Path $resolvedReviewedAliasesPath
$resolvedModelsPath = Get-OrDownloadJson `
    -ExistingPath $ModelsPath `
    -Uri 'https://models.dev/models.json' `
    -DefaultName 'models.dev-models.json'
$resolvedProviderCatalogPath = Get-OrDownloadJson `
    -ExistingPath $ProviderCatalogPath `
    -Uri 'https://models.dev/catalog.json' `
    -DefaultName 'models.dev-catalog.json'
$resolvedLiteLlmCatalogPath = Get-OrDownloadJson `
    -ExistingPath $LiteLlmCatalogPath `
    -Uri 'https://raw.githubusercontent.com/BerriAI/litellm/main/model_prices_and_context_window.json' `
    -DefaultName 'litellm-model-prices.json'

if ([string]::IsNullOrWhiteSpace($CatalogVersion)) {
    $CatalogVersion = Get-NextCatalogVersion -CurrentCatalog $currentCatalog
}
if ($CatalogVersion -cnotmatch '^[a-z0-9][a-z0-9._-]{0,127}$') {
    throw 'CatalogVersion must be a lowercase stable identifier.'
}

$models = Get-Content -Raw -LiteralPath $resolvedModelsPath |
    ConvertFrom-Json -AsHashtable
$providerCatalog = Get-Content -Raw -LiteralPath $resolvedProviderCatalogPath |
    ConvertFrom-Json -AsHashtable
$liteLlmCatalog = Get-Content -Raw -LiteralPath $resolvedLiteLlmCatalogPath |
    ConvertFrom-Json -AsHashtable
if ($models.Count -eq 0) {
    throw 'models.dev returned an empty base-model catalog.'
}
if (-not $providerCatalog.ContainsKey('providers')) {
    throw 'models.dev provider catalog is missing the providers object.'
}
if ($liteLlmCatalog.Count -eq 0) {
    throw 'LiteLLM returned an empty model catalog.'
}

$canonicalByQualified =
    [System.Collections.Generic.Dictionary[string, string]]::new(
        [System.StringComparer]::Ordinal)
$canonicalModels =
    [System.Collections.Generic.HashSet[string]]::new(
        [System.StringComparer]::Ordinal)
$aliasTargets =
    [System.Collections.Generic.Dictionary[string, System.Collections.Generic.HashSet[string]]]::new(
        [System.StringComparer]::Ordinal)
$aliasEvidence =
    [System.Collections.Generic.Dictionary[string, System.Collections.Generic.HashSet[string]]]::new(
        [System.StringComparer]::Ordinal)

foreach ($qualifiedValue in ($models.Keys | Sort-Object)) {
    $qualified = Normalize-Identifier ([string] $qualifiedValue)
    $separator = $qualified.IndexOf('/')
    if ($separator -le 0 -or $separator -eq $qualified.Length - 1) {
        throw "Invalid provider-agnostic model identifier: $qualified"
    }

    $canonical = $qualified.Substring($separator + 1)
    if ($canonical.Contains('/')) {
        throw "Provider-agnostic model identifier has multiple namespaces: $qualified"
    }
    if ($canonicalByQualified.ContainsKey($qualified)) {
        throw "Duplicate qualified model identifier: $qualified"
    }

    $canonicalByQualified.Add($qualified, $canonical)
    [void] $canonicalModels.Add($canonical)
    Add-AliasTarget `
        $aliasTargets $aliasEvidence $canonical $canonical 'models.dev'
    Add-AliasTarget `
        $aliasTargets $aliasEvidence $qualified $canonical 'models.dev'
}

foreach ($providerEntry in $providerCatalog.providers.GetEnumerator()) {
    $provider = $providerEntry.Value
    if ($null -eq $provider -or -not $provider.ContainsKey('models')) {
        continue
    }

    $providerId = if ($provider.ContainsKey('id')) {
        Normalize-Identifier ([string] $provider.id)
    }
    else {
        Normalize-Identifier ([string] $providerEntry.Key)
    }

    foreach ($modelEntry in $provider.models.GetEnumerator()) {
        $candidates = [System.Collections.Generic.HashSet[string]]::new(
            [System.StringComparer]::Ordinal)
        [void] $candidates.Add(
            (Normalize-Identifier ([string] $modelEntry.Key)))
        if ($modelEntry.Value.ContainsKey('id')) {
            [void] $candidates.Add(
                (Normalize-Identifier ([string] $modelEntry.Value.id)))
        }

        foreach ($candidate in $candidates) {
            $canonical = $null
            if (-not $canonicalByQualified.TryGetValue(
                    $candidate,
                    [ref] $canonical) -and
                -not $canonicalModels.Contains($candidate)) {
                continue
            }
            if ($null -eq $canonical) {
                $canonical = $candidate
            }

            Add-AliasTarget `
                $aliasTargets `
                $aliasEvidence `
                $candidate `
                $canonical `
                'models.dev'
            if (-not $candidate.StartsWith(
                    "$providerId/",
                    [System.StringComparison]::Ordinal)) {
                Add-AliasTarget `
                    $aliasTargets `
                    $aliasEvidence `
                    "$providerId/$candidate" `
                    $canonical `
                    'models.dev'
            }
        }
    }
}

# LiteLLM is a second independent catalog. It contains both bare model IDs and
# provider/router-qualified forms. AgenTally accepts a qualified form only when
# its final path segment exactly equals a known bare canonical ID. No fuzzy,
# version-suffix, pricing or provider-family inference is allowed here.
$liteLlmKeys = [System.Collections.Generic.HashSet[string]]::new(
    [System.StringComparer]::Ordinal)
foreach ($entry in $liteLlmCatalog.GetEnumerator()) {
    $metadata = $entry.Value
    if ($null -eq $metadata -or
        -not $metadata.ContainsKey('litellm_provider') -or
        [string]::IsNullOrWhiteSpace([string] $metadata.litellm_provider)) {
        continue
    }

    $key = Normalize-Identifier ([string] $entry.Key)
    if ([string]::IsNullOrWhiteSpace($key)) {
        throw 'LiteLLM contains an empty model identifier.'
    }
    [void] $liteLlmKeys.Add($key)
}
if ($liteLlmKeys.Count -eq 0) {
    throw 'LiteLLM catalog contains no valid model entries.'
}

foreach ($key in ($liteLlmKeys |
        Where-Object { -not $_.Contains('/') } |
        Sort-Object)) {
    [void] $canonicalModels.Add($key)
    Add-AliasTarget $aliasTargets $aliasEvidence $key $key 'litellm'
}

$unmappedLiteLlmQualified = [System.Collections.Generic.List[string]]::new()
foreach ($key in ($liteLlmKeys |
        Where-Object { $_.Contains('/') } |
        Sort-Object)) {
    $terminal = $key.Substring($key.LastIndexOf('/') + 1)
    if (-not [string]::IsNullOrWhiteSpace($terminal) -and
        $canonicalModels.Contains($terminal)) {
        Add-AliasTarget `
            $aliasTargets $aliasEvidence $key $terminal 'litellm'
        continue
    }

    $unmappedLiteLlmQualified.Add($key)
}

$resolvedAliases = [ordered] @{}
$resolvedAliasSources = [ordered] @{}
$conflicts = [System.Collections.Generic.List[object]]::new()
$corroboratedAliasCount = 0
foreach ($alias in ($aliasTargets.Keys | Sort-Object)) {
    $targets = @($aliasTargets[$alias] | Sort-Object)
    $sources = @($aliasEvidence[$alias] | Sort-Object)
    if ($targets.Count -ne 1) {
        $conflicts.Add([pscustomobject]@{
            Alias = $alias
            Targets = ($targets -join ', ')
            Sources = ($sources -join ', ')
        })
        continue
    }

    $resolvedAliases[$alias] = $targets[0]
    $resolvedAliasSources[$alias] = $sources
    if ($sources.Count -gt 1) {
        $corroboratedAliasCount++
    }
}

$reviewedAliasConflicts = [System.Collections.Generic.List[object]]::new()
foreach ($reviewedRule in @($reviewedAliasCatalog.rules |
        Sort-Object scope, alias)) {
    $alias = [string] $reviewedRule.alias
    if (-not $resolvedAliases.Contains($alias)) {
        continue
    }

    $marketCanonical = [string] $resolvedAliases[$alias]
    $reviewedCanonical = [string] $reviewedRule.canonical
    if ([string]::Equals(
            $marketCanonical,
            $reviewedCanonical,
            [StringComparison]::Ordinal)) {
        continue
    }

    $reviewedAliasConflicts.Add([ordered] @{
        scope = [string] $reviewedRule.scope
        alias = [string] $alias
        marketCanonical = $marketCanonical
        reviewedCanonical = $reviewedCanonical
        marketSources = @($resolvedAliasSources[$alias])
    })
}

$resolvedCanonicalModels = [System.Collections.Generic.HashSet[string]]::new(
    [System.StringComparer]::Ordinal)
foreach ($canonical in $resolvedAliases.Values) {
    [void] $resolvedCanonicalModels.Add([string] $canonical)
}

$modelsHash = (Get-FileHash -LiteralPath $resolvedModelsPath -Algorithm SHA256).Hash
$providerCatalogHash =
    (Get-FileHash -LiteralPath $resolvedProviderCatalogPath -Algorithm SHA256).Hash
$liteLlmCatalogHash =
    (Get-FileHash -LiteralPath $resolvedLiteLlmCatalogPath -Algorithm SHA256).Hash
$reviewedAliasesHash =
    (Get-FileHash -LiteralPath $resolvedReviewedAliasesPath -Algorithm SHA256).Hash
$dataSources = @(
    [ordered] @{
        id = 'models.dev'
        uri = 'https://models.dev/'
        entryCount = $models.Count
        artifacts = @(
            [ordered] @{
                name = 'models.json'
                sha256 = $modelsHash
            },
            [ordered] @{
                name = 'catalog.json'
                sha256 = $providerCatalogHash
            }
        )
    },
    [ordered] @{
        id = 'litellm'
        uri = 'https://github.com/BerriAI/litellm/blob/main/model_prices_and_context_window.json'
        entryCount = $liteLlmKeys.Count
        artifacts = @(
            [ordered] @{
                name = 'model_prices_and_context_window.json'
                sha256 = $liteLlmCatalogHash
            }
        )
    }
)
$referenceProjects = @(
    [ordered] @{
        id = 'ccusage'
        version = 'v20.0.19@caf89e8c'
        uri = 'https://github.com/ccusage/ccusage/tree/v20.0.19'
        role = 'matching-strategy-reference-only'
    },
    [ordered] @{
        id = 'tokscale'
        version = 'v4.8.1@45b3b3e4'
        uri = 'https://github.com/junhoyeo/tokscale/tree/v4.8.1'
        role = 'matching-strategy-reference-only'
    }
)
$snapshot = [ordered] @{
    schemaVersion = 2
    catalogVersion = $CatalogVersion
    dataSources = $dataSources
    referenceProjects = $referenceProjects
    modelCount = $resolvedCanonicalModels.Count
    aliasCount = $resolvedAliases.Count
    corroboratedAliasCount = $corroboratedAliasCount
    singleSourceAliasCount = $resolvedAliases.Count - $corroboratedAliasCount
    omittedConflictCount = $conflicts.Count
    omittedUnmappedQualifiedCount = $unmappedLiteLlmQualified.Count
    aliases = $resolvedAliases
    aliasSources = $resolvedAliasSources
}

$currentAliases = if ($null -eq $currentCatalog) {
    [ordered] @{}
}
else {
    $currentCatalog.aliases
}
$allAliases = [System.Collections.Generic.HashSet[string]]::new(
    [System.StringComparer]::Ordinal)
foreach ($alias in $currentAliases.Keys) {
    [void] $allAliases.Add([string] $alias)
}
foreach ($alias in $resolvedAliases.Keys) {
    [void] $allAliases.Add([string] $alias)
}

$addedAliases = [System.Collections.Generic.List[object]]::new()
$removedAliases = [System.Collections.Generic.List[object]]::new()
$retargetedAliases = [System.Collections.Generic.List[object]]::new()
$evidenceChanges = [System.Collections.Generic.List[object]]::new()
foreach ($alias in ($allAliases | Sort-Object)) {
    $hasCurrent = $currentAliases.Contains($alias)
    $hasCandidate = $resolvedAliases.Contains($alias)
    $currentSources = @(Get-AliasSources $currentCatalog $alias)
    $candidateSources = @(Get-AliasSources $snapshot $alias)
    if (-not $hasCurrent) {
        $addedAliases.Add([ordered] @{
            alias = $alias
            canonical = [string] $resolvedAliases[$alias]
            sources = $candidateSources
        })
        continue
    }
    if (-not $hasCandidate) {
        $removedAliases.Add([ordered] @{
            alias = $alias
            canonical = [string] $currentAliases[$alias]
            sources = $currentSources
        })
        continue
    }

    $currentCanonical = [string] $currentAliases[$alias]
    $candidateCanonical = [string] $resolvedAliases[$alias]
    if (-not [string]::Equals(
            $currentCanonical,
            $candidateCanonical,
            [StringComparison]::Ordinal)) {
        $retargetedAliases.Add([ordered] @{
            alias = $alias
            previousCanonical = $currentCanonical
            candidateCanonical = $candidateCanonical
            previousSources = $currentSources
            candidateSources = $candidateSources
        })
        continue
    }

    if (-not [string]::Equals(
            ($currentSources -join "`0"),
            ($candidateSources -join "`0"),
            [StringComparison]::Ordinal)) {
        $evidenceChanges.Add([ordered] @{
            alias = $alias
            canonical = $candidateCanonical
            previousSources = $currentSources
            candidateSources = $candidateSources
        })
    }
}

$currentModels = Get-CanonicalModels $currentAliases
$candidateModels = Get-CanonicalModels $resolvedAliases
$addedModels = @($candidateModels |
    Where-Object { -not $currentModels.Contains($_) } |
    Sort-Object)
$removedModels = @($currentModels |
    Where-Object { -not $candidateModels.Contains($_) } |
    Sort-Object)

$currentSourceSnapshots = Get-SourceSnapshots $currentCatalog
$candidateSourceSnapshots = Get-SourceSnapshots $snapshot
$allSourceSnapshotKeys = [System.Collections.Generic.HashSet[string]]::new(
    [System.StringComparer]::Ordinal)
foreach ($key in $currentSourceSnapshots.Keys) {
    [void] $allSourceSnapshotKeys.Add([string] $key)
}
foreach ($key in $candidateSourceSnapshots.Keys) {
    [void] $allSourceSnapshotKeys.Add([string] $key)
}
$sourceSnapshotChanges = [System.Collections.Generic.List[object]]::new()
foreach ($key in ($allSourceSnapshotKeys | Sort-Object)) {
    $previous = if ($currentSourceSnapshots.Contains($key)) {
        $currentSourceSnapshots[$key]
    }
    else {
        $null
    }
    $candidate = if ($candidateSourceSnapshots.Contains($key)) {
        $candidateSourceSnapshots[$key]
    }
    else {
        $null
    }
    if (-not (Test-EquivalentObject $previous $candidate)) {
        $sourceSnapshotChanges.Add([ordered] @{
            key = $key
            previous = $previous
            candidate = $candidate
        })
    }
}

$currentReferenceSnapshots = Get-ReferenceSnapshots $currentCatalog
$candidateReferenceSnapshots = Get-ReferenceSnapshots $snapshot
$allReferenceKeys = [System.Collections.Generic.HashSet[string]]::new(
    [System.StringComparer]::Ordinal)
foreach ($key in $currentReferenceSnapshots.Keys) {
    [void] $allReferenceKeys.Add([string] $key)
}
foreach ($key in $candidateReferenceSnapshots.Keys) {
    [void] $allReferenceKeys.Add([string] $key)
}
$referenceChanges = [System.Collections.Generic.List[object]]::new()
foreach ($key in ($allReferenceKeys | Sort-Object)) {
    $previous = if ($currentReferenceSnapshots.Contains($key)) {
        $currentReferenceSnapshots[$key]
    }
    else {
        $null
    }
    $candidate = if ($candidateReferenceSnapshots.Contains($key)) {
        $candidateReferenceSnapshots[$key]
    }
    else {
        $null
    }
    if (-not (Test-EquivalentObject $previous $candidate)) {
        $referenceChanges.Add([ordered] @{
            id = $key
            previous = $previous
            candidate = $candidate
        })
    }
}

$hasIdentityChanges =
    $addedAliases.Count -gt 0 -or
    $removedAliases.Count -gt 0 -or
    $retargetedAliases.Count -gt 0
$hasChanges =
    $hasIdentityChanges -or
    $evidenceChanges.Count -gt 0 -or
    $sourceSnapshotChanges.Count -gt 0 -or
    $referenceChanges.Count -gt 0
$hasBreakingChanges =
    $removedAliases.Count -gt 0 -or
    $retargetedAliases.Count -gt 0
$diff = [ordered] @{
    schemaVersion = 1
    previousCatalogVersion = if ($null -eq $currentCatalog) {
        $null
    }
    else {
        [string] $currentCatalog.catalogVersion
    }
    candidateCatalogVersion = $CatalogVersion
    hasChanges = $hasChanges
    hasIdentityChanges = $hasIdentityChanges
    hasBreakingChanges = $hasBreakingChanges
    reviewedAliasOverlay = [ordered] @{
        catalogVersion = [string] $reviewedAliasCatalog.catalogVersion
        ruleCount = @($reviewedAliasCatalog.rules).Count
        sha256 = $reviewedAliasesHash
    }
    counts = [ordered] @{
        addedModels = $addedModels.Count
        removedModels = $removedModels.Count
        addedAliases = $addedAliases.Count
        removedAliases = $removedAliases.Count
        retargetedAliases = $retargetedAliases.Count
        evidenceChanges = $evidenceChanges.Count
        sourceSnapshotChanges = $sourceSnapshotChanges.Count
        referenceChanges = $referenceChanges.Count
        candidateConflicts = $conflicts.Count
        reviewedAliasConflicts = $reviewedAliasConflicts.Count
        candidateUnmappedQualified = $unmappedLiteLlmQualified.Count
    }
    addedModels = $addedModels
    removedModels = $removedModels
    addedAliases = $addedAliases
    removedAliases = $removedAliases
    retargetedAliases = $retargetedAliases
    evidenceChanges = $evidenceChanges
    sourceSnapshotChanges = $sourceSnapshotChanges
    referenceChanges = $referenceChanges
    reviewedAliasConflicts = $reviewedAliasConflicts
}
$json = $snapshot | ConvertTo-Json -Depth 8
$json = $json.Replace("`r`n", "`n") + "`n"
$json | Set-Content -LiteralPath $candidatePath -Encoding utf8NoBOM -NoNewline
$diffJson = $diff | ConvertTo-Json -Depth 10
$diffJson = $diffJson.Replace("`r`n", "`n") + "`n"
$diffJson | Set-Content -LiteralPath $diffPath -Encoding utf8NoBOM -NoNewline

$report = [System.Collections.Generic.List[string]]::new()
$report.Add('# AgenTally model identity catalog candidate')
$report.Add('')
$report.Add("- Previous catalog version: ``$(if ($null -eq $currentCatalog) { '<none>' } else { [string] $currentCatalog.catalogVersion })``")
$report.Add("- Catalog version: ``$CatalogVersion``")
$report.Add("- Has changes: ``$($hasChanges.ToString().ToLowerInvariant())``")
$report.Add("- Has identity changes: ``$($hasIdentityChanges.ToString().ToLowerInvariant())``")
$report.Add("- Has breaking changes: ``$($hasBreakingChanges.ToString().ToLowerInvariant())``")
$report.Add('- Independent data catalogs: 2 (`models.dev`, `LiteLLM`)')
$report.Add('- Matching references: `ccusage v20.0.19`, `tokscale v4.8.1`')
$report.Add("- Reviewed alias overlay: ``$($reviewedAliasCatalog.catalogVersion)`` ($(@($reviewedAliasCatalog.rules).Count) rules)")
$report.Add("- Reviewed alias overlay conflicts: $($reviewedAliasConflicts.Count)")
$report.Add("- Canonical models: $($resolvedCanonicalModels.Count)")
$report.Add("- Unambiguous exact aliases: $($resolvedAliases.Count)")
$report.Add("- Cross-catalog corroborated aliases: $corroboratedAliasCount")
$report.Add("- Single-catalog exact aliases: $($resolvedAliases.Count - $corroboratedAliasCount)")
$report.Add("- Omitted conflicting aliases: $($conflicts.Count)")
$report.Add("- Omitted LiteLLM qualified IDs without an exact canonical tail: $($unmappedLiteLlmQualified.Count)")
$report.Add("- Added canonical models: $($addedModels.Count)")
$report.Add("- Removed canonical models: $($removedModels.Count)")
$report.Add("- Added aliases: $($addedAliases.Count)")
$report.Add("- Removed aliases: $($removedAliases.Count)")
$report.Add("- Retargeted aliases: $($retargetedAliases.Count)")
$report.Add("- Alias evidence changes: $($evidenceChanges.Count)")
$report.Add("- Source snapshot changes: $($sourceSnapshotChanges.Count)")
$report.Add("- Reference project changes: $($referenceChanges.Count)")
$report.Add("- models.json SHA-256: ``$modelsHash``")
$report.Add("- catalog.json SHA-256: ``$providerCatalogHash``")
$report.Add("- LiteLLM catalog SHA-256: ``$liteLlmCatalogHash``")
$report.Add("- Reviewed alias overlay SHA-256: ``$reviewedAliasesHash``")
$report.Add('')
$report.Add('The generator accepts only exact full IDs and exact terminal-ID equality. It does not import third-party prices, fuzzy matching, tier stripping, date stripping, or family inference.')
if ($addedModels.Count -gt 0) {
    $report.Add('')
    $report.Add('## Added canonical models')
    $report.Add('')
    foreach ($model in $addedModels) {
        $report.Add("- ``$model``")
    }
}
if ($removedModels.Count -gt 0) {
    $report.Add('')
    $report.Add('## Removed canonical models')
    $report.Add('')
    foreach ($model in $removedModels) {
        $report.Add("- ``$model``")
    }
}
if ($addedAliases.Count -gt 0) {
    $report.Add('')
    $report.Add('## Added aliases')
    $report.Add('')
    foreach ($change in $addedAliases) {
        $report.Add("- ``$($change.alias)`` -> ``$($change.canonical)`` [$($change.sources -join ', ')]")
    }
}
if ($removedAliases.Count -gt 0) {
    $report.Add('')
    $report.Add('## Removed aliases')
    $report.Add('')
    foreach ($change in $removedAliases) {
        $report.Add("- ``$($change.alias)`` -> ``$($change.canonical)`` [$($change.sources -join ', ')]")
    }
}
if ($retargetedAliases.Count -gt 0) {
    $report.Add('')
    $report.Add('## Retargeted aliases')
    $report.Add('')
    foreach ($change in $retargetedAliases) {
        $report.Add("- ``$($change.alias)``: ``$($change.previousCanonical)`` -> ``$($change.candidateCanonical)``; sources ``$($change.previousSources -join ', ')`` -> ``$($change.candidateSources -join ', ')``")
    }
}
if ($evidenceChanges.Count -gt 0) {
    $report.Add('')
    $report.Add('## Alias evidence changes')
    $report.Add('')
    foreach ($change in $evidenceChanges) {
        $report.Add("- ``$($change.alias)`` -> ``$($change.canonical)``; sources ``$($change.previousSources -join ', ')`` -> ``$($change.candidateSources -join ', ')``")
    }
}
if ($sourceSnapshotChanges.Count -gt 0) {
    $report.Add('')
    $report.Add('## Source snapshot changes')
    $report.Add('')
    foreach ($change in $sourceSnapshotChanges) {
        $previousHash = if ($null -eq $change.previous) {
            '<missing>'
        }
        else {
            [string] $change.previous.sha256
        }
        $candidateHash = if ($null -eq $change.candidate) {
            '<missing>'
        }
        else {
            [string] $change.candidate.sha256
        }
        $previousCount = if ($null -eq $change.previous) {
            '<missing>'
        }
        else {
            [string] $change.previous.entryCount
        }
        $candidateCount = if ($null -eq $change.candidate) {
            '<missing>'
        }
        else {
            [string] $change.candidate.entryCount
        }
        $report.Add("- ``$($change.key)``: SHA-256 ``$previousHash`` -> ``$candidateHash``; entries ``$previousCount`` -> ``$candidateCount``")
    }
}
if ($referenceChanges.Count -gt 0) {
    $report.Add('')
    $report.Add('## Reference project changes')
    $report.Add('')
    foreach ($change in $referenceChanges) {
        $previousVersion = if ($null -eq $change.previous) {
            '<missing>'
        }
        else {
            [string] $change.previous.version
        }
        $candidateVersion = if ($null -eq $change.candidate) {
            '<missing>'
        }
        else {
            [string] $change.candidate.version
        }
        $report.Add("- ``$($change.id)``: ``$previousVersion`` -> ``$candidateVersion``")
    }
}
if ($conflicts.Count -gt 0) {
    $report.Add('')
    $report.Add('## Omitted conflicting aliases')
    $report.Add('')
    foreach ($conflict in $conflicts) {
        $report.Add("- ``$($conflict.Alias)`` -> $($conflict.Targets) [$($conflict.Sources)]")
    }
}
if ($reviewedAliasConflicts.Count -gt 0) {
    $report.Add('')
    $report.Add('## Reviewed alias overlay conflicts')
    $report.Add('')
    $report.Add('Publishing is blocked until every reviewed alias and generated market alias have the same canonical target.')
    $report.Add('')
    foreach ($conflict in $reviewedAliasConflicts) {
        $report.Add("- ``$($conflict.scope)`` / ``$($conflict.alias)``: reviewed ``$($conflict.reviewedCanonical)``; market ``$($conflict.marketCanonical)`` [$($conflict.marketSources -join ', ')]")
    }
}
if ($unmappedLiteLlmQualified.Count -gt 0) {
    $report.Add('')
    $report.Add('## Sample omitted LiteLLM qualified IDs')
    $report.Add('')
    $report.Add('These IDs remain unchanged at runtime because no exact bare canonical target was proved.')
    $report.Add('')
    foreach ($alias in ($unmappedLiteLlmQualified | Select-Object -First 100)) {
        $report.Add("- ``$alias``")
    }
}
($report -join "`n") + "`n" |
    Set-Content -LiteralPath $reportPath -Encoding utf8NoBOM -NoNewline

$outputPath = $candidatePath
$applied = $false
$applyStatus = 'candidate-only'
if ($Apply) {
    $blockingReasons = [System.Collections.Generic.List[string]]::new()
    if ($conflicts.Count -gt 0) {
        $blockingReasons.Add(
            "$($conflicts.Count) conflicting aliases were omitted")
    }
    if ($reviewedAliasConflicts.Count -gt 0) {
        $blockingReasons.Add(
            "$($reviewedAliasConflicts.Count) generated aliases conflict with the reviewed overlay")
    }
    if ($removedAliases.Count -gt 0 -and -not $AllowRemovals) {
        $blockingReasons.Add(
            "$($removedAliases.Count) aliases would be removed; pass -AllowRemovals after review")
    }
    if ($retargetedAliases.Count -gt 0 -and -not $AllowRetargeting) {
        $blockingReasons.Add(
            "$($retargetedAliases.Count) aliases would change canonical target; pass -AllowRetargeting after review")
    }
    if ($hasChanges -and
        $null -ne $currentCatalog -and
        [string]::Equals(
            [string] $currentCatalog.catalogVersion,
            $CatalogVersion,
            [StringComparison]::Ordinal)) {
        $blockingReasons.Add(
            'the candidate changes catalog content without changing catalogVersion')
    }
    if (-not [string]::Equals(
            [IO.Path]::GetFullPath($resolvedCurrentCatalogPath),
            [IO.Path]::GetFullPath($sourcePath),
            [StringComparison]::OrdinalIgnoreCase)) {
        $blockingReasons.Add(
            'Apply requires comparing against the currently shipped catalog')
    }
    if ($blockingReasons.Count -gt 0) {
        throw (
            'Catalog apply is blocked: ' +
            ($blockingReasons -join '; ') +
            ". Review $reportPath and $diffPath.")
    }

    if (-not $hasChanges) {
        $applyStatus = 'no-changes'
    }
    else {
        $sourceDirectory = Split-Path -Parent $sourcePath
        New-Item -ItemType Directory -Force -Path $sourceDirectory | Out-Null
        if ($PSCmdlet.ShouldProcess($sourcePath, 'Replace the shipped identity catalog')) {
            Copy-Item -LiteralPath $candidatePath -Destination $sourcePath -Force
            $outputPath = $sourcePath
            $applied = $true
            $applyStatus = 'applied'
        }
        else {
            $applyStatus = 'what-if'
        }
    }
}

[pscustomobject]@{
    CatalogVersion = $CatalogVersion
    DataSourceCount = $dataSources.Count
    ReferenceProjectCount = $referenceProjects.Count
    ModelCount = $resolvedCanonicalModels.Count
    AliasCount = $resolvedAliases.Count
    CorroboratedAliasCount = $corroboratedAliasCount
    HasChanges = $hasChanges
    HasBreakingChanges = $hasBreakingChanges
    AddedModelCount = $addedModels.Count
    RemovedModelCount = $removedModels.Count
    AddedAliasCount = $addedAliases.Count
    RemovedAliasCount = $removedAliases.Count
    RetargetedAliasCount = $retargetedAliases.Count
    EvidenceChangeCount = $evidenceChanges.Count
    SourceSnapshotChangeCount = $sourceSnapshotChanges.Count
    ReferenceChangeCount = $referenceChanges.Count
    ReviewedAliasCatalogVersion = [string] $reviewedAliasCatalog.catalogVersion
    ReviewedAliasCount = @($reviewedAliasCatalog.rules).Count
    ReviewedAliasConflictCount = $reviewedAliasConflicts.Count
    ConflictCount = $conflicts.Count + $reviewedAliasConflicts.Count
    UnmappedQualifiedCount = $unmappedLiteLlmQualified.Count
    Applied = $applied
    ApplyStatus = $applyStatus
    OutputPath = $outputPath
    DiffPath = $diffPath
    ReportPath = $reportPath
}
