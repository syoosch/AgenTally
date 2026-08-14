# Copyright (c) AgenTally contributors.
# Developer-only maintenance command. The shipped application never invokes it.
<#
.SYNOPSIS
Audits and updates AgenTally's complete offline token-price catalog stack.

.DESCRIPTION
Reads or downloads models.dev and LiteLLM catalogs, keeps only official-provider
token pricing, resolves source identifiers through AgenTally's shipped model
identity catalog, and generates a deterministic offline fallback snapshot.
It also validates and diffs a proposed AgenTally-maintained official-price
catalog and reviewed price-alias catalog against their shipped baselines. The
single machine diff and Markdown report cover all three layers, including exact
maintained-versus-upstream disagreements. models.dev wins over LiteLLM when both
upstream sources price the same canonical model; maintained prices remain higher
priority at runtime. Nothing is published unless -Apply is supplied.

Rate changes and removals require separate explicit approval. The shipped
application never downloads or invokes either upstream catalog.

.PARAMETER ProviderCatalogPath
Optional local models.dev catalog.json. When omitted, it is downloaded into the
ignored Development maintenance directory.

.PARAMETER LiteLlmCatalogPath
Optional local LiteLLM model_prices_and_context_window.json. When omitted, it is
downloaded into the ignored Development maintenance directory.

.PARAMETER IdentityCatalogPath
Optional AgenTally market-model identity catalog. Defaults to the shipped file.

.PARAMETER MaintainedCatalogPath
Optional proposed AgenTally-maintained price catalog. Defaults to the shipped
file, so an ordinary upstream-only refresh leaves this layer unchanged.

.PARAMETER CurrentMaintainedCatalogPath
Optional comparison baseline for the maintained catalog. Defaults to the
shipped file. An alternate baseline is review-only and cannot be applied.

.PARAMETER MaintainedCatalogVersion
Candidate maintained-catalog version. When omitted, the script retains the
current version for a no-op or proposes the next UTC-date rN revision.

.PARAMETER ReviewedPriceAliasCatalogPath
Optional proposed reviewed price-alias catalog. Defaults to the shipped file.

.PARAMETER CurrentReviewedPriceAliasCatalogPath
Optional comparison baseline for reviewed price aliases. Defaults to the
shipped file. An alternate baseline is review-only and cannot be applied.

.PARAMETER ReviewedPriceAliasCatalogVersion
Candidate price-alias catalog version. When omitted, the script retains the
current version for a no-op or proposes the next UTC-date rN revision.

.PARAMETER CurrentCatalogPath
Optional comparison baseline. Defaults to the shipped upstream snapshot.

.PARAMETER CatalogVersion
Candidate version. When omitted, the script retains the current version for a
no-op or proposes the next UTC-date rN revision for changed content.

.PARAMETER AllowRateChanges
Allows changed prices or selected sources during -Apply.

.PARAMETER AllowRemovals
Allows upstream or maintained model-price removals during -Apply.

.PARAMETER AllowMaintainedChanges
Allows changed maintained rules, including rates, model membership or official
evidence, and changed price-alias evidence during -Apply. Additive rules and
aliases do not require this switch.

.PARAMETER AllowAliasRemovals
Allows reviewed price-alias removals during -Apply.

.PARAMETER AllowAliasRetargeting
Allows reviewed price aliases to change their price target during -Apply.

.PARAMETER Apply
Replaces the shipped upstream snapshot after all review gates pass.

.EXAMPLE
.\scripts\Update-AgenTallyPriceCatalog.ps1

Generates a candidate, JSON diff and Markdown review report without publishing.

.EXAMPLE
.\scripts\Update-AgenTallyPriceCatalog.ps1 -AllowRateChanges -Apply

Publishes a reviewed candidate that changes existing upstream prices.

.EXAMPLE
.\scripts\Update-AgenTallyPriceCatalog.ps1 `
    -MaintainedCatalogPath .\artifacts\development\proposed-official-prices.json `
    -ReviewedPriceAliasCatalogPath .\artifacts\development\proposed-price-aliases.json

Audits proposed developer-maintained rules and aliases together with refreshed
upstream prices. Review the generated report before using -Apply and any
required approval switches. This command is never exposed in the application UI.
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [string] $ProviderCatalogPath,
    [string] $LiteLlmCatalogPath,
    [string] $IdentityCatalogPath,
    [string] $MaintainedCatalogPath,
    [string] $CurrentMaintainedCatalogPath,
    [string] $MaintainedCatalogVersion,
    [string] $ReviewedPriceAliasCatalogPath,
    [string] $CurrentReviewedPriceAliasCatalogPath,
    [string] $ReviewedPriceAliasCatalogVersion,
    [string] $CurrentCatalogPath,
    [string] $CatalogVersion,
    [switch] $AllowRateChanges,
    [switch] $AllowRemovals,
    [switch] $AllowMaintainedChanges,
    [switch] $AllowAliasRemovals,
    [switch] $AllowAliasRetargeting,
    [switch] $Apply
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$maintenanceRoot =
    Join-Path $repoRoot 'artifacts\development\price-catalog-maintenance'
$candidatePath = Join-Path $maintenanceRoot 'upstream-token-prices.candidate.json'
$maintainedCandidatePath =
    Join-Path $maintenanceRoot 'official-api-token-prices.candidate.json'
$reviewedPriceAliasCandidatePath =
    Join-Path $maintenanceRoot 'reviewed-price-aliases.candidate.json'
$diffPath = Join-Path $maintenanceRoot 'price-catalogs.diff.json'
$reportPath = Join-Path $maintenanceRoot 'price-catalogs.report.md'
$sourcePath =
    Join-Path $repoRoot 'src\AgenTally.Storage\Pricing\Catalog\upstream-token-prices.json'
$identitySourcePath =
    Join-Path $repoRoot 'src\AgenTally.Domain\Usage\Catalog\market-model-aliases.json'
$maintainedSourcePath =
    Join-Path $repoRoot 'src\AgenTally.Storage\Pricing\Catalog\official-api-token-prices.json'
$reviewedPriceAliasSourcePath =
    Join-Path $repoRoot 'src\AgenTally.Storage\Pricing\Catalog\reviewed-price-aliases.json'

$modelsDevUri = 'https://models.dev/catalog.json'
$liteLlmUri =
    'https://raw.githubusercontent.com/BerriAI/litellm/main/model_prices_and_context_window.json'
$selectionPolicy = 'official-provider-modelsdev-then-direct-litellm-v1'

function Normalize-Identifier {
    param([Parameter(Mandatory)] [string] $Value)

    return $Value.Trim().ToLowerInvariant()
}

function Get-OrDownloadJson {
    param(
        [string] $ExistingPath,
        [Parameter(Mandatory)] [string] $Uri,
        [Parameter(Mandatory)] [string] $DefaultName
    )

    if (-not [string]::IsNullOrWhiteSpace($ExistingPath)) {
        return (Resolve-Path -LiteralPath $ExistingPath).Path
    }

    $downloadPath = Join-Path $maintenanceRoot $DefaultName
    Invoke-WebRequest -Uri $Uri -OutFile $downloadPath
    return $downloadPath
}

function Read-JsonDocument {
    param(
        [Parameter(Mandatory)] [string] $Path,
        [switch] $Optional
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        if ($Optional) {
            return $null
        }
        throw "JSON file is missing: $Path"
    }

    return Get-Content -Raw -LiteralPath $Path |
        ConvertFrom-Json -AsHashtable
}

function Get-NextCatalogVersion {
    param([AllowNull()] [System.Collections.IDictionary] $CurrentCatalog)

    $date = [DateTime]::UtcNow.ToString('yyyy-MM-dd')
    $revision = 1
    if ($null -ne $CurrentCatalog) {
        $currentVersion = [string] $CurrentCatalog.catalogVersion
        $pattern = '^market-token-prices-{0}-r(?<revision>[0-9]+)$' -f
            [Regex]::Escape($date)
        if ($currentVersion -match $pattern) {
            $revision = [int] $Matches.revision + 1
        }
    }

    return "market-token-prices-$date-r$revision"
}

function Get-CanonicalModel {
    param(
        [Parameter(Mandatory)] [string] $SourceKey,
        [Parameter(Mandatory)] [System.Collections.IDictionary] $Aliases
    )

    $key = Normalize-Identifier $SourceKey
    if ($Aliases.Contains($key)) {
        return [string] $Aliases[$key]
    }

    $separator = $key.LastIndexOf('/')
    $terminal = if ($separator -ge 0) {
        $key.Substring($separator + 1)
    }
    else {
        $key
    }
    if ($Aliases.Contains($terminal)) {
        return [string] $Aliases[$terminal]
    }
    return $terminal
}

function Get-OptionalDecimal {
    param(
        [Parameter(Mandatory)] [System.Collections.IDictionary] $Data,
        [Parameter(Mandatory)] [string] $Name
    )

    if (-not $Data.Contains($Name) -or $null -eq $Data[$Name]) {
        return $null
    }
    return [decimal] $Data[$Name]
}

function Get-RateRatio {
    param(
        [decimal] $Base,
        [decimal] $Tier
    )

    if ($Base -eq 0) {
        if ($Tier -eq 0) {
            return [decimal] 1
        }
        return $null
    }
    $ratio = $Tier / $Base
    if ($ratio -ge 1 -and $ratio -le 100) {
        return $ratio
    }
    return $null
}

