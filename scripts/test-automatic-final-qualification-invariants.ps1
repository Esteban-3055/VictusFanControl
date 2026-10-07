$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
Set-Location $repoRoot

function Require([bool]$Condition, [string]$Message) {
    if (-not $Condition) {
        throw "Automatic final qualification invariant failed: $Message"
    }
}

$gatePath = Join-Path $repoRoot 'src\VictusFanControl\Control\Adaptive\Hp8C40AutomaticFinalQualificationGate.cs'
$userGatePath = Join-Path $repoRoot 'src\VictusFanControl\Control\Adaptive\Hp8C40PostM9UserControlGate.cs'
$programPath = Join-Path $repoRoot 'src\VictusFanControl.App\Program.cs'
$mainPath = Join-Path $repoRoot 'src\VictusFanControl.App\MainForm.cs'
$qualificationPath = Join-Path $repoRoot 'src\VictusFanControl.App\MainForm.AutomaticFinalQualification.cs'
$surfacePath = Join-Path $repoRoot 'src\VictusFanControl.App\P13FanControlSurface.cs'

foreach ($path in @($gatePath, $userGatePath, $programPath, $mainPath, $qualificationPath, $surfacePath)) {
    Require (Test-Path -LiteralPath $path) "missing required file $path"
}

$gate = Get-Content -LiteralPath $gatePath -Raw
$userGate = Get-Content -LiteralPath $userGatePath -Raw
$program = Get-Content -LiteralPath $programPath -Raw
$main = Get-Content -LiteralPath $mainPath -Raw
$qualification = Get-Content -LiteralPath $qualificationPath -Raw
$surface = Get-Content -LiteralPath $surfacePath -Raw

Require ($gate -match 'PhysicalExecutionAuthorized\s*=\s*true') 'dedicated physical qualification gate must be explicit'
Require ($gate -match 'RequiredToken\s*=\s*"8C40-AUTOMATIC-FINAL"') 'dedicated token changed unexpectedly'
Require ($gate -match 'NormalUserAutomaticRemainsClosed') 'dedicated gate must assert normal user Automatic remains closed'
Require ($userGate -match 'AutomaticExecutionAuthorized\s*=\s*false') 'normal user Automatic gate must remain CLOSED'

Require ($program.Contains('--8c40-automatic-final-qualification')) 'Program missing dedicated mode switch'
Require ($program.Contains('--8c40-automatic-test-token')) 'Program missing explicit token'
Require ($program.Contains('--8c40-automatic-marker-root')) 'Program missing isolated evidence root'
Require ($program.Contains('Hp8C40AutomaticFinalQualificationGate.RequiredToken')) 'Program must compare against the gate token'
Require ($program.Contains('automaticFinalQualificationHardwareTest')) 'Program must carry dedicated qualification state'

Require ($main.Contains('_automaticFinalQualificationHardwareTest')) 'MainForm missing dedicated qualification flag'
Require ($main.Contains('Hp8C40AutomaticFinalQualificationGate.IsAuthorizedForTarget')) 'MainForm must derive Automatic authority only from exact-target qualification gate'
Require ($main.Contains('RecordAutomaticFinalAuthorityChange(e)')) 'authority loss between decisions must be observed synchronously'
Require ($qualification.Contains('fan-authority-transition')) 'original coordinator reason must be retained'
Require ($qualification.Contains('_automaticFinalFirmwareReleaseRequested')) 'real Firmware escape must not be reported as unsolicited loss'
Require ($qualification.Contains('AdaptiveFanProductionActionKind.HoldFirmware))')) 'post-active HoldFirmware must fail qualification'

Require ($main.Contains('RecordAutomaticFinalDecision')) 'Automatic decisions must be recorded in qualification mode'
Require ($main.Contains('TryPublishAutomaticFinalReadyAsync')) 'qualification READY must depend on live runtime preflight'
Require ($main.Contains('IsAutomaticFinalControlInteractionAuthorized')) 'real P13 interactions must be fenced'
Require ($main.Contains('OnAutomaticFinalControlInteraction')) 'real P13 interactions must be observed'
Require ($main.Contains('!_automaticFinalQualificationHardwareTest')) 'dedicated Automatic gate must not inherit a P16 qualification session'

