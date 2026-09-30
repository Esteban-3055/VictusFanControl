# Windows PowerShell 5.1 and PowerShell 7 host-only regression.
# Launches powershell.exe with controlled exit status: no M8B controller,
# watchdog service, WMI/EC writes, fan access, or durable journal mutation.
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'm8b-tracked-child.ps1')

$exe=(Get-Command powershell.exe -CommandType Application -ErrorAction Stop).Source
$root=Split-Path -Parent $PSScriptRoot

foreach($expected in @(0,7)){
    $child=Start-M8BTrackedChild -Executable $exe -Arguments @(
        '-NoProfile',
        '-NonInteractive',
        '-Command',
        ("exit {0}" -f $expected)
    ) -WorkingDirectory $root

    try {
        if($child.Id -le 0){
            throw 'Tracked synthetic child did not provide a real process ID.'
        }

        $observed=Wait-M8BTrackedChildExitCode -Process $child -Seconds 15

        if($observed -ne $expected){
            throw ("Native tracked child exit-code mismatch: expected {0}, observed {1}." -f
                $expected,$observed)
        }

        Write-Host ("M8B native tracked child exit {0}: PASS (PID {1})" -f
            $expected,$child.Id)
    }
    finally {
        $child.Dispose()
    }
}

Write-Host 'M8B process/ExitCode native helper self-test: PASS' -ForegroundColor Green
