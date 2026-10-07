param(
    [string]$ModulesDir = ''
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($ModulesDir)) {
    $ModulesDir = Join-Path $root 'modules'
}

$module = Join-Path ([IO.Path]::GetFullPath($ModulesDir)) 'IntelMSR.bin'
$project = Join-Path $root 'src\VictusFanControl.CpuProbe\VictusFanControl.CpuProbe.csproj'
$exe = Join-Path $root 'src\VictusFanControl.CpuProbe\bin\Release\net8.0-windows\VictusFanControl.CpuProbe.exe'

Write-Host 'OC Mailbox ratio capability query - bounded diagnostic' -ForegroundColor Cyan
Write-Host ''
Write-Host 'IMPORTANT:' -ForegroundColor Yellow
Write-Host '  This is NOT physically read-only. Intel OC mailbox queries are submitted by'
Write-Host '  writing one fixed query command to MSR 0x150, then reading the response.'
Write-Host '  The only command used is 0x1C (per-core/favored-core ratio capability query).'
Write-Host '  No caller-controlled MSR address, command or payload is accepted.'
Write-Host '  The probe verifies that 0x1AD (turbo ratios) and 0x610 (RAPL limits)'
Write-Host '  are bit-identical before and after the query sequence.'
Write-Host ''

Write-Host '1. Building CpuProbe with warnings as errors.'
& dotnet build $project -c Release -warnaserror
if ($LASTEXITCODE -ne 0) {
    throw 'Build failed. No hardware query was started.'
}

if (-not (Test-Path -LiteralPath $module)) {
    throw "Missing signed IntelMSR.bin: $module. Run .\scripts\setup-pawnio-modules.ps1 first."
}

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
try {
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw 'Open PowerShell as Administrator and run this script again.'
    }
}
finally {
    $identity.Dispose()
}

$name = 'oc-mailbox-ratio-query_' + (Get-Date -Format 'yyyy-MM-dd_HHmmss') + '_' +
    [Guid]::NewGuid().ToString('N').Substring(0, 8)
$directory = Join-Path $root ('logs\' + $name)
$zip = $directory + '.zip'

Write-Host ''
Write-Host '2. Before continuing:' -ForegroundColor Cyan
Write-Host '   - Keep AC connected.'
Write-Host '   - Close VictusFanControl.'
Write-Host '   - Close ThrottleStop, Intel XTU, UXTU or any CPU tuning utility.'
Write-Host '   - Leave the system mostly idle during the query.'
Write-Host ''
Write-Host 'The first mailbox query is issued only on logical processor 0.'
Write-Host 'If that query is rejected, busy, or returns a non-zero completion code,'
Write-Host 'the probe stops without querying the remaining logical processors.'
Write-Host ''

& $exe --oc-mailbox-ratio-query --module $module --output-dir $directory
$exitCode = $LASTEXITCODE

if (Test-Path -LiteralPath $directory) {
    $manifest = @(Get-ChildItem -LiteralPath $directory -File | ForEach-Object {
        [pscustomobject]@{
            File = $_.Name
            Sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
            Bytes = $_.Length
        }
    })

    ConvertTo-Json -InputObject $manifest -Depth 5 |
        Set-Content -LiteralPath (Join-Path $directory 'file-hashes.json') -Encoding UTF8

    Compress-Archive -LiteralPath $directory -DestinationPath $zip
    (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash |
        Set-Content -LiteralPath ($zip + '.sha256') -Encoding ASCII

    Write-Host ''
    Write-Host ("Evidence ZIP: {0}" -f $zip) -ForegroundColor Green
    Write-Host 'Attach the ZIP and its .sha256 file here for analysis.'
}

if ($exitCode -ne 0) {
    Write-Host ''
    Write-Host ("Probe finished with code {0}. Do not repeat it blindly; send the evidence first." -f $exitCode) -ForegroundColor Yellow
    exit $exitCode
}
