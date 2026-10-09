# Shared current boundary for historical milestone invariants.
function Test-P16CManualPromotion {
 $repo=Split-Path -Parent $PSScriptRoot
 $path=Join-Path $repo 'release/p16c-manual-promotion.json'
 if(-not(Test-Path -LiteralPath $path)){return $false}
 $c=Get-Content -LiteralPath $path -Raw|ConvertFrom-Json
 $p=Get-Content -LiteralPath (Join-Path $repo 'release/p16-target-checkpoint.json') -Raw|ConvertFrom-Json
 $e=$p.normalManual.physicalAttemptHistory[6]
 if($c.status -cne 'P16C_MANUAL_PROMOTED_AWAITING_SAME_HEAD_CI' -or $c.targetProfileId -cne 'HP-8C40-9D0R1LA-F18' -or -not $c.manualExecutionAuthorized -or $c.automaticExecutionAuthorized -or $c.controlEnabledByDefault -or $c.candidateCurveAuthorizedForProduction -or -not $c.sameHeadCiRequiredBeforeDistribution -or $c.sourcePhysicalClosureHead -cne '335c9dec2f30bf392e84e7a8d0ab487bf8e91fb8' -or [long]$c.sourceClosureCiRunId -ne 37100418375 -or $c.sourceClosureCiResult -cne 'SUCCESS' -or $c.evidenceZipSha256 -cne $e.evidenceZipSha256 -or $e.result -cne 'PASS' -or $e.sourceHead -cne $c.sourceAttemptHead -or -not $p.normalManual.physicalPassed -or -not $p.normalManual.evidenceClosed -or -not $p.normalManual.physicalClosure.closed -or $p.normalManual.executionAuthorized -or $p.normalManual.controllerPhysicalExecutionAuthorized -or $p.normalManual.physicalGatesOpened -or -not $p.normalManual.authorization.authorizationConsumed){throw 'P16C promotion lacks exact physical closure or opens a forbidden boundary.'}
 foreach($flag in @('hardwareExecutionByPromotionCommit','fanWritesChanged','fanFreshnessLimitChanged','installedM4WatchdogChanged','legacy88F8Changed','thermalThresholdsChanged','qualificationGateOpened','persistedAuthorityAllowed')){if([bool]$c.$flag){throw "P16C scope changed: $flag"}}
 if($c.startupMode -cne 'Firmware' -or -not $c.requiresExplicitManualAndApply -or [int]$c.minimumEqualLevel -ne 10 -or [int]$c.maximumEqualLevel -ne 50){throw 'P16C startup/manual range contract changed.'}
 return $true
}
function Assert-CurrentManualAuthorizationBoundary([string]$GateSource) {
 $expected=if(Test-P16CManualPromotion){'ManualExecutionAuthorized = true'}else{'ManualExecutionAuthorized = false'}
 if($GateSource.IndexOf($expected,[StringComparison]::Ordinal)-lt 0){throw 'Current Manual source gate does not match the evidence-backed promotion boundary.'}
 if($GateSource.IndexOf('AutomaticExecutionAuthorized = false',[StringComparison]::Ordinal)-lt 0){throw 'Automatic must remain closed.'}
}
