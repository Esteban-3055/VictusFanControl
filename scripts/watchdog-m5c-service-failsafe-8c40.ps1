param(
    [ValidateRange(15, 300)]
    [int]$DelaySeconds = 45,

    [Parameter(Mandatory = $true)]
    [string]$LogPath
)

$ErrorActionPreference = 'Stop'

$serviceName = 'VictusFanControlWatchdogM4'
$journalPath = Join-Path $env:ProgramData 'VictusFanControl\WatchdogM4\state\lease.json'

function Log-Line {
    param([string]$Text)

    $directory = Split-Path -Parent $LogPath
    if ($directory) {
        New-Item -ItemType Directory -Force -Path $directory | Out-Null
    }

    Add-Content -Path $LogPath -Value ("{0:O}  {1}" -f (Get-Date), $Text)
}

function Test-OwnedPhase {
    param($Phase)

    if ($null -eq $Phase) {
        return $false
    }

    if ($Phase -is [string]) {
        return ($Phase -ceq 'Owned' -or $Phase -ceq '2')
    }

    try {
        return ([int]$Phase -eq 2)
    }
    catch {
        return $false
    }
}

try {
    Start-Sleep -Seconds $DelaySeconds

    if (-not (Test-Path $journalPath)) {
        Log-Line 'M5C FAILSAFE NO-OP: durable journal is absent.'
        exit 0
    }

    $journal = Get-Content $journalPath -Raw | ConvertFrom-Json

    if ([int]$journal.SchemaVersion -ne 2 -or
        $journal.TargetProfileId -cne 'HP-8C40-9D0R1LA-F18' -or
        -not (Test-OwnedPhase -Phase $journal.Phase) -or
        [int]$journal.Owned.Cpu -ne 30 -or
        [int]$journal.Owned.Gpu -ne 30) {
        Log-Line 'M5C FAILSAFE REFUSED: retained journal is not exact-target durable OWNED 30/30.'
        exit 2
    }

    $svc = Get-CimInstance Win32_Service -Filter "Name='$serviceName'" -ErrorAction SilentlyContinue

    if (-not $svc) {
        Log-Line 'M5C FAILSAFE REFUSED: recovery service is not installed.'
        exit 3
    }

    if ($svc.State -eq 'Running' -and [int]$svc.ProcessId -gt 0) {
        Log-Line ("M5C FAILSAFE NO-OP: recovery service already running PID={0}; journal remains for that service to process." -f [int]$svc.ProcessId)
        exit 0
    }

    Log-Line 'M5C FAILSAFE TAKEOVER: exact-target OWNED 30/30 journal remains and service is absent; starting the already-qualified LocalSystem recovery service.'
    Start-Service -Name $serviceName

    $deadline = (Get-Date).AddSeconds(20)
    while ((Get-Date) -lt $deadline) {
        $current = Get-CimInstance Win32_Service -Filter "Name='$serviceName'" -ErrorAction SilentlyContinue
        if ($current -and $current.State -eq 'Running' -and [int]$current.ProcessId -gt 0) {
            Log-Line ("M5C FAILSAFE STARTED: recovery service PID={0}. Journal ownership remains service-side; failsafe will not delete it." -f [int]$current.ProcessId)
            exit 0
        }

        Start-Sleep -Milliseconds 200
    }

    Log-Line 'M5C FAILSAFE ERROR: service start was requested but no running recovery process appeared within 20 s.'
    exit 4
}
catch {
    Log-Line ("M5C FAILSAFE ERROR: " + $_.Exception.ToString())
    exit 5
}
