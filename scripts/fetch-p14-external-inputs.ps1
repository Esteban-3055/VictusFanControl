param(
    [Parameter(Mandatory = $true)]
    [string]$OutputDirectory
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$contract = Get-Content -LiteralPath (Join-Path $root 'release\p14-external-inputs.json') -Raw | ConvertFrom-Json
$pawn = $contract.pawnIoModules
$out = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Force -Path $out | Out-Null
$path = Join-Path $out ([string]$pawn.archiveName)

Invoke-WebRequest -Uri ([string]$pawn.url) -OutFile $path
$actual = (Get-FileHash -Algorithm SHA256 -LiteralPath $path).Hash.ToLowerInvariant()
$expected = ([string]$pawn.sha256).ToLowerInvariant()
if ($actual -ne $expected) {
    Remove-Item -LiteralPath $path -Force -ErrorAction SilentlyContinue
    throw "P14.3 external-input SHA-256 mismatch. Expected $expected, got $actual."
}

Write-Host "P14.3 external input verified: $path" -ForegroundColor Green
