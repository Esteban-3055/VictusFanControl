# Host-only PowerShell 7 + Windows PowerShell 5.1 regression.
# No M9D App, watchdog service, WMI/EC access, journal or power transition.
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'm9d-tracked-child.ps1')

$exe=(Get-Command powershell.exe -CommandType Application -ErrorAction Stop).Source
$root=Split-Path -Parent $PSScriptRoot

foreach($expected in @(0,7)){
    $child=Start-M9DTrackedChild -Executable $exe -Arguments @(
        '-NoProfile','-NonInteractive','-Command',("exit {0}" -f $expected)
    ) -WorkingDirectory $root

    try {
        if($child.Id -le 0){throw 'M9D tracked synthetic child did not expose a PID.'}
        $observed=Wait-M9DTrackedChildExitCode -Process $child -Seconds 15
        if($observed -ne $expected){
            throw ("M9D tracked exit-code mismatch: expected {0}, observed {1}." -f $expected,$observed)
        }
        Write-Host ("M9D native tracked child exit {0}: PASS (PID {1})" -f $expected,$child.Id)
    }
    finally {$child.Dispose()}
}

Write-Host 'M9D process/ExitCode native helper self-test: PASS' -ForegroundColor Green
