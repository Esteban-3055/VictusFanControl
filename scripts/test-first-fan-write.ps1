$ErrorActionPreference = 'Stop'

Write-Host 'VictusFanControl - FIRST HP 88F8 FAN WRITE TEST' -ForegroundColor Cyan
Write-Host ''
Write-Host 'Test sequence:'
Write-Host '  1. Preflight the HP fan-level release + LegacyDefault WMI restore path.'
Write-Host '  2. Require a light-load telemetry baseline.'
Write-Host '  3. Apply fixed BIOS fan level 30,30 for at most 15 seconds.'
Write-Host '  4. Verify EC 0x34/0x35 acknowledge the commanded 30,30 setpoint.'
Write-Host '  5. Monitor all telemetry and both tachometers every second.'
Write-Host '  6. Require stable RPM acknowledgement.'
Write-Host '  7. Release the fixed level with FF,FF and restore HP FanMode=LegacyDefault after ANY attempted write.'
Write-Host ''
Write-Host 'Keep OMEN Gaming Hub open with your normal undervolt.'
Write-Host 'Do not run a game or stress test during this first write validation.'
Write-Host 'Before continuing, verify the undervolt shown in OMEN Gaming Hub.' -ForegroundColor Yellow
Write-Host ''

$pre = Read-Host 'Type UNDERVOLT-OK after checking it'
if ($pre -cne 'UNDERVOLT-OK') {
    Write-Host 'Cancelled.'
    exit 1
}

Write-Host ''
Write-Host 'Step 1: preflighting HP fan-level release + LegacyDefault restore path...' -ForegroundColor Cyan
dotnet run --project .\src\VictusFanControl -c Release -- --restore-hp-auto
if ($LASTEXITCODE -ne 0) {
    Write-Error 'HP-auto restore preflight failed. Fan-write test will NOT run.'
    exit $LASTEXITCODE
}

Write-Host ''
$confirm = Read-Host 'Restore preflight succeeded. Type FAN30 to run the 15-second level 30,30 test'
if ($confirm -cne 'FAN30') {
    Write-Host 'Cancelled before fan-level write.'
    exit 1
}

Write-Host ''
dotnet run --project .\src\VictusFanControl -c Release -- --first-fan-write-test --write-token 88F8-FAN30
$testExit = $LASTEXITCODE

Write-Host ''
Write-Host 'Check the undervolt value in OMEN Gaming Hub again.' -ForegroundColor Yellow
$post = Read-Host 'Type SAME if it is unchanged, or CHANGED if it changed'

if ($post -ceq 'SAME') {
    Write-Host 'Undervolt preservation: CONFIRMED by user.' -ForegroundColor Green
} elseif ($post -ceq 'CHANGED') {
    Write-Warning 'Undervolt preservation: FAILED/CHANGED. Do not continue fan-control development until investigated.'
    if ($testExit -eq 0) { $testExit = 27 }
} else {
    Write-Warning 'Undervolt preservation was not confirmed.'
    if ($testExit -eq 0) { $testExit = 28 }
}

exit $testExit
