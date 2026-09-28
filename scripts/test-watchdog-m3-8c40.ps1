$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
Set-Location $repoRoot

$serviceName = 'VictusFanControlWatchdogM3'
$serviceRoot = Join-Path $env:ProgramData 'VictusFanControl\WatchdogM3'
$stateDir = Join-Path $serviceRoot 'state'
$handoffPath = Join-Path $stateDir 'm3-8c40.handoff.json'
$resultPath = Join-Path $stateDir 'm3-8c40.result.json'
$journalPath = Join-Path $stateDir 'lease.json'
$logPath = Join-Path $serviceRoot ("logs\watchdog-m3-8c40-{0}.log" -f (Get-Date -Format 'yyyy-MM-dd'))
$cli = Join-Path $repoRoot 'src\VictusFanControl\bin\Release\net8.0-windows\VictusFanControl.dll'
$modulesDir = Join-Path $repoRoot 'modules'
$requiredToken = '8C40-M3-RESTORE30'

function Assert-Administrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'This M3 hardware gate must be run from an elevated PowerShell.' }
}

function Read-8C40Setpoint {
    $output = (& dotnet $cli --probe-8c40-setpoint --modules-dir $modulesDir 2>&1 | Out-String)
    Write-Host $output.TrimEnd()
    $line = ($output -split "[\r\n]+" | Where-Object { $_ -match '^setpoint CPU=' } | Select-Object -Last 1)
    if (-not $line) { throw "Could not parse HP 8C40 read-only setpoint probe. Raw output: $output" }
    $match = [regex]::Match($line, '^setpoint CPU=(\d+) GPU=(\d+)$')
    if (-not $match.Success) { throw "Could not parse HP 8C40 setpoints from: $line" }
    [pscustomobject]@{ Cpu = [int]$match.Groups[1].Value; Gpu = [int]$match.Groups[2].Value; Raw = $line }
}

function Show-Diagnostics {
    Write-Host ''
    if (Test-Path $resultPath) { Write-Host 'M3 result:' -ForegroundColor Cyan; Get-Content $resultPath }
    if (Test-Path $logPath) { Write-Host ''; Write-Host 'Recent M3 log:' -ForegroundColor Cyan; Get-Content $logPath | Select-Object -Last 80 }
    Write-Host ''
    & sc.exe queryex $serviceName | Out-Host
}

Assert-Administrator

Write-Host 'VictusFanControl - HP 8C40 WATCHDOG M3 (RESTORE ONLY)' -ForegroundColor Cyan
Write-Host ''
Write-Host 'This gate performs exactly one bounded active sequence:'
Write-Host '  firmware FF/FF -> VFC-owned 30/30 -> LocalSystem M3 restore -> verified FF/FF'
Write-Host ''
Write-Host 'M3 service has no ordinary SetFanLevel path and no watchdog lease.' -ForegroundColor Yellow
Write-Host ''

foreach ($name in @('OmenMon','OmenMon-Reborn','VictusFanControl.App')) {
    if (Get-Process -Name $name -ErrorAction SilentlyContinue) { throw "Refusing M3 while process '$name' is running. Close it and retry." }
}

foreach ($legacyName in @('VictusFanControlWatchdogGateA','VictusFanControlWatchdogGateB','VictusFanControlWatchdog')) {
    if (Get-Service -Name $legacyName -ErrorAction SilentlyContinue) { throw "M3 refused because historical 88F8 service '$legacyName' is installed." }
}

$m2 = Get-Service -Name 'VictusFanControlWatchdogM2' -ErrorAction SilentlyContinue
if ($m2 -and $m2.Status -ne 'Stopped') { throw 'M3 refused while VictusFanControlWatchdogM2 is running.' }

Write-Host 'Step 1: build + synthetic M3/regression gates...' -ForegroundColor Cyan
dotnet build .\VictusFanControl.sln -c Release -warnaserror
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
dotnet run --project .\src\VictusFanControl.Watchdog -c Release --no-build -- --m3-8c40-self-test
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
dotnet run --project .\src\VictusFanControl -c Release --no-build -- --bios-contract-self-test
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
dotnet run --project .\src\VictusFanControl -c Release --no-build -- --hp-backend-self-test
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Write-Host ''
Write-Host 'Step 2: clean firmware-owned baseline...' -ForegroundColor Cyan
$baseline = Read-8C40Setpoint
if ($baseline.Cpu -ne 255 -or $baseline.Gpu -ne 255) { throw "M3 requires baseline FF/FF; observed $($baseline.Cpu)/$($baseline.Gpu). No write was issued." }

Write-Host ''
Write-Host 'Step 3: install isolated M3 restore-only service...' -ForegroundColor Cyan
& (Join-Path $PSScriptRoot 'install-watchdog-m3-8c40.ps1')

Write-Host ''
Write-Host 'ACTIVE-WRITE BOUNDARY' -ForegroundColor Yellow
Write-Host 'The next step will issue one VFC 30/30 command, then the LocalSystem service' -ForegroundColor Yellow
Write-Host 'will issue one FF/FF -> LegacyDefault restore transaction.' -ForegroundColor Yellow
Write-Host ''
$token = Read-Host "Type exactly $requiredToken to continue"
if ($token -cne $requiredToken) { throw 'M3 cancelled: acknowledgement token did not match.' }