function Get-ModelsDevTier {
    param([Parameter(Mandatory)] [System.Collections.IDictionary] $Cost)

    if (-not $Cost.Contains('tiers')) {
        return [ordered] @{
            supported = $true
            threshold = $null
            inputMultiplier = [decimal] 1
            outputMultiplier = [decimal] 1
        }
    }

    $tiers = @($Cost.tiers)
    if ($tiers.Count -ne 1 -or
        -not $tiers[0].Contains('tier') -or
        -not $tiers[0].tier.Contains('type') -or
        -not [string]::Equals(
            [string] $tiers[0].tier.type,
            'context',
            [StringComparison]::Ordinal) -or
        -not $tiers[0].tier.Contains('size') -or
        -not $tiers[0].Contains('input') -or
        -not $tiers[0].Contains('output')) {
        return [ordered] @{
            supported = $false
            reason = 'requires exactly one complete context tier'
        }
    }

    $inputMultiplier = Get-RateRatio `
        ([decimal] $Cost.input) ([decimal] $tiers[0].input)
    $outputMultiplier = Get-RateRatio `
        ([decimal] $Cost.output) ([decimal] $tiers[0].output)
    if ($null -eq $inputMultiplier -or $null -eq $outputMultiplier) {
        return [ordered] @{
            supported = $false
            reason = 'tier rates are not valid proportional multipliers'
        }
    }

    foreach ($cacheName in @('cache_read', 'cache_write')) {
        $baseRate = Get-OptionalDecimal $Cost $cacheName
        $tierRate = Get-OptionalDecimal $tiers[0] $cacheName
        if (($null -eq $baseRate) -ne ($null -eq $tierRate)) {
            return [ordered] @{
                supported = $false
                reason = "base and tier disagree on $cacheName availability"
            }
        }
        if ($null -ne $baseRate) {
            $cacheMultiplier = Get-RateRatio $baseRate $tierRate
            if ($null -eq $cacheMultiplier -or
                $cacheMultiplier -ne $inputMultiplier) {
                return [ordered] @{
                    supported = $false
                    reason = "$cacheName tier multiplier differs from input"
                }
            }
        }
    }

    return [ordered] @{
        supported = $true
        threshold = [long] $tiers[0].tier.size
        inputMultiplier = $inputMultiplier
        outputMultiplier = $outputMultiplier
    }
}

function Get-LiteLlmTier {
    param([Parameter(Mandatory)] [System.Collections.IDictionary] $Metadata)

    $tiers = [System.Collections.Generic.List[object]]::new()
    foreach ($threshold in @(128000, 200000, 256000, 272000, 512000)) {
        $label = [string] ($threshold / 1000)
        $inputName = "input_cost_per_token_above_${label}k_tokens"
        $outputName = "output_cost_per_token_above_${label}k_tokens"
        $hasInput = $Metadata.Contains($inputName)
        $hasOutput = $Metadata.Contains($outputName)
        if ($hasInput -or $hasOutput) {
            $tiers.Add([ordered] @{
                threshold = $threshold
                inputName = $inputName
                outputName = $outputName
                complete = $hasInput -and $hasOutput
            })
        }
    }

    if ($tiers.Count -eq 0) {
        return [ordered] @{
            supported = $true
            threshold = $null
            inputMultiplier = [decimal] 1
            outputMultiplier = [decimal] 1
        }
    }
    if ($tiers.Count -ne 1 -or
        -not $tiers[0].complete -or
        $null -ne (Get-OptionalDecimal $Metadata 'cache_read_input_token_cost') -or
        $null -ne (Get-OptionalDecimal $Metadata 'cache_creation_input_token_cost')) {
        return [ordered] @{
            supported = $false
            reason = 'requires exactly one complete tier without cache tier fields'
        }
    }

    $inputMultiplier = Get-RateRatio `
        ([decimal] $Metadata.input_cost_per_token) `
        ([decimal] $Metadata[$tiers[0].inputName])
    $outputMultiplier = Get-RateRatio `
        ([decimal] $Metadata.output_cost_per_token) `
        ([decimal] $Metadata[$tiers[0].outputName])
    if ($null -eq $inputMultiplier -or $null -eq $outputMultiplier) {
        return [ordered] @{
            supported = $false
            reason = 'tier rates are not valid proportional multipliers'
        }
    }

    return [ordered] @{
        supported = $true
        threshold = [long] $tiers[0].threshold
        inputMultiplier = $inputMultiplier
        outputMultiplier = $outputMultiplier
    }
}

function New-Rule {
    param(
        [Parameter(Mandatory)] [string] $Source,
        [Parameter(Mandatory)] [string] $SourceKey,
        [Parameter(Mandatory)] [string] $Model,
        [Parameter(Mandatory)] $InputRate,
        [AllowNull()] $CachedInput,
        [AllowNull()] $CacheWrite,
        [Parameter(Mandatory)] $OutputRate,
        [AllowNull()] $LongContextThreshold,
        $LongContextInputMultiplier = 1,
        $LongContextOutputMultiplier = 1
    )

    $requiredNumbers = [ordered] @{
        InputRate = $InputRate
        OutputRate = $OutputRate
        LongContextInputMultiplier = $LongContextInputMultiplier
        LongContextOutputMultiplier = $LongContextOutputMultiplier
    }
    foreach ($number in $requiredNumbers.GetEnumerator()) {
        if ($number.Value -is [array]) {
            throw "Price rule '$SourceKey' received an array for '$($number.Key)' where a numeric value was required."
        }
    }

    $rule = [ordered] @{
        ruleId = "${Source}:$SourceKey"
        source = $Source
        sourceKey = $SourceKey
        models = @($Model)
        inputUsdPerMillion = [decimal] $InputRate
        cachedInputUsdPerMillion = if ($null -eq $CachedInput) {
            $null
        }
        else { [decimal] $CachedInput }
        cacheWriteUsdPerMillion = if ($null -eq $CacheWrite) {
            $null
        }
        else { [decimal] $CacheWrite }
        outputUsdPerMillion = [decimal] $OutputRate
    }
    if ($null -ne $LongContextThreshold) {
        $rule.longContextThresholdTokens = [long] $LongContextThreshold
        $rule.longContextInputMultiplier =
            [decimal] $LongContextInputMultiplier
        $rule.longContextOutputMultiplier =
            [decimal] $LongContextOutputMultiplier
    }
    return $rule
}

function Get-RateSignature {
    param([Parameter(Mandatory)] [System.Collections.IDictionary] $Rule)

    $culture = [Globalization.CultureInfo]::InvariantCulture
    $format = {
        param($Value)
        if ($null -eq $Value) {
            return '<null>'
        }
        return ([decimal] $Value).ToString('G29', $culture)
    }
    $threshold = if ($Rule.Contains('longContextThresholdTokens')) {
        [string] ([long] $Rule.longContextThresholdTokens)
    }
    else { '<null>' }
    $inputMultiplier = if ($Rule.Contains('longContextInputMultiplier')) {
        $Rule.longContextInputMultiplier
    }
    else { [decimal] 1 }
    $outputMultiplier = if ($Rule.Contains('longContextOutputMultiplier')) {
        $Rule.longContextOutputMultiplier
    }
    else { [decimal] 1 }
    return @(
        (& $format $Rule.inputUsdPerMillion),
        (& $format $Rule.cachedInputUsdPerMillion),
        (& $format $Rule.cacheWriteUsdPerMillion),
        (& $format $Rule.outputUsdPerMillion),
        $threshold,
        (& $format $inputMultiplier),
        (& $format $outputMultiplier)
    ) -join '|'
}

function Get-CatalogContentSignature {
    param([AllowNull()] [System.Collections.IDictionary] $Catalog)

    if ($null -eq $Catalog) {
        return '<missing>'
    }
    $parts = [System.Collections.Generic.List[string]]::new()
    foreach ($name in @(
            'schemaVersion',
            'currency',
            'unit',
            'selectionPolicy',
            'identityCatalogVersion',
            'shadowedByMaintainedCount',
            'sourceDisagreementCount')) {
        $value = if ($Catalog.Contains($name)) {
            [string] $Catalog[$name]
        }
        else { '<missing>' }
        $parts.Add("meta|$name|$value")
    }
    foreach ($source in @($Catalog.dataSources |
            Sort-Object { [string] $_.id })) {
        $parts.Add(@(
            'source',
            [string] $source.id,
            [string] $source.uri,
            [string] $source.artifact,
            [string] $source.sha256,
            [string] $source.selectedRuleCount
        ) -join '|')
    }
    foreach ($rule in @($Catalog.rules |
            Sort-Object { [string] $_.ruleId })) {
        $parts.Add(@(
            'rule',
            [string] $rule.ruleId,
            [string] $rule.source,
            [string] $rule.sourceKey,
            (@($rule.models | Sort-Object) -join ','),
            (Get-RateSignature $rule)
        ) -join '|')
    }
    return ($parts -join [Environment]::NewLine)
}

function Get-RuleMap {
    param([AllowNull()] [System.Collections.IDictionary] $Catalog)

    $result = [ordered] @{}
    if ($null -eq $Catalog -or
        -not $Catalog.Contains('rules') -or
        $null -eq $Catalog.rules) {
        return $result
    }
    foreach ($rule in @($Catalog.rules)) {
        foreach ($model in @($rule.models)) {
            $result[[string] $model] = $rule
        }
    }
    return $result
}

