$ErrorActionPreference = 'Stop'

Write-Host 'VictusFanControl - FIRST HP 88F8 FAN WRITE TEST' -ForegroundColor Cyan
Write-Host ''
Write-Host 'Test sequence:'
Write-Host '  1. Verify HP LegacyDefault restore works.'
Write-Host '  2. Apply fixed BIOS fan level 30,30 for at most 15 seconds.'
Write-Host '  3. Monitor all telemetry and both tachometers every second.'
Write-Host '  4. Abort on telemetry/safety failure or missing RPM acknowledgement.'
Write-Host '  5. Restore HP FanMode=LegacyDefault in a finally block.'
Write-Host ''
Write-Host 'Keep OMEN Gaming Hub open with your normal undervolt.'
Write-Host 'Before continuing, verify the undervolt shown in OMEN Gaming Hub.' -ForegroundColor Yellow
Write-Host ''

$pre = Read-Host 'Type UNDERVOLT-OK after checking it'
if ($pre -cne 'UNDERVOLT-OK') {
    Write-Host 'Cancelled.'
    exit 1
}

Write-Host ''
Write-Host 'Step 1: validating HP firmware restore...' -ForegroundColor Cyan
dotnet run --project .\src\VictusFanControl -c Release -- --restore-hp-auto
if ($LASTEXITCODE -ne 0) {
    Write-Error 'LegacyDefault restore validation failed. Fan-write test will NOT run.'
    exit $LASTEXITCODE
}

Write-Host ''
$confirm = Read-Host 'Restore succeeded. Type FAN30 to run the 15-second level 30,30 test'
if ($confirm -cne 'FAN30') {
    Write-Host 'Cancelled before fan-level write.'
    exit 1
}

Write-Host ''
dotnet run --project .\src\VictusFanControl -c Release -- --first-fan-write-test
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