Require ($qualification.Contains('CpuDemandTemperatureSource.HottestPerformanceCoresAverage')) 'qualification must pin hottest-P-core demand source'
Require ($qualification.Contains('tuning.HottestPerformanceCoreCount != 3')) 'qualification must pin N=3 P-Cores'
Require ($qualification.Contains('tuning.MinimumLevel != 30')) 'qualification must pin floor 30'
Require ($qualification.Contains('tuning.MaximumLevel != 50')) 'qualification must pin ceiling 50'
Require ($qualification.Contains('tuning.NormalMaximumUpStepLevels != 1')) 'qualification must pin normal rise step to 1'
Require ($qualification.Contains('tuning.MaximumDownStepLevels != 1')) 'qualification must pin normal descent step to 1'
Require ($qualification.Contains('tuning.NormalPollingDelayMilliseconds != 1000')) 'qualification must pin normal polling to 1000 ms'
Require ($qualification.Contains('tuning.RememberThermalDemand')) 'qualification must explicitly reject thermal-demand memory'
Require ($qualification.Contains('tuning.SustainedLoadSeconds, 1200')) 'qualification must pin 20-minute load history threshold'
Require ($qualification.Contains('fail-cleanup')) 'qualification failure must schedule a production-controller Firmware cleanup'
Require ($qualification.Contains('_automaticFinalReadyConfigurationJson')) 'READY must bind the exact saved configuration used by the run'
Require ($qualification.Contains('Fan configuration changed after READY')) 'post-READY configuration mutation must fail closed'
Require ($qualification.Contains('EnsureAutomaticFinalReadyEnvelope')) 'READY must enforce a bounded thermal/power envelope'
Require ($qualification.Contains('CpuDemandTemperature.Select')) 'READY must prove the hottest-3-P-Core source is actually available'
Require ($qualification.Contains('AdaptiveFanProductionActionKind.RestoreFirmware')) 'real Firmware transition must be verified'
Require ($qualification.Contains('ReleaseRequestAccepted: true')) 'WMI release acceptance required'
Require ($qualification.Contains('GuardianLeaseRetired: true')) 'WMI guardian retirement required'
Require (-not $qualification.Contains('ReadStableM6FirmwareAutoProof')) 'WMI qualification must not access direct EC'
Require ($qualification.Contains('IndependentFirmwareOwnershipVerified: false')) 'WMI acceptance must not claim independent ownership'
Require ($qualification.Contains('File.Exists(P15CJournalPath)')) 'durable journal absence must be checked'
Require ($qualification.Contains('_automaticFinalDecisionCount < 30')) 'minimum real Automatic decision evidence required'
Require ($qualification.Contains('_automaticFinalHardwareCommandDecisions < 2')) 'minimum changed-level hardware evidence required'

Require ($surface.Contains('stored profiles never grant fan authority')) 'GUI must state that settings do not grant authority'

Write-Host 'PASS: final Automatic qualification remains isolated from normal product authority.'
Write-Host 'PASS: exact target + explicit token + evidence root are required.'
Write-Host 'PASS: qualification profile is pinned to hottest 3 P-Cores, 30..50 and adaptive timings.'
Write-Host 'PASS: WMI-only Automatic -> Firmware requires accepted release/default and retired guardian; no EC proof.'