function Get-NextLayerVersion {
    param(
        [Parameter(Mandatory)] [string] $Prefix,
        [AllowNull()] [System.Collections.IDictionary] $CurrentCatalog
    )

    $date = [DateTime]::UtcNow.ToString('yyyy-MM-dd')
    $revision = 1
    if ($null -ne $CurrentCatalog) {
        $currentVersion = [string] $CurrentCatalog.catalogVersion
        $pattern = '^{0}-{1}-r(?<revision>[0-9]+)$' -f
            [Regex]::Escape($Prefix),
            [Regex]::Escape($date)
        if ($currentVersion -match $pattern) {
            $revision = [int] $Matches.revision + 1
        }
    }
    return "$Prefix-$date-r$revision"
}

function Assert-OfficialEvidence {
    param(
        [Parameter(Mandatory)] [System.Collections.IDictionary] $Item,
        [Parameter(Mandatory)] [string] $Description
    )

    if (-not $Item.Contains('officialSource') -or
        [string]::IsNullOrWhiteSpace([string] $Item.officialSource)) {
        throw "$Description requires officialSource."
    }
    $uri = $null
    if (-not [Uri]::TryCreate(
            [string] $Item.officialSource,
            [UriKind]::Absolute,
            [ref] $uri) -or
        -not [string]::Equals(
            $uri.Scheme,
            'https',
            [StringComparison]::OrdinalIgnoreCase)) {
        throw "$Description officialSource must be an absolute HTTPS URL."
    }
    if (-not $Item.Contains('verifiedOn') -or
        [string] $Item.verifiedOn -cnotmatch '^\d{4}-\d{2}-\d{2}$') {
        throw "$Description verifiedOn must use YYYY-MM-DD."
    }
    try {
        [void] [DateTime]::ParseExact(
            [string] $Item.verifiedOn,
            'yyyy-MM-dd',
            [Globalization.CultureInfo]::InvariantCulture,
            [Globalization.DateTimeStyles]::None)
    }
    catch {
        throw "$Description verifiedOn is not a valid calendar date."
    }
}

function Assert-OptionalNonNegativeDecimal {
    param(
        [Parameter(Mandatory)] [System.Collections.IDictionary] $Item,
        [Parameter(Mandatory)] [string] $Name,
        [Parameter(Mandatory)] [string] $Description,
        [switch] $Required
    )

    if (-not $Item.Contains($Name) -or $null -eq $Item[$Name]) {
        if ($Required) {
            throw "$Description requires $Name."
        }
        return
    }
    try {
        $value = [decimal] $Item[$Name]
    }
    catch {
        throw "$Description $Name must be numeric."
    }
    if ($value -lt 0) {
        throw "$Description $Name cannot be negative."
    }
}

function Assert-MaintainedCatalog {
    param([Parameter(Mandatory)] [System.Collections.IDictionary] $Catalog)

    if (-not $Catalog.Contains('schemaVersion') -or
        [int] $Catalog.schemaVersion -ne 1 -or
        -not $Catalog.Contains('catalogVersion') -or
        -not $Catalog.Contains('currency') -or
        -not [string]::Equals(
            [string] $Catalog.currency,
            'USD',
            [StringComparison]::Ordinal) -or
        -not $Catalog.Contains('unit') -or
        -not [string]::Equals(
            [string] $Catalog.unit,
            'per_million_tokens',
            [StringComparison]::Ordinal) -or
        -not $Catalog.Contains('rules')) {
        throw 'AgenTally-maintained price catalog has an invalid schema.'
    }

    $ruleIds = [System.Collections.Generic.HashSet[string]]::new(
        [System.StringComparer]::Ordinal)
    $models = [System.Collections.Generic.HashSet[string]]::new(
        [System.StringComparer]::Ordinal)
    foreach ($rule in @($Catalog.rules)) {
        if ($null -eq $rule -or
            -not ($rule -is [System.Collections.IDictionary]) -or
            -not $rule.Contains('ruleId')) {
            throw 'AgenTally-maintained price catalog contains an invalid rule.'
        }
        $ruleId = [string] $rule.ruleId
        $description = "Maintained price rule '$ruleId'"
        if ($ruleId -cnotmatch '^[a-z0-9][a-z0-9._/-]{0,127}$' -or
            -not $ruleIds.Add($ruleId)) {
            throw "$description has an invalid or duplicate ruleId."
        }
        if (-not $rule.Contains('provider') -or
            [string] $rule.provider -cnotmatch '^[a-z0-9][a-z0-9._-]{0,63}$') {
            throw "$description requires a normalized provider."
        }
        Assert-OfficialEvidence $rule $description
        if (-not $rule.Contains('models') -or @($rule.models).Count -eq 0) {
            throw "$description must contain at least one model."
        }
        foreach ($modelValue in @($rule.models)) {
            $model = Normalize-Identifier ([string] $modelValue)
            if ([string]::IsNullOrWhiteSpace($model) -or
                $model.Contains('/') -or
                -not [string]::Equals(
                    $model,
                    [string] $modelValue,
                    [StringComparison]::Ordinal) -or
                -not $models.Add($model)) {
                throw "$description contains an invalid or duplicate model '$modelValue'."
            }
        }
        Assert-OptionalNonNegativeDecimal $rule 'inputUsdPerMillion' $description -Required
        Assert-OptionalNonNegativeDecimal $rule 'cachedInputUsdPerMillion' $description
        Assert-OptionalNonNegativeDecimal $rule 'cacheWriteUsdPerMillion' $description
        Assert-OptionalNonNegativeDecimal $rule 'outputUsdPerMillion' $description -Required

        $tierNames = @(
            'longContextThresholdTokens',
            'longContextInputMultiplier',
            'longContextOutputMultiplier')
        $tierCount = @($tierNames | Where-Object { $rule.Contains($_) }).Count
        if ($tierCount -ne 0 -and $tierCount -ne $tierNames.Count) {
            throw "$description must provide all long-context fields together."
        }
        if ($tierCount -eq $tierNames.Count) {
            if ([long] $rule.longContextThresholdTokens -le 0 -or
                [decimal] $rule.longContextInputMultiplier -lt 1 -or
                [decimal] $rule.longContextOutputMultiplier -lt 1) {
                throw "$description has invalid long-context values."
            }
        }
    }
}

function Assert-ReviewedPriceAliasCatalog {
    param([Parameter(Mandatory)] [System.Collections.IDictionary] $Catalog)

    if (-not $Catalog.Contains('schemaVersion') -or
        [int] $Catalog.schemaVersion -ne 1 -or
        -not $Catalog.Contains('catalogVersion') -or
        -not $Catalog.Contains('aliases')) {
        throw 'Reviewed price-alias catalog has an invalid schema.'
    }
    $aliases = [System.Collections.Generic.HashSet[string]]::new(
        [System.StringComparer]::Ordinal)
    foreach ($rule in @($Catalog.aliases)) {
        if ($null -eq $rule -or
            -not ($rule -is [System.Collections.IDictionary])) {
            throw 'Reviewed price-alias catalog contains an invalid rule.'
        }
        $alias = Normalize-Identifier ([string] $rule.alias)
        $pricedAs = Normalize-Identifier ([string] $rule.pricedAs)
        $description = "Reviewed price alias '$alias'"
        if ([string]::IsNullOrWhiteSpace($alias) -or
            [string]::IsNullOrWhiteSpace($pricedAs) -or
            $alias.Contains('/') -or
            $pricedAs.Contains('/') -or
            $alias -eq $pricedAs -or
            -not [string]::Equals(
                $alias,
                [string] $rule.alias,
                [StringComparison]::Ordinal) -or
            -not [string]::Equals(
                $pricedAs,
                [string] $rule.pricedAs,
                [StringComparison]::Ordinal) -or
            -not $aliases.Add($alias)) {
            throw "$description is invalid or duplicated."
        }
        Assert-OfficialEvidence $rule $description
    }
}

function Get-MaintainedCatalogContentSignature {
    param([Parameter(Mandatory)] [System.Collections.IDictionary] $Catalog)

    $parts = [System.Collections.Generic.List[string]]::new()
    $parts.Add("schemaVersion|$($Catalog.schemaVersion)")
    $parts.Add("currency|$($Catalog.currency)")
    $parts.Add("unit|$($Catalog.unit)")
    foreach ($rule in @($Catalog.rules | Sort-Object { [string] $_.ruleId })) {
        $parts.Add(@(
            [string] $rule.ruleId,
            [string] $rule.provider,
            [string] $rule.officialSource,
            [string] $rule.verifiedOn,
            (@($rule.models | Sort-Object) -join ','),
            (Get-RateSignature $rule)
        ) -join '|')
    }
    return $parts -join [Environment]::NewLine
}

function Get-ReviewedAliasContentSignature {
    param([Parameter(Mandatory)] [System.Collections.IDictionary] $Catalog)

    $parts = [System.Collections.Generic.List[string]]::new()
    $parts.Add("schemaVersion|$($Catalog.schemaVersion)")
    foreach ($rule in @($Catalog.aliases | Sort-Object { [string] $_.alias })) {
        $parts.Add(@(
            [string] $rule.alias,
            [string] $rule.pricedAs,
            [string] $rule.officialSource,
            [string] $rule.verifiedOn
        ) -join '|')
    }
    return $parts -join [Environment]::NewLine
}

