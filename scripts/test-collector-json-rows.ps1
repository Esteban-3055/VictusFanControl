$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'CollectorJsonRows.ps1')

function Expect-Rejected([scriptblock]$Action) {
    $rejected = $false
    try { & $Action | Out-Null } catch { $rejected = $true }
    if (-not $rejected) { throw 'Invalid collector evidence was accepted.' }
}

$json = '[{"Destination":"other.txt","Status":"timeout"},{"Destination":"acpi/capture.txt","Status":"ok"}]'
$rows = @(Convert-CollectorJsonRows $json)
if ($rows.Count -ne 2 -or $rows[0].Status -ne 'timeout' -or $rows[1].Status -ne 'ok') { throw 'Record enumeration changed.' }
$firmware = Get-CollectorFirmwareCommand $rows
if ($firmware.Destination -ne 'acpi/capture.txt' -or $firmware.Status -ne 'ok') { throw 'Unrelated timeout poisoned firmware admission.' }

# Reproduce the old false failure specifically on the Windows PowerShell shell
# used by the smoke test. This confirms the fix addresses that representation.
$legacy = @($json | ConvertFrom-Json)
$legacyFirmware = @($legacy | Where-Object { $_.Destination -match 'acpi[\\/]capture.txt$' })
$legacyRejected = if ($legacyFirmware.Count -ne 1 -or $legacyFirmware[0].Status -ne 'ok') { $true } else { $false }
if ($PSVersionTable.PSVersion.Major -eq 5 -and -not $legacyRejected) { throw 'Windows PowerShell 5.1 false failure was not reproduced.' }
if ($PSVersionTable.PSVersion.Major -ge 7 -and $legacyRejected) { throw 'Unexpected PowerShell 7 legacy array shape.' }

$empty = @(Convert-CollectorJsonRows '[]')
if ($empty.Count -ne 0) { throw 'Empty ETW history became a phantom session.' }
$single = @(Convert-CollectorJsonRows '[{"Destination":"acpi\\capture.txt","Status":"ok"}]')
if ($single.Count -ne 1) { throw 'Single-record array changed.' }
Get-CollectorFirmwareCommand $single | Out-Null
Expect-Rejected { Get-CollectorFirmwareCommand @(Convert-CollectorJsonRows '[{"Destination":"acpi/capture.txt","Status":"timeout"}]') }
Expect-Rejected { Get-CollectorFirmwareCommand $empty }
Expect-Rejected { Get-CollectorFirmwareCommand ($single + $single) }
Expect-Rejected { Get-CollectorFirmwareCommand @(Convert-CollectorJsonRows '[{"Destination":"acpi/capture.txt","Status":["ok"]}]') }
Expect-Rejected { Convert-CollectorJsonRows '{}' }
Expect-Rejected { Convert-CollectorJsonRows '[null]' }
Expect-Rejected { Convert-CollectorJsonRows '[[{}]]' }
Expect-Rejected { Convert-CollectorJsonRows '[broken' }
Write-Host ('Collector JSON admission: PASS (unrelated timeout, real failure, duplicate/missing identity, empty/single arrays and malformed records; PowerShell ' + $PSVersionTable.PSVersion + '; no hardware IO).')
