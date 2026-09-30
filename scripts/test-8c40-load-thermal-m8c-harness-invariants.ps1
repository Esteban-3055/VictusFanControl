$ErrorActionPreference='Stop'

$repoRoot=Split-Path -Parent $PSScriptRoot
$harnessPath=Join-Path $PSScriptRoot 'test-8c40-load-thermal-m8c.ps1'
$failsafePath=Join-Path $PSScriptRoot 'watchdog-m8c-service-failsafe-8c40.ps1'
$controllerPath=Join-Path $repoRoot 'src\VictusFanControl\Hardware\Hp\Hp8C40M8CPhysicalThermalPreemptionQualificationTest.cs'

$harness=Get-Content $harnessPath -Raw
$failsafe=Get-Content $failsafePath -Raw
$controller=Get-Content $controllerPath -Raw
$profile=Get-Content (Join-Path $repoRoot 'profiles\HP-8C40.json') -Raw | ConvertFrom-Json
$doc=Get-Content (Join-Path $repoRoot 'docs\LOAD_THERMAL_8C40_M8.md') -Raw

function Assert-Contains([string]$Text,[string]$Needle,[string]$Message){
    if($Text.IndexOf($Needle,[StringComparison]::Ordinal)-lt 0){throw $Message}
}
function Assert-NotContains([string]$Text,[string]$Needle,[string]$Message){
    if($Text.IndexOf($Needle,[StringComparison]::Ordinal)-ge 0){throw $Message}
}
function Assert-False([bool]$Value,[string]$Message){
    if($Value){throw $Message}
}
function Assert-True([bool]$Value,[string]$Message){
    if(-not $Value){throw $Message}
}

$barrier=$harness.IndexOf('if(-not [bool]$profile.loadThermalM8Qualification.m8b.physicalPassed',[StringComparison]::Ordinal)
if($barrier -lt 0){throw 'M8C parent physical profile barrier is missing.'}

foreach($activeAnchor in @(
    'Assert-Administrator',
    'Start-Service -Name $serviceName',
    'Start-Process powershell.exe',
    'Start-M8CTrackedChild',
    '& dotnet $cli --probe-8c40-setpoint'
)){
    $index=$harness.IndexOf($activeAnchor,[StringComparison]::Ordinal)
    if($index -lt 0 -or $barrier -ge $index){
        throw ("M8C profile barrier must precede active harness boundary: {0}" -f $activeAnchor)
    }
}

foreach($needle in @(
    'm8b.physicalPassed',
    'm8c.physicalExecutionAuthorized',
    'M8C PHYSICAL BLOCKED',
    '8C40-M8C-THERMAL50',
    'watchdog-m8c-service-failsafe-8c40.ps1',
    "Run-M8CSubcycle -Case 'cpu'",
    "Run-M8CSubcycle -Case 'gpu'",
    'Assert-M8COwnedJournal',
    'M8C-CONTINUE',
    'M8C_SYNTHETIC_QUALIFICATION_ONLY',
    'Assert-CausalServiceLog',
    'PREPARE < WRITE_INTENT < COMMIT < RESTORE_BEGIN < RELEASE',
    'Assert-StableFirmwareOwned',
    'finalJournalAbsent',
    'Manual/Stopped',
    'm8c-tracked-child.ps1',
    'Start-M8CTrackedChild',
    'Wait-M8CTrackedChildExitCode',
    'M8C independent failsafe did not publish ARMED evidence before controller launch.',
    'failsafePid=',
    'failsafeLogPresent=',
    'package-latest-m8c-evidence.ps1',
    'M8C automatic evidence package:'
)){
    Assert-Contains $harness $needle ("M8C parent harness invariant missing: {0}" -f $needle)
}

$cpuIndex=$harness.IndexOf("Run-M8CSubcycle -Case 'cpu'",[StringComparison]::Ordinal)
$gpuIndex=$harness.IndexOf("Run-M8CSubcycle -Case 'gpu'",[StringComparison]::Ordinal)
if($cpuIndex -lt 0 -or $gpuIndex -lt 0 -or $cpuIndex -ge $gpuIndex){
    throw 'M8C must execute the CPU and GPU cases as separate ordered subcycles.'
}