function Get-RuleDetails {
    param([Parameter(Mandatory)] [System.Collections.IDictionary] $Rule)

    return [ordered] @{
        ruleId = [string] $Rule.ruleId
        source = if ($Rule.Contains('source')) { [string] $Rule.source } else { $null }
        provider = if ($Rule.Contains('provider')) { [string] $Rule.provider } else { $null }
        sourceKey = if ($Rule.Contains('sourceKey')) { [string] $Rule.sourceKey } else { $null }
        officialSource = if ($Rule.Contains('officialSource')) { [string] $Rule.officialSource } else { $null }
        verifiedOn = if ($Rule.Contains('verifiedOn')) { [string] $Rule.verifiedOn } else { $null }
        models = @($Rule.models)
        inputUsdPerMillion = $Rule.inputUsdPerMillion
        cachedInputUsdPerMillion = $Rule.cachedInputUsdPerMillion
        cacheWriteUsdPerMillion = $Rule.cacheWriteUsdPerMillion
        outputUsdPerMillion = $Rule.outputUsdPerMillion
        longContextThresholdTokens = if ($Rule.Contains('longContextThresholdTokens')) {
            $Rule.longContextThresholdTokens
        } else { $null }
        longContextInputMultiplier = if ($Rule.Contains('longContextInputMultiplier')) {
            $Rule.longContextInputMultiplier
        } else { $null }
        longContextOutputMultiplier = if ($Rule.Contains('longContextOutputMultiplier')) {
            $Rule.longContextOutputMultiplier
        } else { $null }
    }
}

function Get-RuleIdMap {
    param([Parameter(Mandatory)] [System.Collections.IDictionary] $Catalog)

    $result = [ordered] @{}
    foreach ($rule in @($Catalog.rules)) {
        $result[[string] $rule.ruleId] = $rule
    }
    return $result
}

function Get-ReviewedAliasMap {
    param([Parameter(Mandatory)] [System.Collections.IDictionary] $Catalog)

    $result = [ordered] @{}
    foreach ($rule in @($Catalog.aliases)) {
        $result[[string] $rule.alias] = $rule
    }
    return $result
}

function Format-OptionalRate {
    param([AllowNull()] $Value)

    if ($null -eq $Value) {
        return 'n/a'
    }
    return ([decimal] $Value).ToString(
        'G29',
        [Globalization.CultureInfo]::InvariantCulture)
}

function Format-RuleRates {
    param([Parameter(Mandatory)] [System.Collections.IDictionary] $Rule)

    $threshold = if ($Rule.Contains('longContextThresholdTokens') -and
        $null -ne $Rule.longContextThresholdTokens) {
        "; threshold=$(Format-OptionalRate $Rule.longContextThresholdTokens)" +
        "; inputMultiplier=$(Format-OptionalRate $Rule.longContextInputMultiplier)" +
        "; outputMultiplier=$(Format-OptionalRate $Rule.longContextOutputMultiplier)"
    }
    else { '' }
    return 'input={0}; cachedInput={1}; cacheWrite={2}; output={3}{4}' -f
        (Format-OptionalRate $Rule.inputUsdPerMillion),
        (Format-OptionalRate $Rule.cachedInputUsdPerMillion),
        (Format-OptionalRate $Rule.cacheWriteUsdPerMillion),
        (Format-OptionalRate $Rule.outputUsdPerMillion),
        $threshold
}

New-Item -ItemType Directory -Force -Path $maintenanceRoot | Out-Null
$resolvedIdentityPath = if ([string]::IsNullOrWhiteSpace($IdentityCatalogPath)) {
    $identitySourcePath
}
else { (Resolve-Path -LiteralPath $IdentityCatalogPath).Path }
$resolvedMaintainedPath = if ([string]::IsNullOrWhiteSpace($MaintainedCatalogPath)) {
    $maintainedSourcePath
}
else { (Resolve-Path -LiteralPath $MaintainedCatalogPath).Path }
$resolvedCurrentMaintainedPath = if (
    [string]::IsNullOrWhiteSpace($CurrentMaintainedCatalogPath)) {
    $maintainedSourcePath
}
else { (Resolve-Path -LiteralPath $CurrentMaintainedCatalogPath).Path }
$resolvedReviewedPriceAliasPath = if (
    [string]::IsNullOrWhiteSpace($ReviewedPriceAliasCatalogPath)) {
    $reviewedPriceAliasSourcePath
}
else { (Resolve-Path -LiteralPath $ReviewedPriceAliasCatalogPath).Path }
$resolvedCurrentReviewedPriceAliasPath = if (
    [string]::IsNullOrWhiteSpace($CurrentReviewedPriceAliasCatalogPath)) {
    $reviewedPriceAliasSourcePath
}
else { (Resolve-Path -LiteralPath $CurrentReviewedPriceAliasCatalogPath).Path }
$resolvedCurrentPath = if ([string]::IsNullOrWhiteSpace($CurrentCatalogPath)) {
    $sourcePath
}
else { (Resolve-Path -LiteralPath $CurrentCatalogPath).Path }
$resolvedProviderPath = Get-OrDownloadJson `
    $ProviderCatalogPath $modelsDevUri 'models.dev-catalog.json'
$resolvedLiteLlmPath = Get-OrDownloadJson `
    $LiteLlmCatalogPath $liteLlmUri 'litellm-model-prices.json'

$identity = Read-JsonDocument $resolvedIdentityPath
$maintained = Read-JsonDocument $resolvedMaintainedPath
$currentMaintained = Read-JsonDocument $resolvedCurrentMaintainedPath
$reviewedPriceAliases = Read-JsonDocument $resolvedReviewedPriceAliasPath
$currentReviewedPriceAliases =
    Read-JsonDocument $resolvedCurrentReviewedPriceAliasPath
$current = Read-JsonDocument $resolvedCurrentPath -Optional
$providerCatalog = Read-JsonDocument $resolvedProviderPath
$liteLlmCatalog = Read-JsonDocument $resolvedLiteLlmPath
if (-not $identity.Contains('aliases') -or
    -not $providerCatalog.Contains('models') -or
    -not $providerCatalog.Contains('providers')) {
    throw 'One or more price-maintenance inputs have an invalid schema.'
}
Assert-MaintainedCatalog $maintained
Assert-MaintainedCatalog $currentMaintained
Assert-ReviewedPriceAliasCatalog $reviewedPriceAliases
Assert-ReviewedPriceAliasCatalog $currentReviewedPriceAliases

$maintainedContentChanged = -not [string]::Equals(
    (Get-MaintainedCatalogContentSignature $maintained),
    (Get-MaintainedCatalogContentSignature $currentMaintained),
    [StringComparison]::Ordinal)
if ([string]::IsNullOrWhiteSpace($MaintainedCatalogVersion)) {
    $MaintainedCatalogVersion = if (-not $maintainedContentChanged) {
        [string] $currentMaintained.catalogVersion
    }
    else {
        Get-NextLayerVersion 'official-api-usd' $currentMaintained
    }
}
if ($MaintainedCatalogVersion -cnotmatch '^[a-z0-9][a-z0-9._-]{0,127}$') {
    throw 'MaintainedCatalogVersion must be a lowercase stable identifier.'
}
if ($maintainedContentChanged -and [string]::Equals(
        $MaintainedCatalogVersion,
        [string] $currentMaintained.catalogVersion,
        [StringComparison]::Ordinal)) {
    throw 'Changed maintained price content requires a new MaintainedCatalogVersion.'
}
$maintained.catalogVersion = $MaintainedCatalogVersion

$reviewedAliasContentChanged = -not [string]::Equals(
    (Get-ReviewedAliasContentSignature $reviewedPriceAliases),
    (Get-ReviewedAliasContentSignature $currentReviewedPriceAliases),
    [StringComparison]::Ordinal)
