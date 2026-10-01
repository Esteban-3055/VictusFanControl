$ErrorActionPreference='Stop'
$repoRoot=Split-Path -Parent $PSScriptRoot
$app=Get-Content (Join-Path $repoRoot 'src\VictusFanControl.App\MainForm.cs') -Raw
$program=Get-Content (Join-Path $repoRoot 'src\VictusFanControl.App\Program.cs') -Raw
$factory=Get-Content (Join-Path $repoRoot 'src\VictusFanControl\Hardware\Hp\HpFanControlBackendFactory.cs') -Raw

function Assert-Contains([string]$Text,[string]$Needle,[string]$Message){
    if($Text.IndexOf($Needle,[StringComparison]::Ordinal) -lt 0){throw $Message}
}
function Assert-NotContains([string]$Text,[string]$Needle,[string]$Message){
    if($Text.IndexOf($Needle,[StringComparison]::Ordinal) -ge 0){throw $Message}
}

Assert-Contains $program '--8c40-m7-hibernation-test' 'M7 app mode is missing.'
Assert-Contains $program '--8c40-m7-test-token' 'M7 explicit token option is missing.'
Assert-Contains $program '8C40-M7-HIBERNATION30' 'M7 explicit hibernation token is missing.'
Assert-Contains $program 'Hp8C40TargetProfile.Matches' 'M7 must exact-match HP 8C40 before MainForm/backend construction.'
Assert-Contains $app '_m7HibernationHardwareTest' 'M7 mode flag is missing from MainForm.'
Assert-Contains $app 'DisplayAware8C40LifecycleHardwareTest' 'M7 must reuse the hardened display-aware lifecycle engine.'
Assert-Contains $app 'Hp8C40FanControlBackend.CreateLifecycleQualificationBackend' 'M7 must remain behind the qualification-only HP 8C40 backend.'
Assert-Contains $app 'RegisterPowerSettingNotification' 'M7 must receive SESSION_DISPLAY_STATUS.'
Assert-Contains $app 'RegisterSuspendResumeNotification' 'M7 must receive registered suspend/resume notifications.'
Assert-Contains $app 'GUID_SESSION_DISPLAY_STATUS/Off' 'M7 must use display Off as the proactive release boundary.'
Assert-Contains $app 'GUID_SESSION_DISPLAY_STATUS/On' 'M7 must use display On as the telemetry resume boundary.'
Assert-Contains $app 'registered-WM_POWERBROADCAST/PBT_APMSUSPEND' 'M7 must complete pre-transition restore in registered PBT_APMSUSPEND.'
Assert-Contains $app 'WaitForHardwareReadQuiescenceAsync' 'M7 must quiesce telemetry hardware reads before restore IO.'
Assert-Contains $app 'transitionMode={DisplayAwareLifecycleTransitionMode}' 'M7 markers must identify the transition mode.'
Assert-Contains $app '(_m6ModernStandbyHardwareTest ||' 'Display-aware maintenance-resume proof must remain scoped away from M7.'
Assert-Contains $app '_m9dProductionLifecycleHardwareTest) &&' 'M9D Modern Standby may share the M6 maintenance-resume proof without imposing it on M7.'
Assert-Contains $app 'WatchdogReleaseVerified: true' 'M7 must require watchdog Release acknowledgement.'
Assert-Contains $app 'ReadStableM6FirmwareAutoProof' 'M7 must retain stable two-sample FF/FF proof.'
Assert-Contains $app 'AllowCustomAdmissionAfterRecoveryAsync' 'M7 must reopen admission only after fresh recovery.'
Assert-Contains $app 'Automatic policy remains OFF' 'M7 qualification must not enable automatic policy.'
Assert-NotContains $factory 'CreateLifecycleQualificationBackend' 'Production backend factory must not route through qualification bypass.'
Assert-NotContains $app 'SetSuspendState' 'M7 app must not issue a suspend/hibernate transition itself.'

Write-Host 'HP 8C40 M7 hibernation lifecycle invariant self-test: PASS' -ForegroundColor Green