param(
    [ValidateRange(10, 120)][int]$DurationSeconds = 20,
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

Write-Host 'CPU frequency / HWP READ-ONLY study' -ForegroundColor Cyan
Write-Host 'No MSR write function is invoked by this test.'
Write-Host ''

Write-Host '1. Building CpuProbe with warnings as errors.'
& dotnet build $project -c Release -warnaserror
if ($LASTEXITCODE -ne 0) {
    throw 'Build failed. No hardware probe was started.'
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

$name = 'cpu-frequency-readonly_' + (Get-Date -Format 'yyyy-MM-dd_HHmmss') + '_' +
    [Guid]::NewGuid().ToString('N').Substring(0, 8)
$directory = Join-Path $root ('logs\' + $name)
$zip = $directory + '.zip'

Write-Host ''
Write-Host '2. Pre-test conditions:' -ForegroundColor Cyan
Write-Host '   - Keep AC connected.'
Write-Host '   - Close VictusFanControl before running, so PerformanceGuardian does not alter CPU limits.'
Write-Host '   - Do not change Windows power settings during the capture.'
Write-Host ("   - Capture duration: {0} seconds." -f $DurationSeconds)
Write-Host ''
Write-Host 'The probe will inspect CPUID HWP support, MSR_PLATFORM_INFO,'
Write-Host 'MSR_TURBO_RATIO_LIMIT, IA32_PERF_STATUS and will safely test whether'
Write-Host 'the current signed PawnIO IntelMSR module permits READ access to'
Write-Host 'IA32_PM_ENABLE (0x770), IA32_HWP_CAPABILITIES (0x771), and'
Write-Host 'IA32_HWP_REQUEST (0x774). Access-denied is recorded as evidence, not bypassed.'
Write-Host ''

& $exe --frequency-readonly --module $module --output-dir $directory --duration-seconds $DurationSeconds
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
    Write-Host 'Attach that ZIP here for analysis.'
}

if ($exitCode -ne 0) {
    exit $exitCode
}
