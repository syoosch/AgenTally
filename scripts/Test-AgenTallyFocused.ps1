#requires -Version 5.1

[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [string] $Filter,

    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Debug',

    [switch] $WindowedDesktop
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$testProject = Join-Path $repositoryRoot (
    'tests\AgenTally.Tests\AgenTally.Tests.csproj')
$categoryFilter = if ($WindowedDesktop) {
    'TestCategory=WindowedDesktop'
}
else {
    'TestCategory!=WindowedDesktop'
}
$effectiveFilter = "($Filter)&$categoryFilter"
$arguments = @(
    'test',
    '--project', $testProject,
    '--configuration', $Configuration,
    '--no-restore',
    '-p:AgenTallyIncludeWindowedDesktopTests=true',
    '--filter', $effectiveFilter
)

Push-Location $repositoryRoot
try {
    & dotnet @arguments
    if ($LASTEXITCODE -ne 0) {
        throw "Focused AgenTally tests failed with exit code $LASTEXITCODE."
    }
}
finally {
    Pop-Location
}
