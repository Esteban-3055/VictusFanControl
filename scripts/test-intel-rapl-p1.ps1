param(
    [switch]$WriteTest,
    [ValidateRange(10, 120)][int]$DurationSeconds = 60,
    [string]$ModulesDir = '',
    [switch]$SelfTest
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($ModulesDir)) { $ModulesDir = Join-Path $root 'modules' }
$module = Join-Path ([IO.Path]::GetFullPath($ModulesDir)) 'IntelMSR.bin'
$project = Join-Path $root 'src\VictusFanControl.CpuProbe\VictusFanControl.CpuProbe.csproj'
$exe = Join-Path $root 'src\VictusFanControl.CpuProbe\bin\Release\net8.0-windows\VictusFanControl.CpuProbe.exe'

Write-Host '1. Building the CPU-only probe (.NET 8, warnings treated as errors).'
& dotnet build $project -c Release -warnaserror
if ($LASTEXITCODE -ne 0) { throw 'Build failed; no CPU hardware test was started.' }
Write-Host '2. Running the simulated RAPL policy/recovery fixtures (no hardware I/O).'
& $exe --self-test
if ($LASTEXITCODE -ne 0) { throw 'Fixtures failed; no CPU hardware test was started.' }
if ($SelfTest) { return }

if (-not (Test-Path -LiteralPath $module)) { throw "Missing signed IntelMSR.bin: $module. Run scripts\setup-pawnio-modules.ps1." }
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
try {
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw 'Open PowerShell as Administrator and run this script again.'
    }
} finally { $identity.Dispose() }

$mode = '--observe'
if ($WriteTest) { $mode = '--write-test' }
$name = 'rapl-p1_' + (Get-Date -Format 'yyyy-MM-dd_HHmmss') + '_' + [Guid]::NewGuid().ToString('N').Substring(0, 8)
$directory = Join-Path $root ('logs\' + $name)
$zip = $directory + '.zip'
$consoleLog = $directory + '.console.txt'
Write-Host ''
Write-Host '3. Starting a separate CPU experiment. Finish the EC/WMI capture first.'
Write-Host 'Close VictusFanControl and its fan watchdog; keep firmware fan control and AC connected.'
if ($WriteTest) {
    Write-Host 'P1 WRITE TEST: original limits observed for 10 seconds, then both power fields reduced 20%.' -ForegroundColor Yellow
    Write-Host 'Enable, Clamp, Tau, Lock, reserved bits, GPU and fan settings are preserved.'
    Write-Host 'Start with CPU below 85 C. After IMMEDIATE_READBACK_EXACT_MATCH you may start a repeatable CPU workload.'
    Write-Host ("The reduced limits are observed for {0} seconds, then restoration is checked." -f $DurationSeconds)
    Write-Host 'Ctrl+C requests stop/restoration. Do not kill the CPU guardian process.'
} else {
    Write-Host ("READ-ONLY: {0} seconds. No CPU limit writes. Use a known workload to observe HP/DTT changes." -f $DurationSeconds)
}
Write-Host ("Evidence directory: {0}" -f $directory)

$exitCode = 2
try {
    & $exe $mode --module $module --output-dir $directory --duration-seconds $DurationSeconds 2>&1 |
        Tee-Object -FilePath $consoleLog
    $exitCode = $LASTEXITCODE
} finally {
    if (Test-Path -LiteralPath $directory) {
        # A timed-out native call may leave the guardian alive. Do not archive a
        # changing evidence folder or claim restoration until the guardian exits.
        $guardianAlive = $false
        $guardianFile = Join-Path $directory 'guardian.json'
        if (Test-Path -LiteralPath $guardianFile) {
            $guardian = Get-Content -LiteralPath $guardianFile -Raw | ConvertFrom-Json
            $process = Get-Process -Id $guardian.Id -ErrorAction SilentlyContinue
            if ($null -ne $process) {
                try { $guardianAlive = ($process.StartTime.ToUniversalTime().Ticks -eq [long]$guardian.StartTicks) }
                finally { $process.Dispose() }
            }
        }
        if ($guardianAlive) {
            Write-Host 'Guardian still running: restoration UNCONFIRMED. Evidence was not zipped.' -ForegroundColor Red
        } else {
            Write-Host '4. Saving console output and file hashes, then creating the evidence ZIP.'
            if (Test-Path -LiteralPath $consoleLog) { Copy-Item -LiteralPath $consoleLog -Destination (Join-Path $directory 'console.txt') }
            $manifest = @(Get-ChildItem -LiteralPath $directory -File | ForEach-Object {
                [pscustomobject]@{ File = $_.Name; Sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash; Bytes = $_.Length }
            })
            ConvertTo-Json -InputObject $manifest -Depth 5 | Set-Content -LiteralPath (Join-Path $directory 'file-hashes.json') -Encoding UTF8
            Compress-Archive -LiteralPath $directory -DestinationPath $zip
            (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash | Set-Content -LiteralPath ($zip + '.sha256') -Encoding ASCII
            Write-Host ("Attach this ZIP: {0}" -f $zip) -ForegroundColor Cyan
        }
    }
}
if ($exitCode -ne 0) {
    Write-Host ("Probe finished with code {0}; inspect the evidence and restoration result before another test." -f $exitCode) -ForegroundColor Yellow
    exit $exitCode
}
