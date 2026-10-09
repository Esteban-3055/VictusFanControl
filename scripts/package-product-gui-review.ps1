param([Parameter(Mandatory=$true)][string]$RcArtifactDirectory,[Parameter(Mandatory=$true)][string]$OutputDirectory,[Parameter(Mandatory=$true)][string]$SourceHead)
$ErrorActionPreference='Stop'
& (Join-Path $PSScriptRoot 'package-product-v1.ps1') @PSBoundParameters
