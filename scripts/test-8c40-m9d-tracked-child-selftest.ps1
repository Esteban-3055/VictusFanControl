$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'm9d-tracked-child.ps1')
$temp=Join-Path $env:TEMP ("vfc-m9d-child-{0}" -f ([guid]::NewGuid().ToString('N')))
New-Item -ItemType Directory -Force -Path $temp | Out-Null
try {
    $exe=(Get-Command powershell.exe -ErrorAction Stop).Source
    $child=Start-M9DTrackedChild -Executable $exe -WorkingDirectory $temp -Arguments @('-NoProfile','-Command','exit 7')
    $code=Wait-M9DTrackedChildExitCode -Process $child -Seconds 20
    if($code -ne 7){throw "Expected exit 7, observed $code"}
    $child.Dispose()
    Write-Host 'HP 8C40 M9D native tracked child self-test: PASS' -ForegroundColor Green
}
finally {if(Test-Path $temp){Remove-Item $temp -Recurse -Force -ErrorAction SilentlyContinue}}
