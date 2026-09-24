param(
    [switch]$SkipEcSnapshots
)

$ErrorActionPreference = 'Stop'

Write-Host 'VictusFanControl - HP firmware restore test' -ForegroundColor Cyan
Write-Host 'This performs the HP/Omen-compatible release sequence: FanLevel=FF,FF then FanMode=LegacyDefault.'
Write-Host 'It releases any fixed fan-level override. It does NOT modify undervolt settings or the EC manual/countdown flag.'
Write-Host ''

$confirm = Read-Host 'Type RESTORE to continue'
if ($confirm -cne 'RESTORE') {
    Write-Host 'Cancelled.'
    exit 1
}

$args = @(
    'run',
    '--project', '.\src\VictusFanControl',
    '-c', 'Release',
    '--',
    '--restore-hp-auto'
)

if ($SkipEcSnapshots) {
    $args += '--skip-ec-snapshots'
}

dotnet @args
exit $LASTEXITCODE
