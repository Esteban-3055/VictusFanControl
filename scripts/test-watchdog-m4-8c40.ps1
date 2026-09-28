param(
    [Parameter(Mandatory = $true)]
    [ValidateSet(10, 30, 50)]
    [int]$Level
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
Set-Location $repoRoot

$serviceName = 'VictusFanControlWatchdogM4'
$serviceRoot = Join-Path $env:ProgramData 'VictusFanControl\WatchdogM4'
$stateDir = Join-Path $serviceRoot 'state'
$statusPath = Join-Path $stateDir 'm4-8c40.status.json'
$journalPath = Join-Path $stateDir 'lease.json'
$logPath = Join-Path $serviceRoot ("logs\watchdog-m4-8c40-{0}.log" -f (Get-Date -Format 'yyyy-MM-dd'))

$cli = Join-Path $repoRoot 'src\VictusFanControl\bin\Release\net8.0-windows\VictusFanControl.dll'
$modulesDir = Join-Path $repoRoot 'modules'
$gate = switch ($Level) {
    10 { 'M4B' }
    30 { 'M4A' }
    50 { 'M4C' }
}
$requiredToken = "8C40-M4-LEASE$Level"
$cliSwitch = "--8c40-m4-lease$Level"

function Assert-Administrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw 'This M4 endpoint hardware gate must be run from an elevated PowerShell.'
    }
}

function Read-8C40Setpoint {
    $output = (& dotnet $cli --probe-8c40-setpoint --modules-dir $modulesDir 2>&1 | Out-String)
    Write-Host $output.TrimEnd()
    $line = ($output -split "[\r\n]+" | Where-Object { $_ -match '^setpoint CPU=' } | Select-Object -Last 1)
    if (-not $line) { throw "Could not parse HP 8C40 setpoint probe. Raw output: $output" }
    $match = [regex]::Match($line, '^setpoint CPU=(\d+) GPU=(\d+)$')
    if (-not $match.Success) { throw "Could not parse setpoints from: $line" }

    [pscustomobject]@{
        Cpu = [int]$match.Groups[1].Value
        Gpu = [int]$match.Groups[2].Value
        Raw = $line
    }
}

function Wait-M4Ready {
    $deadline = (Get-Date).AddSeconds(20)

    while ((Get-Date) -lt $deadline) {
        if (Test-Path $statusPath) {
            try {
                $status = Get-Content $statusPath -Raw | ConvertFrom-Json
                if ($status.Ready -and
                    -not $status.Blocked -and
                    $status.SessionId -eq 0 -and
                    $status.AccountName -match 'SYSTEM$' -and
                    $status.TargetProfileId -ceq 'HP-8C40-9D0R1LA-F18' -and
                    $status.PipeName -ceq 'VictusFanControl.Watchdog.M4.8C40.v2') {
                    return $status
                }
            } catch {
            }
        }

        Start-Sleep -Milliseconds 250
    }

    throw 'Timed out waiting for M4 service Ready state.'
}

function Show-Diagnostics {
    Write-Host ''
    if (Test-Path $statusPath) {
        Write-Host 'M4 status:' -ForegroundColor Cyan
        Get-Content $statusPath
    }
    if (Test-Path $journalPath) {
        Write-Host ''
        Write-Host 'M4 durable journal:' -ForegroundColor Yellow
        Get-Content $journalPath
    }
    if (Test-Path $logPath) {
        Write-Host ''
        Write-Host 'Recent M4 log:' -ForegroundColor Cyan
        Get-Content $logPath | Select-Object -Last 100
    }
    Write-Host ''
    & sc.exe queryex $serviceName | Out-Host
}

Assert-Administrator

Write-Host "VictusFanControl - HP 8C40 WATCHDOG $gate (REAL TARGET-BOUND LEASE $Level/$Level)" -ForegroundColor Cyan
Write-Host ''
Write-Host "Sequence: PREPARE -> WRITE_INTENT -> $Level/$Level -> COMMIT -> Probe/Heartbeat -> RESTORE_BEGIN -> FF/FF -> RELEASE"
Write-Host 'The M4 service itself has no ordinary fan-level write method.' -ForegroundColor Yellow
Write-Host ''

foreach ($name in @('OmenMon','OmenMon-Reborn','VictusFanControl.App')) {
    if (Get-Process -Name $name -ErrorAction SilentlyContinue) {
        throw "Refusing M4 endpoint qualification while process '$name' is running. Close it and retry."
    }
}

Write-Host 'Step 1: build + M4/Gate-C/backend regressions...' -ForegroundColor Cyan
dotnet build .\VictusFanControl.sln -c Release -warnaserror
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

