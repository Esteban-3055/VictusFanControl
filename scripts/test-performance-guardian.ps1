param(
    [switch]$SelfTest
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root 'src\VictusFanControl.PerformanceGuardian\VictusFanControl.PerformanceGuardian.csproj'
$exe = Join-Path $root 'src\VictusFanControl.PerformanceGuardian\bin\Release\net8.0-windows\VictusFanControl.PerformanceGuardian.exe'

Write-Host '1. Building detached Performance Guardian software-only fixture.'
& dotnet build $project -c Release -warnaserror
if ($LASTEXITCODE -ne 0) {
    throw 'Performance Guardian build failed.'
}

Write-Host '2. Running pure authority/nonce state-machine self-test. No hardware I/O.'
& $exe --self-test
if ($LASTEXITCODE -ne 0) {
    throw 'Performance Guardian authority self-test failed.'
}

$fixtureRoot = Join-Path ([IO.Path]::GetTempPath()) ('VictusFanControl-PerformanceGuardian-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $fixtureRoot | Out-Null

try {
    Write-Host '3. Running native detached-process lifecycle fixture. No hardware I/O.'
    & $exe --process-fixture $fixtureRoot
    if ($LASTEXITCODE -ne 0) {
        throw 'Performance Guardian detached-process fixture failed.'
    }
}
finally {
    Remove-Item -LiteralPath $fixtureRoot -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Host 'Performance Guardian software-only qualification: PASS'
