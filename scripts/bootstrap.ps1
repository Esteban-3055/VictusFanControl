$ErrorActionPreference = 'Stop'

Write-Host 'VictusFanControl bootstrap' -ForegroundColor Cyan

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw '.NET SDK was not found. Install the .NET 8 SDK from Microsoft and run this script again.'
}

$version = dotnet --version
Write-Host ".NET SDK: $version"

dotnet restore .\VictusFanControl.sln
dotnet build .\VictusFanControl.sln -c Release

Write-Host ''
Write-Host 'Build completed.' -ForegroundColor Green
Write-Host 'Next: .\scripts\list-sensors.ps1'