dotnet run --project .\src\VictusFanControl.Watchdog -c Release --no-build -- --m4-8c40-self-test
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

dotnet run --project .\src\VictusFanControl.Watchdog -c Release --no-build -- --gate-c-self-test
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

dotnet run --project .\src\VictusFanControl -c Release --no-build -- --hp-backend-self-test
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Write-Host ''
Write-Host 'Step 2: clean firmware-owned baseline...' -ForegroundColor Cyan
$baseline = Read-8C40Setpoint
if ($baseline.Cpu -ne 255 -or $baseline.Gpu -ne 255) {
    throw "M4 endpoint qualification requires FF/FF baseline; observed $($baseline.Cpu)/$($baseline.Gpu)."
}

Write-Host ''
Write-Host 'Step 3: install/start isolated M4 service...' -ForegroundColor Cyan
if (Test-Path $journalPath) {
    Write-Host 'M4 durable journal is present. Refusing to delete or overwrite ownership evidence.' -ForegroundColor Red
    Get-Content $journalPath
    throw "M4 endpoint qualification requires an absent durable journal before service installation: $journalPath"
}

& (Join-Path $PSScriptRoot 'install-watchdog-m4-8c40.ps1')

Remove-Item $statusPath -Force -ErrorAction SilentlyContinue

Start-Service -Name $serviceName
$status = Wait-M4Ready

Write-Host "M4 Ready          : $($status.Ready)"
Write-Host "Session           : $($status.SessionId)"
Write-Host "Account           : $($status.AccountName)"
Write-Host "Target            : $($status.TargetProfileId)"
Write-Host "Pipe              : $($status.PipeName)"
Write-Host "Startup recovery  : $($status.RecoveryDisposition)"

Write-Host ''
Write-Host 'ACTIVE LEASE/WRITE BOUNDARY' -ForegroundColor Yellow
Write-Host "The controller will issue one real $Level/$Level command protected by the M4 durable lease." -ForegroundColor Yellow
Write-Host 'Normal completion restores FF/FF locally and asks the service to normalize/release the lease.' -ForegroundColor Yellow
Write-Host ''
$token = Read-Host "Type exactly $requiredToken to continue"
if ($token -cne $requiredToken) {
    Stop-Service -Name $serviceName -Force -ErrorAction SilentlyContinue
    throw "$gate cancelled: acknowledgement token did not match."
}

try {
    Write-Host ''
    Write-Host "Step 4: execute real coordinator/backend + $gate lease at $Level/$Level..." -ForegroundColor Cyan
    & dotnet $cli $cliSwitch --8c40-m4-lease-token $requiredToken --modules-dir $modulesDir
    $qualificationExit = $LASTEXITCODE

    if ($qualificationExit -ne 0) {
        Start-Sleep -Seconds 1
        Show-Diagnostics

        Write-Host ''
        Write-Host 'Failure-path independent EC setpoint probe (READ-ONLY):' -ForegroundColor Cyan
        try {
            $failureFinal = Read-8C40Setpoint
            Write-Host "Failure-path EC setpoint: $($failureFinal.Cpu)/$($failureFinal.Gpu)"
        } catch {
            Write-Warning "Failure-path EC probe could not be completed: $($_.Exception.Message)"
        }

        throw "M4 endpoint qualification process failed with exit code $qualificationExit."
    }

    Start-Sleep -Milliseconds 500

    if (Test-Path $journalPath) {
        Show-Diagnostics
        throw 'M4 endpoint normal completion left a durable lease journal behind.'
    }

    Write-Host ''
    Write-Host 'Step 5: independent final FF/FF verification...' -ForegroundColor Cyan
    $final = Read-8C40Setpoint

    if ($final.Cpu -ne 255 -or $final.Gpu -ne 255) {
        Show-Diagnostics
        throw "M4 endpoint final EC probe did not read FF/FF: $($final.Raw)"
    }

    Write-Host ''
    if (Test-Path $logPath) {
        Get-Content $logPath | Select-Object -Last 100
    }

    Write-Host ''
    Write-Host "PASS: HP 8C40 $gate real target-bound lease at $Level/$Level completed." -ForegroundColor Green
    Write-Host 'Verified normal lease lifecycle and final firmware ownership with no retained journal.' -ForegroundColor Green
    Write-Host 'This gate qualifies only this bounded awake endpoint lease; process-death recovery and Modern Standby lifecycle remain separate.' -ForegroundColor Yellow
}
finally {
    $service = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
    if ($service -and $service.Status -ne 'Stopped') {
        Stop-Service -Name $serviceName -Force -ErrorAction SilentlyContinue
    }
}