if ([string]::IsNullOrWhiteSpace($ReviewedPriceAliasCatalogVersion)) {
    $ReviewedPriceAliasCatalogVersion = if (-not $reviewedAliasContentChanged) {
        [string] $currentReviewedPriceAliases.catalogVersion
    }
    else {
        Get-NextLayerVersion `
            'reviewed-price-aliases' $currentReviewedPriceAliases
    }
}
if ($ReviewedPriceAliasCatalogVersion -cnotmatch
    '^[a-z0-9][a-z0-9._-]{0,127}$') {
    throw 'ReviewedPriceAliasCatalogVersion must be a lowercase stable identifier.'
}
if ($reviewedAliasContentChanged -and [string]::Equals(
        $ReviewedPriceAliasCatalogVersion,
        [string] $currentReviewedPriceAliases.catalogVersion,
        [StringComparison]::Ordinal)) {
    throw 'Changed reviewed price aliases require a new ReviewedPriceAliasCatalogVersion.'
}
$reviewedPriceAliases.catalogVersion = $ReviewedPriceAliasCatalogVersion

$aliases = $identity.aliases
$originByCanonical = @{}
foreach ($qualifiedValue in ($providerCatalog.models.Keys | Sort-Object)) {
    $qualified = Normalize-Identifier ([string] $qualifiedValue)
    $separator = $qualified.IndexOf('/')
    if ($separator -le 0 -or $separator -eq $qualified.Length - 1) {
        continue
    }
    $canonical = Get-CanonicalModel $qualified $aliases
    if (-not $originByCanonical.Contains($canonical)) {
        $originByCanonical[$canonical] =
            [System.Collections.Generic.HashSet[string]]::new(
                [System.StringComparer]::Ordinal)
    }
    [void] $originByCanonical[$canonical].Add(
        $qualified.Substring(0, $separator))
}

$modelsDevRules = @{}
$unsupportedModelsDevTierEntries =
    [System.Collections.Generic.List[object]]::new()
foreach ($qualifiedValue in ($providerCatalog.models.Keys | Sort-Object)) {
    $qualifiedOriginal = [string] $qualifiedValue
    $qualified = Normalize-Identifier $qualifiedOriginal
    $separator = $qualifiedOriginal.IndexOf('/')
    if ($separator -le 0 -or $separator -eq $qualifiedOriginal.Length - 1) {
        continue
    }
    $providerIdOriginal = $qualifiedOriginal.Substring(0, $separator)
    $modelIdOriginal = $qualifiedOriginal.Substring($separator + 1)
    if (-not $providerCatalog.providers.Contains($providerIdOriginal)) {
        continue
    }
    $provider = $providerCatalog.providers[$providerIdOriginal]
    if (-not $provider.Contains('models') -or
        -not $provider.models.Contains($modelIdOriginal)) {
        continue
    }
    $metadata = $provider.models[$modelIdOriginal]
    if (-not $metadata.Contains('cost') -or
        -not $metadata.cost.Contains('input') -or
        -not $metadata.cost.Contains('output')) {
        continue
    }
    $tier = Get-ModelsDevTier $metadata.cost
    if (-not $tier.supported) {
        $unsupportedModelsDevTierEntries.Add([ordered] @{
            sourceKey = $qualified
            reason = [string] $tier.reason
        })
        continue
    }

    $canonical = Get-CanonicalModel $qualified $aliases
    $modelsDevRules[$canonical] = New-Rule `
        -Source 'models.dev' `
        -SourceKey $qualified `
        -Model $canonical `
        -InputRate ([decimal] $metadata.cost.input) `
        -CachedInput (Get-OptionalDecimal $metadata.cost 'cache_read') `
        -CacheWrite (Get-OptionalDecimal $metadata.cost 'cache_write') `
        -OutputRate ([decimal] $metadata.cost.output) `
        -LongContextThreshold $tier.threshold `
        -LongContextInputMultiplier $tier.inputMultiplier `
        -LongContextOutputMultiplier $tier.outputMultiplier
}

$liteProviderOrigins = @{
    ai21 = @('ai21')
    anthropic = @('anthropic')
    cohere = @('cohere')
    dashscope = @('alibaba')
    deepseek = @('deepseek')
    gemini = @('google')
    minimax = @('minimax')
    mistral = @('mistral')
    moonshot = @('moonshotai')
    nvidia_nim = @('nvidia')
    openai = @('openai')
    perplexity = @('perplexity')
    watsonx = @('ibm')
    xai = @('xai')
    zai = @('zhipuai')
}
$liteProviderModelPatterns = @{
    ai21 = '^(jamba|j2-)'
    anthropic = '^claude'
    cohere = '^(command|c4ai)'
    dashscope = '^qwen'
    deepseek = '^deepseek'
    gemini = '^(gemini|learnlm)'
    minimax = '^minimax'
    mistral = '^(mistral|codestral|ministral|pixtral|magistral|devstral|open-mistral|open-mixtral)'
    moonshot = '^(kimi|moonshot)'
    nvidia_nim = '^(nemotron|nvidia)'
    openai = '^(gpt-|o[134](?:-|$)|chatgpt-|text-|code-|davinci|babbage|curie|ada|computer-use)'
    perplexity = '^(sonar|pplx)'
    watsonx = '^(granite|ibm)'
    xai = '^grok'
    zai = '^glm'
}
$liteLlmCandidates = @{}
$unsupportedLiteLlmTierEntries =
    [System.Collections.Generic.List[object]]::new()
$excludedLiteLlmProviders =
    [System.Collections.Generic.List[object]]::new()
$excludedLiteLlmModelFamilies =
    [System.Collections.Generic.List[object]]::new()
foreach ($entry in ($liteLlmCatalog.GetEnumerator() | Sort-Object Key)) {
    $metadata = $entry.Value
    if ($null -eq $metadata -or
        -not ($metadata -is [System.Collections.IDictionary]) -or
        -not $metadata.Contains('litellm_provider') -or
        -not $metadata.Contains('input_cost_per_token') -or
        -not $metadata.Contains('output_cost_per_token') -or
        $null -eq $metadata.input_cost_per_token -or
        $null -eq $metadata.output_cost_per_token) {
        continue
    }
    $provider = Normalize-Identifier ([string] $metadata.litellm_provider)
    if ($metadata.Contains('mode') -and
        -not [string]::IsNullOrWhiteSpace([string] $metadata.mode) -and
        @('chat', 'completion', 'responses', 'text_completion') -notcontains
            (Normalize-Identifier ([string] $metadata.mode))) {
        continue
    }

    $key = Normalize-Identifier ([string] $entry.Key)
    if (-not $liteProviderOrigins.Contains($provider)) {
        $excludedLiteLlmProviders.Add([ordered] @{
            provider = $provider
            sourceKey = $key
        })
        continue
    }
    $segments = @($key.Split('/'))
    if ($segments.Count -gt 2 -or
        ($segments.Count -eq 2 -and
         -not [string]::Equals(
             $segments[0],
             $provider,
             [StringComparison]::Ordinal))) {
        continue
    }
    $canonical = Get-CanonicalModel $key $aliases
    if ($canonical -cnotmatch $liteProviderModelPatterns[$provider]) {
        $excludedLiteLlmModelFamilies.Add([ordered] @{
            provider = $provider
            sourceKey = $key
            canonical = $canonical
        })
        continue
    }
    if ($originByCanonical.Contains($canonical)) {
        $compatible = $false
        foreach ($origin in $liteProviderOrigins[$provider]) {
            if ($originByCanonical[$canonical].Contains($origin)) {
                $compatible = $true
                break
            }
        }
        if (-not $compatible) {
            continue
        }
    }

    $tier = Get-LiteLlmTier $metadata
    if (-not $tier.supported) {
        $unsupportedLiteLlmTierEntries.Add([ordered] @{
            sourceKey = $key
            reason = [string] $tier.reason
        })
        continue
    }
    $rule = New-Rule `
        -Source 'litellm' `
        -SourceKey $key `
        -Model $canonical `
        -InputRate ([decimal] $metadata.input_cost_per_token * 1000000) `
        -CachedInput $(
            $value = Get-OptionalDecimal `
                $metadata 'cache_read_input_token_cost'
            if ($null -eq $value) { $null } else { $value * 1000000 }
        ) `
        -CacheWrite $(
            $value = Get-OptionalDecimal `
                $metadata 'cache_creation_input_token_cost'
            if ($null -eq $value) { $null } else { $value * 1000000 }
        ) `
        -OutputRate ([decimal] $metadata.output_cost_per_token * 1000000) `
        -LongContextThreshold $tier.threshold `
        -LongContextInputMultiplier $tier.inputMultiplier `
        -LongContextOutputMultiplier $tier.outputMultiplier
    $rank = if ($segments.Count -eq 1) { 0 } else { 1 }
    if (-not $liteLlmCandidates.Contains($canonical) -or
        $rank -lt $liteLlmCandidates[$canonical].rank -or
        ($rank -eq $liteLlmCandidates[$canonical].rank -and
         [string]::CompareOrdinal(
             $key,
             [string] $liteLlmCandidates[$canonical].rule.sourceKey) -lt 0)) {
        $liteLlmCandidates[$canonical] = [ordered] @{
            rank = $rank
            rule = $rule
        }
    }
}

$selectedRules = @{}
foreach ($model in $modelsDevRules.Keys) {
    $selectedRules[$model] = $modelsDevRules[$model]
}
$sourceDisagreements = [System.Collections.Generic.List[object]]::new()
foreach ($model in ($liteLlmCandidates.Keys | Sort-Object)) {
    $liteRule = $liteLlmCandidates[$model].rule
    if ($selectedRules.Contains($model)) {
        if (-not [string]::Equals(
            (Get-RateSignature $selectedRules[$model]),
            (Get-RateSignature $liteRule),
            [StringComparison]::Ordinal)) {
            $sourceDisagreements.Add([ordered] @{
                model = $model
                selected = Get-RuleDetails $selectedRules[$model]
                fallback = Get-RuleDetails $liteRule
            })
        }
        continue
    }
    $selectedRules[$model] = $liteRule
}

$maintainedRuleMap = Get-RuleMap $maintained
$currentMaintainedRuleMap = Get-RuleMap $currentMaintained
$maintainedModels = [System.Collections.Generic.HashSet[string]]::new(
    [System.StringComparer]::Ordinal)
foreach ($model in $maintainedRuleMap.Keys) {
    [void] $maintainedModels.Add([string] $model)
}
$shadowedByMaintainedCount = @($selectedRules.Keys |
    Where-Object { $maintainedModels.Contains($_) }).Count

$maintainedUpstreamDifferences =
    [System.Collections.Generic.List[object]]::new()
$maintainedUpstreamMatches = 0
foreach ($model in ($selectedRules.Keys |
        Where-Object { $maintainedRuleMap.Contains($_) } | Sort-Object)) {
    $maintainedRule = $maintainedRuleMap[$model]
    $upstreamRule = $selectedRules[$model]
    if ([string]::Equals(
            (Get-RateSignature $maintainedRule),
            (Get-RateSignature $upstreamRule),
            [StringComparison]::Ordinal)) {
        $maintainedUpstreamMatches++
        continue
    }
    $maintainedUpstreamDifferences.Add([ordered] @{
        model = $model
        maintained = Get-RuleDetails $maintainedRule
        upstream = Get-RuleDetails $upstreamRule
    })
}

$currentMaintainedRulesById = Get-RuleIdMap $currentMaintained
$candidateMaintainedRulesById = Get-RuleIdMap $maintained
$maintainedRuleAdditions = @($candidateMaintainedRulesById.Keys |
    Where-Object { -not $currentMaintainedRulesById.Contains($_) } |
    Sort-Object)
$maintainedRuleRemovals = @($currentMaintainedRulesById.Keys |
    Where-Object { -not $candidateMaintainedRulesById.Contains($_) } |
    Sort-Object)
$maintainedRuleChanges = [System.Collections.Generic.List[object]]::new()
foreach ($ruleId in ($candidateMaintainedRulesById.Keys |
        Where-Object { $currentMaintainedRulesById.Contains($_) } |
        Sort-Object)) {
    $before = $currentMaintainedRulesById[$ruleId]
    $after = $candidateMaintainedRulesById[$ruleId]
    if (-not [string]::Equals(
            ((Get-RuleDetails $before) | ConvertTo-Json -Depth 10 -Compress),
            ((Get-RuleDetails $after) | ConvertTo-Json -Depth 10 -Compress),
            [StringComparison]::Ordinal)) {
        $maintainedRuleChanges.Add([ordered] @{
            ruleId = $ruleId
            before = Get-RuleDetails $before
            after = Get-RuleDetails $after
        })
    }
}
$maintainedModelAdditions = @($maintainedRuleMap.Keys |
    Where-Object { -not $currentMaintainedRuleMap.Contains($_) } | Sort-Object)
$maintainedModelRemovals = @($currentMaintainedRuleMap.Keys |
    Where-Object { -not $maintainedRuleMap.Contains($_) } | Sort-Object)

$availablePriceModels = [System.Collections.Generic.HashSet[string]]::new(
    [System.StringComparer]::Ordinal)
foreach ($model in $maintainedModels) {
    [void] $availablePriceModels.Add($model)
}
foreach ($model in $selectedRules.Keys) {
    [void] $availablePriceModels.Add($model)
}
$reviewedPriceAliasKeys = [System.Collections.Generic.HashSet[string]]::new(
    [System.StringComparer]::Ordinal)
$reviewedPriceAliasExactShadows = 0
foreach ($rule in @($reviewedPriceAliases.aliases)) {
    $alias = Normalize-Identifier ([string] $rule.alias)
    $pricedAs = Normalize-Identifier ([string] $rule.pricedAs)
    if ([string]::IsNullOrWhiteSpace($alias) -or
        [string]::IsNullOrWhiteSpace($pricedAs) -or
        $alias.Contains('/') -or
        $pricedAs.Contains('/') -or
        $alias -eq $pricedAs -or
        -not $reviewedPriceAliasKeys.Add($alias)) {
        throw 'Reviewed price-alias catalog contains an invalid or duplicate rule.'
    }
    if (-not $availablePriceModels.Contains($pricedAs)) {
        throw "Reviewed price alias '$alias' targets missing rule '$pricedAs'."
    }
    if ($availablePriceModels.Contains($alias)) {
        $reviewedPriceAliasExactShadows++
    }
}

$currentReviewedAliasMap = Get-ReviewedAliasMap $currentReviewedPriceAliases
$candidateReviewedAliasMap = Get-ReviewedAliasMap $reviewedPriceAliases
$reviewedAliasAdditions = [System.Collections.Generic.List[object]]::new()
$reviewedAliasRemovals = [System.Collections.Generic.List[object]]::new()
$reviewedAliasRetargets = [System.Collections.Generic.List[object]]::new()
$reviewedAliasEvidenceChanges = [System.Collections.Generic.List[object]]::new()
$allReviewedAliases = [System.Collections.Generic.HashSet[string]]::new(
    [System.StringComparer]::Ordinal)
foreach ($alias in $currentReviewedAliasMap.Keys) {
    [void] $allReviewedAliases.Add([string] $alias)
}
foreach ($alias in $candidateReviewedAliasMap.Keys) {
    [void] $allReviewedAliases.Add([string] $alias)
}
foreach ($alias in ($allReviewedAliases | Sort-Object)) {
    if (-not $currentReviewedAliasMap.Contains($alias)) {
        $reviewedAliasAdditions.Add($candidateReviewedAliasMap[$alias])
        continue
    }
    if (-not $candidateReviewedAliasMap.Contains($alias)) {
        $reviewedAliasRemovals.Add($currentReviewedAliasMap[$alias])
        continue
    }
    $before = $currentReviewedAliasMap[$alias]
    $after = $candidateReviewedAliasMap[$alias]
    if (-not [string]::Equals(
            [string] $before.pricedAs,
            [string] $after.pricedAs,
            [StringComparison]::Ordinal)) {
        $reviewedAliasRetargets.Add([ordered] @{
            alias = $alias
            before = $before
            after = $after
        })
    }
    if (-not [string]::Equals(
            [string] $before.officialSource,
            [string] $after.officialSource,
            [StringComparison]::Ordinal) -or
        -not [string]::Equals(
            [string] $before.verifiedOn,
            [string] $after.verifiedOn,
            [StringComparison]::Ordinal)) {
        $reviewedAliasEvidenceChanges.Add([ordered] @{
            alias = $alias
            beforeOfficialSource = [string] $before.officialSource
            afterOfficialSource = [string] $after.officialSource
            beforeVerifiedOn = [string] $before.verifiedOn
            afterVerifiedOn = [string] $after.verifiedOn
        })
    }
}

$rules = @($selectedRules.Keys | Sort-Object |
    ForEach-Object { $selectedRules[$_] })
$modelsDevSelectedCount = @($rules |
    Where-Object { $_.source -eq 'models.dev' }).Count
$liteLlmSelectedCount = @($rules |
    Where-Object { $_.source -eq 'litellm' }).Count

$dataSources = @(
    [ordered] @{
        id = 'models.dev'
        uri = $modelsDevUri
        artifact = 'catalog.json'
        sha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $resolvedProviderPath).Hash.ToLowerInvariant()
        selectedRuleCount = $modelsDevSelectedCount
    },
    [ordered] @{
        id = 'litellm'
        uri = $liteLlmUri
        artifact = 'model_prices_and_context_window.json'
        sha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $resolvedLiteLlmPath).Hash.ToLowerInvariant()
        selectedRuleCount = $liteLlmSelectedCount
    }
)

$content = [ordered] @{
    schemaVersion = 1
    currency = 'USD'
    unit = 'per_million_tokens'
    selectionPolicy = $selectionPolicy
    identityCatalogVersion = [string] $identity.catalogVersion
    dataSources = $dataSources
    shadowedByMaintainedCount = $shadowedByMaintainedCount
    sourceDisagreementCount = $sourceDisagreements.Count
    rules = $rules
}
$contentJson = Get-CatalogContentSignature $content
$currentContentJson = Get-CatalogContentSignature $current
$contentChanged = -not [string]::Equals(
    $contentJson,
    $currentContentJson,
    [StringComparison]::Ordinal)

if ([string]::IsNullOrWhiteSpace($CatalogVersion)) {
    $CatalogVersion = if (-not $contentChanged -and $null -ne $current) {
        [string] $current.catalogVersion
    }
    else {
        Get-NextCatalogVersion $current
    }
}
if ($CatalogVersion -cnotmatch '^[a-z0-9][a-z0-9._-]{0,127}$') {
    throw 'CatalogVersion must be a lowercase stable identifier.'
}
if ($contentChanged -and $null -ne $current -and
    [string]::Equals(
        $CatalogVersion,
        [string] $current.catalogVersion,
        [StringComparison]::Ordinal)) {
    throw 'Changed price content requires a new CatalogVersion.'
}

$candidate = [ordered] @{
    schemaVersion = 1
    catalogVersion = $CatalogVersion
    currency = 'USD'
    unit = 'per_million_tokens'
    selectionPolicy = $selectionPolicy
    identityCatalogVersion = [string] $identity.catalogVersion
    dataSources = $dataSources
    shadowedByMaintainedCount = $shadowedByMaintainedCount
    sourceDisagreementCount = $sourceDisagreements.Count
    rules = $rules
}
$candidateJson = $candidate | ConvertTo-Json -Depth 12
[IO.File]::WriteAllText(
    $candidatePath,
    $candidateJson + [Environment]::NewLine,
    [Text.UTF8Encoding]::new($false))
[IO.File]::WriteAllText(
    $maintainedCandidatePath,
    ($maintained | ConvertTo-Json -Depth 12) + [Environment]::NewLine,
    [Text.UTF8Encoding]::new($false))
[IO.File]::WriteAllText(
    $reviewedPriceAliasCandidatePath,
    ($reviewedPriceAliases | ConvertTo-Json -Depth 12) + [Environment]::NewLine,
    [Text.UTF8Encoding]::new($false))

$currentRules = Get-RuleMap $current
$candidateRules = Get-RuleMap $candidate
$additions = @($candidateRules.Keys |
    Where-Object { -not $currentRules.Contains($_) } | Sort-Object)
$removals = @($currentRules.Keys |
    Where-Object { -not $candidateRules.Contains($_) } | Sort-Object)
$rateChanges = [System.Collections.Generic.List[object]]::new()
foreach ($model in ($candidateRules.Keys |
        Where-Object { $currentRules.Contains($_) } | Sort-Object)) {
    $before = $currentRules[$model]
    $after = $candidateRules[$model]
    if (-not [string]::Equals(
            (Get-RateSignature $before),
            (Get-RateSignature $after),
            [StringComparison]::Ordinal) -or
        -not [string]::Equals(
            [string] $before.ruleId,
            [string] $after.ruleId,
            [StringComparison]::Ordinal)) {
        $rateChanges.Add([ordered] @{
            model = $model
            before = Get-RuleDetails $before
            after = Get-RuleDetails $after
        })
    }
}

$hasAnyChanges =
    $contentChanged -or
    $maintainedContentChanged -or
    $reviewedAliasContentChanged
$diff = [ordered] @{
    schemaVersion = 1
    hasChanges = $hasAnyChanges
    upstream = [ordered] @{
        currentCatalogVersion = if ($null -eq $current) { $null } else {
            [string] $current.catalogVersion
        }
        candidateCatalogVersion = $CatalogVersion
        contentChanged = $contentChanged
        additions = $additions
        removals = $removals
        rateOrSourceChanges = @($rateChanges)
        sourceDisagreements = @($sourceDisagreements)
        unsupportedTieredModels = [ordered] @{
            modelsDev = @($unsupportedModelsDevTierEntries)
            liteLlm = @($unsupportedLiteLlmTierEntries)
        }
        excludedLiteLlmProviders = @($excludedLiteLlmProviders)
        excludedLiteLlmModelFamilies = @($excludedLiteLlmModelFamilies)
    }
    maintained = [ordered] @{
        currentCatalogVersion = [string] $currentMaintained.catalogVersion
        candidateCatalogVersion = $MaintainedCatalogVersion
        contentChanged = $maintainedContentChanged
        ruleAdditions = $maintainedRuleAdditions
        ruleRemovals = $maintainedRuleRemovals
        ruleChanges = @($maintainedRuleChanges)
        modelAdditions = $maintainedModelAdditions
        modelRemovals = $maintainedModelRemovals
        upstreamRateMatches = $maintainedUpstreamMatches
        upstreamRateDifferences = @($maintainedUpstreamDifferences)
    }
    reviewedPriceAliases = [ordered] @{
        currentCatalogVersion =
            [string] $currentReviewedPriceAliases.catalogVersion
        candidateCatalogVersion = $ReviewedPriceAliasCatalogVersion
        contentChanged = $reviewedAliasContentChanged
        additions = @($reviewedAliasAdditions)
        removals = @($reviewedAliasRemovals)
        retargets = @($reviewedAliasRetargets)
        evidenceChanges = @($reviewedAliasEvidenceChanges)
        exactPriceShadows = $reviewedPriceAliasExactShadows
    }
}
[IO.File]::WriteAllText(
    $diffPath,
    ($diff | ConvertTo-Json -Depth 20) + [Environment]::NewLine,
    [Text.UTF8Encoding]::new($false))

$reportLines = [System.Collections.Generic.List[string]]::new()
$reportLines.Add('# AgenTally complete token price maintenance review')
$reportLines.Add('')
$reportLines.Add("- Has changes: ``$($hasAnyChanges.ToString().ToLowerInvariant())``")
$reportLines.Add("- Upstream: ``$(if ($null -eq $current) { '<none>' } else { [string] $current.catalogVersion })`` -> ``$CatalogVersion``")
$reportLines.Add("- Maintained: ``$($currentMaintained.catalogVersion)`` -> ``$MaintainedCatalogVersion``")
$reportLines.Add("- Reviewed aliases: ``$($currentReviewedPriceAliases.catalogVersion)`` -> ``$ReviewedPriceAliasCatalogVersion``")
$reportLines.Add("- Selection policy: ``$selectionPolicy``")
$reportLines.Add("- Total selected upstream rules: $($rules.Count)")
$reportLines.Add("- models.dev selected: $modelsDevSelectedCount")
$reportLines.Add("- LiteLLM fallback selected: $liteLlmSelectedCount")
$reportLines.Add("- Shadowed by AgenTally-maintained prices: $shadowedByMaintainedCount")
$reportLines.Add("- Maintained/upstream exact rate matches: $maintainedUpstreamMatches")
$reportLines.Add("- Maintained/upstream rate differences: $($maintainedUpstreamDifferences.Count)")
$reportLines.Add("- Reviewed price aliases: $($reviewedPriceAliasKeys.Count)")
$reportLines.Add("- Reviewed aliases shadowed by exact prices: $reviewedPriceAliasExactShadows")
$reportLines.Add("- Cross-source price disagreements: $($sourceDisagreements.Count)")
$reportLines.Add("- Unsupported models.dev tier structures omitted: $($unsupportedModelsDevTierEntries.Count)")
$reportLines.Add("- Unsupported LiteLLM tier structures omitted: $($unsupportedLiteLlmTierEntries.Count)")
$reportLines.Add("- LiteLLM entries excluded by provider allow-list: $($excludedLiteLlmProviders.Count)")
$reportLines.Add("- LiteLLM entries excluded by model-family gate: $($excludedLiteLlmModelFamilies.Count)")
$reportLines.Add("- Upstream additions/removals/changes: $($additions.Count)/$($removals.Count)/$($rateChanges.Count)")
$reportLines.Add("- Maintained rule additions/removals/changes: $($maintainedRuleAdditions.Count)/$($maintainedRuleRemovals.Count)/$($maintainedRuleChanges.Count)")
$reportLines.Add("- Reviewed alias additions/removals/retargets/evidence changes: $($reviewedAliasAdditions.Count)/$($reviewedAliasRemovals.Count)/$($reviewedAliasRetargets.Count)/$($reviewedAliasEvidenceChanges.Count)")
$reportLines.Add('')
$reportLines.Add('models.dev is selected before LiteLLM. Only the model owner/provider lane')
$reportLines.Add('from models.dev and a reviewed allow-list of direct LiteLLM providers are')
$reportLines.Add('eligible; router, cloud-hosted and reseller prices are not candidates.')
$reportLines.Add('Effective-date price intervals are deliberately outside this maintenance pass.')

if ($rateChanges.Count -gt 0) {
    $reportLines.Add('')
    $reportLines.Add('## Upstream rate or selected-source changes')
    $reportLines.Add('')
    foreach ($change in $rateChanges) {
        $reportLines.Add("- ``$($change.model)``")
        $reportLines.Add("  - Before ``$($change.before.ruleId)``: $(Format-RuleRates $change.before)")
        $reportLines.Add("  - After ``$($change.after.ruleId)``: $(Format-RuleRates $change.after)")
    }
}
if ($sourceDisagreements.Count -gt 0) {
    $reportLines.Add('')
    $reportLines.Add('## models.dev versus LiteLLM disagreements')
    $reportLines.Add('')
    foreach ($change in $sourceDisagreements) {
        $reportLines.Add("- ``$($change.model)``")
        $reportLines.Add("  - Selected ``$($change.selected.ruleId)``: $(Format-RuleRates $change.selected)")
        $reportLines.Add("  - Fallback ``$($change.fallback.ruleId)``: $(Format-RuleRates $change.fallback)")
    }
}
if ($maintainedUpstreamDifferences.Count -gt 0) {
    $reportLines.Add('')
    $reportLines.Add('## Maintained prices versus selected upstream prices')
    $reportLines.Add('')
    foreach ($change in $maintainedUpstreamDifferences) {
        $reportLines.Add("- ``$($change.model)``")
        $reportLines.Add("  - Maintained ``$($change.maintained.ruleId)``: $(Format-RuleRates $change.maintained)")
        $reportLines.Add("  - Upstream ``$($change.upstream.ruleId)``: $(Format-RuleRates $change.upstream)")
    }
}
if ($maintainedRuleAdditions.Count -gt 0 -or
    $maintainedRuleRemovals.Count -gt 0 -or
    $maintainedRuleChanges.Count -gt 0) {
    $reportLines.Add('')
    $reportLines.Add('## Maintained catalog changes')
    $reportLines.Add('')
    foreach ($ruleId in $maintainedRuleAdditions) {
        $rule = $candidateMaintainedRulesById[$ruleId]
        $reportLines.Add("- Added ``$ruleId``: models ``$(@($rule.models) -join ', ')``; $(Format-RuleRates $rule)")
    }
    foreach ($ruleId in $maintainedRuleRemovals) {
        $rule = $currentMaintainedRulesById[$ruleId]
        $reportLines.Add("- Removed ``$ruleId``: models ``$(@($rule.models) -join ', ')``; $(Format-RuleRates $rule)")
    }
    foreach ($change in $maintainedRuleChanges) {
        $reportLines.Add("- Changed ``$($change.ruleId)``")
        $reportLines.Add("  - Before: models ``$(@($change.before.models) -join ', ')``; $(Format-RuleRates $change.before); source ``$($change.before.officialSource)``; verified ``$($change.before.verifiedOn)``")
        $reportLines.Add("  - After: models ``$(@($change.after.models) -join ', ')``; $(Format-RuleRates $change.after); source ``$($change.after.officialSource)``; verified ``$($change.after.verifiedOn)``")
    }
}
if ($reviewedAliasAdditions.Count -gt 0 -or
    $reviewedAliasRemovals.Count -gt 0 -or
    $reviewedAliasRetargets.Count -gt 0 -or
    $reviewedAliasEvidenceChanges.Count -gt 0) {
    $reportLines.Add('')
    $reportLines.Add('## Reviewed price-alias changes')
    $reportLines.Add('')
    foreach ($change in $reviewedAliasAdditions) {
        $reportLines.Add("- Added ``$($change.alias)`` -> ``$($change.pricedAs)``; source ``$($change.officialSource)``; verified ``$($change.verifiedOn)``")
    }
    foreach ($change in $reviewedAliasRemovals) {
        $reportLines.Add("- Removed ``$($change.alias)`` -> ``$($change.pricedAs)``; source ``$($change.officialSource)``; verified ``$($change.verifiedOn)``")
    }
    foreach ($change in $reviewedAliasRetargets) {
        $reportLines.Add("- Retargeted ``$($change.alias)``: ``$($change.before.pricedAs)`` -> ``$($change.after.pricedAs)``")
    }
    foreach ($change in $reviewedAliasEvidenceChanges) {
        $reportLines.Add("- Evidence changed for ``$($change.alias)``: ``$($change.beforeOfficialSource)`` / ``$($change.beforeVerifiedOn)`` -> ``$($change.afterOfficialSource)`` / ``$($change.afterVerifiedOn)``")
    }
}
if ($unsupportedModelsDevTierEntries.Count -gt 0 -or
    $unsupportedLiteLlmTierEntries.Count -gt 0) {
    $reportLines.Add('')
    $reportLines.Add('## Unsupported tier structures omitted')
    $reportLines.Add('')
    foreach ($entry in $unsupportedModelsDevTierEntries) {
        $reportLines.Add("- models.dev ``$($entry.sourceKey)``: $($entry.reason)")
    }
    foreach ($entry in $unsupportedLiteLlmTierEntries) {
        $reportLines.Add("- LiteLLM ``$($entry.sourceKey)``: $($entry.reason)")
    }
}
if ($excludedLiteLlmProviders.Count -gt 0) {
    $reportLines.Add('')
    $reportLines.Add('## LiteLLM entries excluded by provider allow-list')
    $reportLines.Add('')
    $reportLines.Add('The complete entry list is preserved in the machine diff; this report groups it by provider for review.')
    foreach ($group in ($excludedLiteLlmProviders |
            Group-Object { [string] $_.provider } | Sort-Object Name)) {
        $reportLines.Add("- ``$($group.Name)``: $($group.Count) entries")
    }
}
if ($excludedLiteLlmModelFamilies.Count -gt 0) {
    $reportLines.Add('')
    $reportLines.Add('## LiteLLM entries excluded by model-family gate')
    $reportLines.Add('')
    foreach ($entry in $excludedLiteLlmModelFamilies) {
        $reportLines.Add("- ``$($entry.sourceKey)`` -> ``$($entry.canonical)`` (provider ``$($entry.provider)``)")
    }
}
$reportLines.Add('')
$reportLines.Add("Upstream candidate: ``$candidatePath``")
$reportLines.Add("Maintained candidate: ``$maintainedCandidatePath``")
$reportLines.Add("Reviewed-alias candidate: ``$reviewedPriceAliasCandidatePath``")
$reportLines.Add("Machine diff: ``$diffPath``")
[IO.File]::WriteAllLines(
    $reportPath,
    $reportLines,
    [Text.UTF8Encoding]::new($false))

if ($Apply) {
    if ($null -ne $current -and $rateChanges.Count -gt 0 -and
        -not $AllowRateChanges) {
        throw 'Rate/source changes require -AllowRateChanges before -Apply.'
    }
    if ($removals.Count -gt 0 -and -not $AllowRemovals) {
        throw 'Price removals require -AllowRemovals before -Apply.'
    }
    if ($maintainedRuleRemovals.Count -gt 0 -and -not $AllowRemovals) {
        throw 'Maintained price removals require -AllowRemovals before -Apply.'
    }
    if ($maintainedRuleChanges.Count -gt 0 -and
        -not $AllowMaintainedChanges) {
        throw 'Maintained rule changes require -AllowMaintainedChanges before -Apply.'
    }
    if ($reviewedAliasRemovals.Count -gt 0 -and
        -not $AllowAliasRemovals) {
        throw 'Reviewed price-alias removals require -AllowAliasRemovals before -Apply.'
    }
    if ($reviewedAliasRetargets.Count -gt 0 -and
        -not $AllowAliasRetargeting) {
        throw 'Reviewed price-alias retargets require -AllowAliasRetargeting before -Apply.'
    }
    if ($reviewedAliasEvidenceChanges.Count -gt 0 -and
        -not $AllowMaintainedChanges) {
        throw 'Reviewed price-alias evidence changes require -AllowMaintainedChanges before -Apply.'
    }
    if (-not [string]::Equals(
            (Resolve-Path -LiteralPath (Split-Path -Parent $resolvedCurrentPath)).Path,
            (Resolve-Path -LiteralPath (Split-Path -Parent $sourcePath)).Path,
            [StringComparison]::OrdinalIgnoreCase) -or
        -not [string]::Equals(
            [IO.Path]::GetFileName($resolvedCurrentPath),
            [IO.Path]::GetFileName($sourcePath),
            [StringComparison]::OrdinalIgnoreCase)) {
        throw 'An alternate CurrentCatalogPath is review-only and cannot be applied.'
    }
    foreach ($baseline in @(
            [ordered] @{
                resolved = $resolvedCurrentMaintainedPath
                shipped = $maintainedSourcePath
                name = 'CurrentMaintainedCatalogPath'
            },
            [ordered] @{
                resolved = $resolvedCurrentReviewedPriceAliasPath
                shipped = $reviewedPriceAliasSourcePath
                name = 'CurrentReviewedPriceAliasCatalogPath'
            })) {
        if (-not [string]::Equals(
                [IO.Path]::GetFullPath([string] $baseline.resolved),
                [IO.Path]::GetFullPath([string] $baseline.shipped),
                [StringComparison]::OrdinalIgnoreCase)) {
            throw "An alternate $($baseline.name) is review-only and cannot be applied."
        }
    }
    $publishOperations = [System.Collections.Generic.List[object]]::new()
    if (-not $contentChanged) {
        Write-Output 'Upstream price catalog is unchanged; no file was replaced.'
    }
    else {
        $publishOperations.Add([ordered] @{
            name = 'upstream price catalog'
            candidate = $candidatePath
            destination = $sourcePath
            backup = Join-Path $maintenanceRoot 'upstream-token-prices.pre-apply.json'
        })
    }
    if (-not $maintainedContentChanged) {
        Write-Output 'Maintained price catalog is unchanged; no file was replaced.'
    }
    else {
        $publishOperations.Add([ordered] @{
            name = 'maintained price catalog'
            candidate = $maintainedCandidatePath
            destination = $maintainedSourcePath
            backup = Join-Path $maintenanceRoot 'official-api-token-prices.pre-apply.json'
        })
    }
    if (-not $reviewedAliasContentChanged) {
        Write-Output 'Reviewed price-alias catalog is unchanged; no file was replaced.'
    }
    else {
        $publishOperations.Add([ordered] @{
            name = 'reviewed price-alias catalog'
            candidate = $reviewedPriceAliasCandidatePath
            destination = $reviewedPriceAliasSourcePath
            backup = Join-Path $maintenanceRoot 'reviewed-price-aliases.pre-apply.json'
        })
    }

    if ($publishOperations.Count -gt 0 -and
        $PSCmdlet.ShouldProcess(
            $repoRoot,
            "Publish $($publishOperations.Count) reviewed price catalog(s)")) {
        foreach ($operation in $publishOperations) {
            Copy-Item -LiteralPath $operation.destination `
                -Destination $operation.backup -Force
        }
        try {
            foreach ($operation in $publishOperations) {
                Copy-Item -LiteralPath $operation.candidate `
                    -Destination $operation.destination -Force
            }
        }
        catch {
            $publishError = $_
            $rollbackErrors = [System.Collections.Generic.List[string]]::new()
            foreach ($operation in $publishOperations) {
                try {
                    Copy-Item -LiteralPath $operation.backup `
                        -Destination $operation.destination -Force
                }
                catch {
                    $rollbackErrors.Add(
                        "$($operation.destination): $($_.Exception.Message)")
                }
            }
            if ($rollbackErrors.Count -gt 0) {
                throw "Price catalog publish failed: $($publishError.Exception.Message). Rollback also failed: $($rollbackErrors -join '; ')"
            }
            throw "Price catalog publish failed and was rolled back: $($publishError.Exception.Message)"
        }
        foreach ($operation in $publishOperations) {
            Write-Output "Published $($operation.name): $($operation.destination)"
        }
    }
}

Write-Output "Upstream candidate: $candidatePath"
Write-Output "Maintained candidate: $maintainedCandidatePath"
Write-Output "Reviewed-alias candidate: $reviewedPriceAliasCandidatePath"
Write-Output "Diff: $diffPath"
Write-Output "Report: $reportPath"