$failSafeStart=$harness.IndexOf('$failsafe=Start-M8CFailsafe',[StringComparison]::Ordinal)
$controllerStart=$harness.IndexOf('$controller=Start-M8CTrackedChild',[StringComparison]::Ordinal)
if($failSafeStart -lt 0 -or $controllerStart -lt 0 -or $failSafeStart -ge $controllerStart){
    throw 'M8C must arm the independent delayed failsafe before the real controller.'
}

$ownedAssert=$harness.IndexOf('Assert-M8COwnedJournal -Journal $journal',[StringComparison]::Ordinal)
$continueWrite=$harness.IndexOf("'M8C-CONTINUE' | Set-Content",[StringComparison]::Ordinal)
if($ownedAssert -lt 0 -or $continueWrite -lt 0 -or $ownedAssert -ge $continueWrite){
    throw 'M8C parent must prove exact durable OWNED 50/50 before releasing synthetic injection.'
}

foreach($needle in @(
    'WRITE_ARMED',
    'OWNED',
    'RESTORING',
    'HP-8C40-9D0R1LA-F18',
    'Pending.Cpu -eq 50',
    'Pending.Gpu -eq 50',
    'Owned.Cpu -eq 50',
    'Owned.Gpu -eq 50',
    'Test-ExactControllerAlive',
    'Kill-ExactController',
    'Wait-JournalGone'
)){
    Assert-Contains $failsafe $needle ("M8C delayed failsafe invariant missing: {0}" -f $needle)
}

Assert-Contains $failsafe 'M8C FAILSAFE ARMED:' 'M8C delayed failsafe must publish durable ARMED evidence before sleeping.'
Assert-NotContains $failsafe 'SetFanLevel(' 'M8C delayed failsafe must not issue ordinary fan targets.'
Assert-NotContains $failsafe '--restore-hp-auto' 'M8C delayed failsafe must not invoke direct HP restore.'
Assert-Contains $controller 'public static readonly bool PhysicalExecutionAuthorized = false;' 'M8C compiled controller must be re-blocked after attempt 1 FAIL_CLOSED.'
$trackedHelper=Get-Content (Join-Path $PSScriptRoot 'm8c-tracked-child.ps1') -Raw
$trackedSelfTest=Get-Content (Join-Path $PSScriptRoot 'test-8c40-m8c-tracked-child-selftest.ps1') -Raw
Assert-Contains $trackedHelper 'New-Object System.Diagnostics.Process' 'M8C helper must own the native process object.'
Assert-Contains $trackedHelper '$Process.ExitCode' 'M8C helper must read ExitCode from the owned native process.'
Assert-Contains $trackedSelfTest 'foreach($expected in @(0,7))' 'M8C helper self-test must cover exit 0 and 7.'
Assert-NotContains $harness '$controller=Start-Process' 'M8C controller must not use PowerShell Start-Process -PassThru.'

Assert-True ([bool]$profile.loadThermalM8Qualification.m8b.physicalPassed) 'M8C harness preparation must preserve recorded M8B physical PASS.'
Assert-False ([bool]$profile.loadThermalM8Qualification.m8c.physicalExecutionAuthorized) 'M8C harness retry must remain profile-blocked after attempt 1 FAIL_CLOSED.'
Assert-False ([bool]$profile.loadThermalM8Qualification.m8c.physicalPassed) 'M8C harness preparation must not mark physical PASS.'
Assert-False ([bool]$profile.loadThermalM8Qualification.automaticPolicyEnabled) 'Automatic policy must remain OFF.'

foreach($needle in @(
    'M8C parent physical harness preparation',
    'two independent physical subcycles',
    'profile gate',
    'm8b.physicalPassed=true',
    'physicalExecutionAuthorized=false',
    'M8C attempt 1 - FAIL_CLOSED before READY'
)){
    Assert-Contains $doc $needle ("M8C parent harness documentation missing: {0}" -f $needle)
}

Write-Host 'HP 8C40 M8C parent physical harness preparation invariant: PASS' -ForegroundColor Green
