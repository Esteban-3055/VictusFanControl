param(
    [switch]$SkipEcSnapshots
)

$ErrorActionPreference = 'Stop'

Write-Host 'VictusFanControl - HP firmware restore test' -ForegroundColor Cyan
Write-Host 'This performs one HP BIOS/WMI write: FanMode = LegacyDefault.'
Write-Host 'It does NOT set fan RPM and does NOT modify undervolt settings.'
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
