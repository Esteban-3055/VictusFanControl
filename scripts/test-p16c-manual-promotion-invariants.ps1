$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'p16c-promotion-boundary.ps1')
if(-not (Test-P16CManualPromotion)){throw 'P16C Manual promotion checkpoint missing.'}
$root=Split-Path -Parent $PSScriptRoot
$gate=Get-Content -LiteralPath (Join-Path $root 'src/VictusFanControl/Control/Adaptive/Hp8C40PostM9UserControlGate.cs') -Raw
Assert-CurrentManualAuthorizationBoundary $gate
$main=Get-Content -LiteralPath (Join-Path $root 'src/VictusFanControl.App/MainForm.cs') -Raw
foreach($needle in @('IsManualAuthorizedForTarget(_targetProfile?.Id)','isolatedManualQualification','Manual WMI COMMAND PROOF:')){if($main.IndexOf($needle,[StringComparison]::Ordinal)-lt 0){throw "P16C integration missing: $needle"}}
$qualification=Get-Content -LiteralPath (Join-Path $root 'src/VictusFanControl.App/P16NormalManualQualification.cs') -Raw
if($qualification.IndexOf('PhysicalExecutionAuthorized = false',[StringComparison]::Ordinal)-lt 0){throw 'P16 qualification gate must stay closed.'}
$settings=Get-Content -LiteralPath (Join-Path $root 'src/VictusFanControl.App/P13UiSettingsStore.cs') -Raw
foreach($forbidden in @('AdaptiveFanProductionMode','ManualExecutionAuthorized','AutomaticExecutionAuthorized','FanAuthority')){if($settings.IndexOf($forbidden,[StringComparison]::Ordinal)-ge 0){throw "UI preference cannot authorize control: $forbidden"}}
Write-Host 'PASS: P16C exact-target Manual promotion after audited physical closure; startup Firmware, Automatic and qualification closed.'
