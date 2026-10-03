$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
function Read-Source([string]$p){Get-Content -LiteralPath (Join-Path $root $p) -Raw}
$editor=Read-Source 'src/VictusFanControl.App/AdaptiveCurveEditorForm.cs'
$chart=Read-Source 'src/VictusFanControl.App/AdaptiveCurveChart.cs'
$profiles=Read-Source 'src/VictusFanControl/Control/Adaptive/AdaptiveCurveProfiles.cs'
foreach($source in @($editor,$chart,$profiles)){
 foreach($forbidden in @('FanControlCoordinator','AdaptiveFanProductionController','HpOmenBiosWmiClient','AcpiEcReader','SetFanLevel(','PawnIo','NamedPipeFanControlWatchdog')){
  if($source.IndexOf($forbidden,[StringComparison]::Ordinal)-ge 0){throw "Editor bypass dependency: $forbidden"}
 }
}
foreach($needle in @('AdaptiveFanPolicyEngine.Interpolate','UnmappedMemberHandling.Disallow','File.Move(temporary, path, overwrite: true)','"Silencio"','"Equilibrado"','"Performance"')){
 if($profiles.IndexOf($needle,[StringComparison]::Ordinal)-lt 0){throw "Profile contract missing: $needle"}
}
$gate=Read-Source 'src/VictusFanControl/Control/Adaptive/Hp8C40PostM9UserControlGate.cs'
if($gate.IndexOf('AutomaticExecutionAuthorized = false',[StringComparison]::Ordinal)-lt 0){throw 'P17 must keep Automatic closed.'}
. (Join-Path $PSScriptRoot 'p16c-promotion-boundary.ps1')
Assert-CurrentManualAuthorizationBoundary $gate
$cp=Read-Source 'release/p17-curve-editor-checkpoint.json'|ConvertFrom-Json
if($cp.hardwareExecution -or $cp.automaticExecutionAuthorized -or $cp.candidatePhysicallyValidated -or $cp.presetsPhysicallyValidated -or $cp.safetyThresholdsChanged){throw 'P17 scope exceeded.'}
Write-Host 'PASS: P17 editor/profile isolation; shared interpolation; Manual preserved; Automatic closed.'