$review = Get-Content (Join-Path $repoRoot 'src/VictusFanControl.App/Product/ProductAutomaticReview.cs') -Raw
$launcher = Get-Content (Join-Path $repoRoot 'release/Start-ProductGui.ps1') -Raw
$package = Get-Content (Join-Path $repoRoot 'scripts/package-product-gui-review.ps1') -Raw
Require ($review -match 'MaximumSeconds\s*=\s*300') 'short product review must stay bounded to five minutes'
Require ($review -match 'ExtendedMaximumSeconds\s*=\s*2700') 'extended review must stay bounded to 45 minutes'
Require ($review -match 'MaximumCpuSpikeMilliseconds\s*=\s*2000') 'long review must not relax CPU confirmation'
Require ($review.Contains('CpuSpikeThresholdC = SafetyGate.CpuEmergencyC')) 'product active confirmation must share the core 95 C threshold'
Require ($program.Contains('ProductAutomaticReview.ResolveEntry')) 'review entry combinations must be rejected'
Require ($launcher.Contains('AutomaticExtendedReview') -and $launcher.Contains('--product-automatic-extended-review')) 'extended launcher entry missing'
Require ($launcher.Contains('explicit-only-2700s-10-to-50-16MiB-diagnostics') -and $package.Contains('explicit-only-2700s-10-to-50-16MiB-diagnostics')) 'extended package contract mismatch'
Require ($launcher.Contains("'FinalCheck'") -and $launcher.Contains("@('SelfTest','Soak','RecoverySelfTest')")) 'final candidate must check packaged software with zero-hardware fixture entries'
Require ($launcher.Contains('$start.WorkingDirectory = $fixtureOutput') -and $launcher.Contains('No hardware activation or physical qualification performed.')) 'packaged fixture outputs must remain outside the verified payload'
$candidate = Get-Content (Join-Path $repoRoot 'release/product-final-candidate.json') -Raw | ConvertFrom-Json
Require ($candidate.stableReleaseAuthorized -eq $false -and $candidate.normalAutomatic -eq 'closed') 'candidate must not claim a stable release or normal Automatic authority'
Require ($candidate.remainingPhysicalChecks.Count -eq 3) 'candidate must retain its thermal/lifecycle/exit physical regressions'
Require ($candidate.automaticThermalContract -eq 'cpu-start90-active95-confirm2000ms-cpu99-immediate-raw-response') 'candidate thermal contract drifted'
Require ($launcher.Contains($candidate.automaticThermalContract) -and $package.Contains($candidate.automaticThermalContract)) 'launcher and package must bind the current thermal contract'
foreach ($evidence in $candidate.physicalEvidence) {
    Require ([IO.Path]::GetFileName($evidence) -eq $evidence -and (Test-Path -LiteralPath (Join-Path $repoRoot ('release/' + $evidence)))) 'candidate physical evidence reference missing or invalid'
    $record = Get-Content -LiteralPath (Join-Path $repoRoot ('release/' + $evidence)) -Raw | ConvertFrom-Json
    Require ($record.target -eq 'HP-8C40-9D0R1LA-F18' -and $record.sourceHead -match '^[0-9a-f]{40}$') 'candidate evidence must bind the exact target and historical code'
}
Write-Host 'PASS: final candidate retains the closed normal gate, explicit physical pending checks and shared 95 C / 2000-ms confirmation.'

foreach ($evidence in $candidate.offlinePolicyEvidence) {
    Require ([IO.Path]::GetFileName($evidence) -eq $evidence) 'offline policy evidence path invalid'
    $offline = Get-Content (Join-Path $repoRoot ('release/' + $evidence)) -Raw | ConvertFrom-Json
    Require ($offline.target -eq 'HP-8C40-9D0R1LA-F18' -and $offline.physicalPassClaimed -eq $false -and $offline.normalAutomaticAuthorized -eq $false) 'replay must not claim physical qualification or authority'
    Require ($offline.historicalReconstruction.expectedDecisions -eq $offline.historicalReconstruction.exactLevelMatches) 'historical replay must reproduce all recorded targets'
    foreach ($binding in $offline.sourceSha256.PSObject.Properties) {
        Require ((Get-FileHash -Algorithm SHA256 -LiteralPath (Join-Path $repoRoot $binding.Name)).Hash.ToLowerInvariant() -eq $binding.Value) 'offline comparison source hash drifted'
    }
}
Write-Host 'PASS: offline stability evidence binds source and retains physical pending status.'
