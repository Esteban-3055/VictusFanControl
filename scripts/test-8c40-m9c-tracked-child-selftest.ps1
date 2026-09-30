# Host-only PowerShell 7 + Windows PowerShell 5.1 regression.
# No M9C controller, watchdog service, WMI/EC access or journal mutation.
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'm9c-tracked-child.ps1')

$exe=(Get-Command powershell.exe -CommandType Application -ErrorAction Stop).Source
$root=Split-Path -Parent $PSScriptRoot

foreach($expected in @(0,7)){
    $child=Start-M9CTrackedChild -Executable $exe -Arguments @(
        '-NoProfile',
        '-NonInteractive',
        '-Command',
        ("exit {0}" -f $expected)
    ) -WorkingDirectory $root

    try {
        if($child.Id -le 0){throw 'M9C tracked synthetic child did not expose a PID.'}

        $observed=Wait-M9CTrackedChildExitCode -Process $child -Seconds 15
        if($observed -ne $expected){
            throw ("M9C tracked exit-code mismatch: expected {0}, observed {1}." -f
                $expected,$observed)
        }

        Write-Host ("M9C native tracked child exit {0}: PASS (PID {1})" -f
            $expected,$child.Id)
    }
    finally {
        $child.Dispose()
    }
}

Write-Host 'M9C process/ExitCode native helper self-test: PASS' -ForegroundColor Green
