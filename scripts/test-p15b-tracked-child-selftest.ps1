# Host-only PowerShell 7 + Windows PowerShell 5.1 regression.
# No P15B controller, watchdog service, WMI/EC access or journal mutation.
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'p15b-tracked-child.ps1')
$exe=(Get-Command powershell.exe -CommandType Application -ErrorAction Stop).Source
$root=Split-Path -Parent $PSScriptRoot
foreach($expected in @(0,7)){
    $child=Start-P15BTrackedChild -Executable $exe -Arguments @('-NoProfile','-NonInteractive','-Command',("exit {0}" -f $expected)) -WorkingDirectory $root
    try{
        if($child.Id -le 0){throw 'P15B tracked synthetic child did not expose a PID.'}
        $observed=Wait-P15BTrackedChildExitCode -Process $child -Seconds 15
        if($observed -ne $expected){throw ("P15B tracked exit-code mismatch: expected {0}, observed {1}." -f $expected,$observed)}
        Write-Host ("P15B native tracked child exit {0}: PASS (PID {1})" -f $expected,$child.Id)
    }finally{$child.Dispose()}
}
Write-Host 'P15B process/ExitCode native helper self-test: PASS' -ForegroundColor Green