Remove-Item $resultPath -Force -ErrorAction SilentlyContinue
Remove-Item $handoffPath -Force -ErrorAction SilentlyContinue
Get-ChildItem $stateDir -Filter 'm3-8c40.handoff.json.claimed.*.json' -ErrorAction SilentlyContinue | Remove-Item -Force -ErrorAction SilentlyContinue
Remove-Item $journalPath -Force -ErrorAction SilentlyContinue

Write-Host ''
Write-Host 'Step 4: start LocalSystem M3 service in restore-wait state...' -ForegroundColor Cyan
Start-Service -Name $serviceName

try {
    Write-Host 'Step 5: arm one VFC-owned 30/30 handoff...' -ForegroundColor Cyan
    $m3Args = @('--8c40-m3-arm','--8c40-m3-arm-token',$requiredToken,'--8c40-m3-handoff-path',$handoffPath,'--8c40-m3-result-path',$resultPath,'--modules-dir',$modulesDir)
    & dotnet $cli @m3Args
    $armExit = $LASTEXITCODE
    if ($armExit -ne 0) { Show-Diagnostics; throw "M3 armer failed with exit code $armExit." }
} finally {
    $service = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
    if ($service -and $service.Status -ne 'Stopped') { Stop-Service -Name $serviceName -Force -ErrorAction SilentlyContinue }
}

if (-not (Test-Path $resultPath)) { Show-Diagnostics; throw 'M3 result marker was not produced.' }
$result = Get-Content $resultPath -Raw | ConvertFrom-Json

Write-Host ''
Write-Host 'M3 LocalSystem result:' -ForegroundColor Cyan
Write-Host "Success          : $($result.Success)"
Write-Host "ServiceRunId     : $($result.ServiceRunId)"
Write-Host "ArmRunId         : $($result.ArmRunId)"
Write-Host "Session          : $($result.SessionId)"
Write-Host "Account          : $($result.AccountName)"
Write-Host "SID              : $($result.UserSid)"
Write-Host "Target           : $($result.TargetProfileId)"
Write-Host "Handoff claimed  : $($result.HandoffClaimed)"
Write-Host "Handoff valid    : $($result.HandoffValidated)"
Write-Host "Armer valid      : $($result.ArmerIdentityValidated)"
Write-Host "Before           : $($result.BeforeCpuSetpoint)/$($result.BeforeGpuSetpoint)"
Write-Host "Restore call     : $($result.RestoreCallSucceeded)"
Write-Host "Verified FF/FF   : $($result.VerifiedFfFf)"
Write-Host "After            : $($result.AfterCpuSetpoint)/$($result.AfterGpuSetpoint)"
Write-Host "GetFanLevel      : $($result.BiosCpuCurrentLevelAfter)/$($result.BiosGpuCurrentLevelAfter)"

$contractPass = $result.Success -and ([int]$result.SessionId -eq 0) -and ($result.UserSid -ceq 'S-1-5-18') -and ($result.TargetProfileId -ceq 'HP-8C40-9D0R1LA-F18') -and $result.TargetMatched -and $result.HandoffClaimed -and $result.HandoffValidated -and $result.ArmerIdentityValidated -and ([int]$result.BeforeCpuSetpoint -eq 30) -and ([int]$result.BeforeGpuSetpoint -eq 30) -and ([int]$result.BeforeMaxFan -eq 0) -and ([int]$result.BeforeFanSwitch -eq 0) -and $result.RestoreCallSucceeded -and $result.VerifiedFfFf -and ([int]$result.AfterCpuSetpoint -eq 255) -and ([int]$result.AfterGpuSetpoint -eq 255)
if (-not $contractPass) { Show-Diagnostics; throw "M3 result did not satisfy the restore-only qualification contract. Failure=$($result.Failure)" }

if (Test-Path $journalPath) { Show-Diagnostics; throw 'M3 unexpectedly created a watchdog lease journal.' }

Write-Host ''
Write-Host 'Step 6: independent final ownership verification...' -ForegroundColor Cyan
$final = Read-8C40Setpoint
if ($final.Cpu -ne 255 -or $final.Gpu -ne 255) { Show-Diagnostics; throw "M3 final independent probe did not read FF/FF: $($final.Raw)" }

Write-Host ''
if (Test-Path $logPath) { Get-Content $logPath | Select-Object -Last 80 }
Write-Host ''
& sc.exe qc $serviceName | Out-Host

Write-Host ''
Write-Host 'PASS: HP 8C40 M3 restore-only qualification completed.' -ForegroundColor Green
Write-Host 'One known VFC-owned 30/30 state was restored by LocalSystem to verified FF/FF.' -ForegroundColor Green
Write-Host 'No watchdog lease was created and no ordinary service-side fan-level command exists.' -ForegroundColor Green
Write-Host ''
Write-Host 'This PASS does NOT set WatchdogRecoveryValidated=true and does not authorize M4 lease ownership.'
