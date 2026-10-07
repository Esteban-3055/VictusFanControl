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

Write-Host 'IA-core OC Mailbox capability query - bounded diagnostic' -ForegroundColor Cyan
Write-Host ''
Write-Host 'This probe submits only three fixed query commands to MSR 0x150:' -ForegroundColor Yellow
Write-Host '  0x01 GET_OC_CAPABILITIES (IA-core domain 0)'
Write-Host '  0x10 GET_VOLTAGE_FREQUENCY (IA-core domain 0)'
Write-Host '  0x07 GET_FUSED_P0_RATIO_VOLTAGE (IA-core domain 0)'
Write-Host ''
Write-Host 'No ratio/voltage/power mutation command is present.'
Write-Host 'The probe verifies MSR 0x1AD and 0x610 are bit-identical before/after.'
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

$name = 'oc-mailbox-ia-core-query_' + (Get-Date -Format 'yyyy-MM-dd_HHmmss') + '_' +
    [Guid]::NewGuid().ToString('N').Substring(0, 8)
$directory = Join-Path $root ('logs\' + $name)
$zip = $directory + '.zip'

Write-Host ''
Write-Host '2. Preconditions:' -ForegroundColor Cyan
Write-Host '   - AC connected.'
Write-Host '   - VictusFanControl closed.'
Write-Host '   - ThrottleStop / Intel XTU / UXTU closed.'
Write-Host '   - Keep the system mostly idle during the query.'
Write-Host ''

& $exe --oc-mailbox-ia-core-query --module $module --output-dir $directory
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
    Write-Host 'Attach the ZIP and its .sha256 file here.'
}

if ($exitCode -ne 0) {
    Write-Host ''
    Write-Host ("Probe finished with code {0}. Do not repeat it blindly; send the evidence first." -f $exitCode) -ForegroundColor Yellow
    exit $exitCode
}
