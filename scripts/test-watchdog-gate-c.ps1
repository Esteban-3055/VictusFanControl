$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
Set-Location $repoRoot

Write-Host 'VictusFanControl - WATCHDOG GATE C (SYNTHETIC LEASE/JOURNAL/PIPE)' -ForegroundColor Cyan
Write-Host ''
Write-Host 'No real fan or EC write is performed by this Gate C test.'
Write-Host ''

dotnet build .\VictusFanControl.sln -c Release -warnaserror
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}

dotnet run --project .\src\VictusFanControl.Watchdog -c Release --no-build -- --gate-c-self-test
exit $LASTEXITCODE
