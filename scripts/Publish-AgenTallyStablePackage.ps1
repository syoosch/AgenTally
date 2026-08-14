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

$implementation = Join-Path $PSScriptRoot 'Build-AgenTallyStableInstaller.ps1'
& $implementation @PSBoundParameters
