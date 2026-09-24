$ErrorActionPreference = 'Stop'

Write-Host 'VictusFanControl - SUSPEND WHILE CUSTOM HARDWARE GATE' -ForegroundColor Cyan
Write-Host ''
Write-Host 'This test WILL put Windows to sleep after explicit confirmation.' -ForegroundColor Yellow
Write-Host 'Save unrelated work before continuing.' -ForegroundColor Yellow
Write-Host ''
Write-Host 'Expected real path:'
Write-Host '  Healthy -> SafetyGate -> Coordinator Custom -> 30/30 ACK'
Write-Host '  -> external Windows Suspend request'
Write-Host '  -> WM_POWERBROADCAST/PBT_APMSUSPEND'
Write-Host '  -> synchronous FF,FF + LegacyDefault'
Write-Host '  -> pre-sleep EC FF/FF verification'
Write-Host '  -> sleep -> resume -> telemetry recovery -> Healthy/Firmware'
Write-Host ''
Write-Host 'Automatic fan policy remains OFF.'
Write-Host 'Keep OMEN Gaming Hub open with your normal undervolt.'
Write-Host 'Close OmenMon, OmenMon-Reborn and the normal VictusFanControl GUI.'
Write-Host 'Do NOT terminate the test through Task Manager.'
Write-Host ''

foreach ($name in @('OmenMon', 'OmenMon-Reborn', 'VictusFanControl.App')) {
    $running = Get-Process -Name $name -ErrorAction SilentlyContinue
    if ($running) {
        Write-Error "Refusing test while process '$name' is running. Close it and retry."
        exit 1
    }
}

Write-Host 'Step 1: building with warnings as errors...' -ForegroundColor Cyan
dotnet build .\VictusFanControl.sln -c Release -warnaserror
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Write-Host ''
Write-Host 'Step 2: synthetic safety/coordinator/backend regressions...' -ForegroundColor Cyan
dotnet run --project .\src\VictusFanControl -c Release --no-build -- --safety-self-test
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
dotnet run --project .\src\VictusFanControl -c Release --no-build -- --control-self-test
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
dotnet run --project .\src\VictusFanControl -c Release --no-build -- --hp-backend-self-test
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Write-Host ''
Write-Host 'Step 3: read-only live hardware preflight...' -ForegroundColor Cyan
dotnet run --project .\src\VictusFanControl -c Release --no-build -- --probe-backends
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
dotnet run --project .\src\VictusFanControl -c Release --no-build -- --probe-88f8-ec-state
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Write-Host ''
Write-Host 'Verify the CPU undervolt shown in OMEN Gaming Hub.' -ForegroundColor Yellow
$pre = Read-Host 'Type UNDERVOLT-OK after checking it'
if ($pre -cne 'UNDERVOLT-OK') {
    Write-Host 'Cancelled before any fan write.'
    exit 1
}

Write-Host ''
Write-Host 'The next confirmation arms 30/30 and then intentionally suspends Windows.' -ForegroundColor Yellow
$confirm = Read-Host 'Type SUSPEND30 to continue'
if ($confirm -cne 'SUSPEND30') {
    Write-Host 'Cancelled before any fan write.'
    exit 1
}

$root = Join-Path $env:LOCALAPPDATA 'VictusFanControl'
$ready = Join-Path $root 'suspend-custom.ready'
$result = Join-Path $root 'suspend-custom.result'
$log = Join-Path (Join-Path $root 'logs') ("events-{0}.log" -f (Get-Date -Format 'yyyy-MM-dd'))

Remove-Item $ready -Force -ErrorAction SilentlyContinue
Remove-Item $result -Force -ErrorAction SilentlyContinue

$app = Resolve-Path '.\src\VictusFanControl.App\bin\Release\net8.0-windows\VictusFanControl.App.exe'

Write-Host ''
Write-Host 'Step 4: launching explicit GUI suspend-test mode...' -ForegroundColor Cyan
$proc = Start-Process -FilePath $app -ArgumentList @(
    '--suspend-custom-test',
    '--suspend-test-token',
    '88F8-SUSPEND30'
) -PassThru

$deadline = (Get-Date).AddSeconds(60)
while (-not (Test-Path $ready)) {
    if ($proc.HasExited) {
        Write-Error "VictusFanControl.App exited before the suspend test armed. ExitCode=$($proc.ExitCode)"
        if (Test-Path $result) { Get-Content $result }
        exit 62
    }

    if ((Get-Date) -gt $deadline) {
        try { $proc.CloseMainWindow() | Out-Null } catch {}
        Write-Error 'Timed out waiting for the GUI to arm Custom 30/30.'
        exit 63
    }

    Start-Sleep -Milliseconds 250
}

Write-Host 'GUI reports READY:' -ForegroundColor Green
Get-Content $ready
Write-Host ''
Write-Host 'Step 5: requesting Windows Suspend from this separate PowerShell process...' -ForegroundColor Cyan

Add-Type -AssemblyName System.Windows.Forms
$accepted = [System.Windows.Forms.Application]::SetSuspendState(
    [System.Windows.Forms.PowerState]::Suspend,
    $false,
    $false)

if (-not $accepted) {
    Write-Warning 'Windows reported that the suspend request was not accepted.'
}

Write-Host ''
Write-Host 'System resumed. Waiting for VictusFanControl telemetry recovery and result...' -ForegroundColor Cyan

$deadline = (Get-Date).AddSeconds(90)
while (-not (Test-Path $result)) {
    if ((Get-Date) -gt $deadline) {
        Write-Error 'Timed out waiting for post-resume hardware-test result.'
        exit 64
    }

    Start-Sleep -Milliseconds 500
}

Write-Host ''
Write-Host 'Hardware-test result:' -ForegroundColor Cyan
$resultText = Get-Content $result -Raw
Write-Host $resultText

try {
    $proc.WaitForExit(15000) | Out-Null
} catch {}

Write-Host ''
Write-Host 'Relevant persistent lifecycle log lines:' -ForegroundColor Cyan
if (Test-Path $log) {
    Get-Content $log |
        Select-String -Pattern 'SUSPEND TEST|Fan authority:|Suspend detected|Resume detected|Telemetry healthy after lifecycle recovery' |
        Select-Object -Last 80 |
        ForEach-Object { Write-Host $_.Line }
}

Write-Host ''
Write-Host 'Check the undervolt value in OMEN Gaming Hub again.' -ForegroundColor Yellow
$post = Read-Host 'Type SAME if it is unchanged, or CHANGED if it changed'

$exitCode = if ($resultText.StartsWith('PASS|')) { 0 } else { 65 }

if ($post -ceq 'SAME') {
    Write-Host 'Undervolt preservation: CONFIRMED by user.' -ForegroundColor Green
} elseif ($post -ceq 'CHANGED') {
    Write-Warning 'Undervolt preservation: FAILED/CHANGED. Stop hardware-control development and investigate.'
    if ($exitCode -eq 0) { $exitCode = 66 }
} else {
    Write-Warning 'Undervolt preservation was not confirmed.'
    if ($exitCode -eq 0) { $exitCode = 67 }
}

exit $exitCode
