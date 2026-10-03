$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'p16-hardening-helpers.ps1')

function Assert-True([bool]$Value,[string]$Message){if(-not $Value){throw $Message}}

Assert-True (Test-P16OrdinalContains -Text 'prefix ABC suffix' -Needle 'ABC') 'P16 ordinal contains helper did not match an exact-case substring.'
Assert-True (-not (Test-P16OrdinalContains -Text 'prefix ABC suffix' -Needle 'abc')) 'P16 ordinal contains helper must remain case-sensitive.'
Assert-True (-not (Test-P16OrdinalContains -Text 'prefix ABC suffix' -Needle 'XYZ')) 'P16 ordinal contains helper reported a missing substring.'


$state=[pscustomobject]@{Calls=0}
$result=Invoke-P16BoundedEcContentionRetry -MaximumAttempts 3 -DelayMilliseconds 0 -Operation {
    $state.Calls++
    if($state.Calls -lt 3){throw 'Timed out waiting for Global\Access_EC.'}
    return 42
}
Assert-True ($result -eq 42 -and $state.Calls -eq 3) 'P16 transient EC retry did not recover on the bounded final attempt.'

$nonTransient=[pscustomobject]@{Calls=0}
$nonTransientFailed=$false
try{
    Invoke-P16BoundedEcContentionRetry -MaximumAttempts 3 -DelayMilliseconds 0 -Operation {
        $nonTransient.Calls++
        throw 'synthetic non-transient probe failure'
    } | Out-Null
}catch{
    $nonTransientFailed=$_.Exception.Message -match 'synthetic non-transient'
}
Assert-True ($nonTransientFailed -and $nonTransient.Calls -eq 1) 'P16 bounded retry incorrectly retried a non-transient failure.'

$exhausted=[pscustomobject]@{Calls=0}
$exhaustedFailed=$false
try{
    Invoke-P16BoundedEcContentionRetry -MaximumAttempts 3 -DelayMilliseconds 0 -Operation {
        $exhausted.Calls++
        throw 'Timed out waiting for Global\Access_EC.'
    } | Out-Null
}catch{
    $exhaustedFailed=Test-P16EcMutexContentionText $_.Exception.ToString()
}
Assert-True ($exhaustedFailed -and $exhausted.Calls -eq 3) 'P16 bounded retry did not stop after exactly three transient attempts.'

$failureFirst=Resolve-P16InteractionOutcome -Lines @(
    'P13 manual request 40/40 FAILED CLOSED: synthetic',
    'P13 manual request 40/40: action=EnterCustomAndApply; authorized=True; authority=Custom;'
) -SuccessPattern 'P13 manual request 40/40: action=' -FailurePattern 'P13 manual request 40/40 FAILED CLOSED:'
Assert-True ([string]$failureFirst.Kind -ceq 'Failure') 'P16 interaction resolver must preserve the first FAILED CLOSED outcome.'

$successFirst=Resolve-P16InteractionOutcome -Lines @(
    'P13 manual request 40/40: action=ApplyChangedLevel; authorized=True; authority=Custom;',
    'unrelated later line'
) -SuccessPattern 'P13 manual request 40/40: action=ApplyChangedLevel;' -FailurePattern 'P13 manual request 40/40 FAILED CLOSED:'
Assert-True ([string]$successFirst.Kind -ceq 'Success') 'P16 interaction resolver did not recognize the expected success.'

$temp=Join-Path ([IO.Path]::GetTempPath()) ('VFC-P16-FenceTest-'+[Guid]::NewGuid().ToString('N'))
try{
    $headA=('a'*40)
    $headB=('b'*40)
    $evidence=Join-Path $temp 'evidence\attempt.json'
    $first=New-P16AuthorizationAttemptFence -Root $temp -AuthorizationHead $headA -EvidencePath $evidence
    Assert-True ($first.Claimed -and (Test-Path -LiteralPath $first.Path) -and (Test-Path -LiteralPath $evidence)) 'P16 first one-shot attempt fence claim failed.'

    $duplicateBlocked=$false
    try{
        New-P16AuthorizationAttemptFence -Root $temp -AuthorizationHead $headA | Out-Null
    }catch{
        $duplicateBlocked=$_.Exception.Message -match 'already consumed'
    }
    Assert-True $duplicateBlocked 'P16 one-shot attempt fence allowed the same authorization HEAD twice.'

    $second=New-P16AuthorizationAttemptFence -Root $temp -AuthorizationHead $headB
    Assert-True ($second.Claimed -and (Test-Path -LiteralPath $second.Path)) 'P16 attempt fence rejected a distinct fresh authorization HEAD.'
}finally{
    Remove-Item -LiteralPath $temp -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Host 'HP 8C40 P16B hardening helper self-test: PASS' -ForegroundColor Green

foreach($lines in @(
    @('P16 QUALIFICATION INTERRUPTED: suspend'),
    @('P13 manual request 30/30: action=EnterCustomAndApply;', 'P16 QUALIFICATION INTERRUPTED: resume'),
    @('P16 QUALIFICATION INTERRUPTED: suspend', 'Recovery completed; telemetry is healthy after 3 complete snapshots.')
)){
    $blocked=$false
    try{Resolve-P16InteractionOutcome -Lines $lines -SuccessPattern 'action=EnterCustomAndApply;' -FailurePattern 'FAILED CLOSED:' | Out-Null}
    catch{$blocked=$_.Exception.Message -match 'permanently interrupted'}
    Assert-True $blocked 'P16 interruption must invalidate the entire session, including success before interruption or healthy recovery afterward.'
}
Assert-P16QualificationSessionUninterrupted -Lines @()
Assert-P16QualificationSessionUninterrupted -Lines @('unrelated older power event')
Write-Host 'P16 causal interruption audit self-test: PASS' -ForegroundColor Green

$ci=[pscustomobject]@{id=123;head_sha=('a'*40);status='completed';conclusion='success';path='.github/workflows/build.yml';head_branch='feature/victus-8c40-p16-normal-manual';event='push';repository=[pscustomobject]@{full_name='Esteban-3055/VictusFanControl'}}
Assert-P16AuthorizationCiRun -Run $ci -ExpectedHead ('a'*40) -ExpectedRunId 123
foreach($property in @('head_sha','status','conclusion','path','head_branch','event','id')){
 $original=$ci.$property;$ci.$property=$(if($property -ceq 'id'){124}else{'wrong'})
 $blocked=$false;try{Assert-P16AuthorizationCiRun -Run $ci -ExpectedHead ('a'*40) -ExpectedRunId 123}catch{$blocked=$true}
 $ci.$property=$original;Assert-True $blocked ("P16 CI barrier must reject $property mismatch.")
}
$proofs=@(
 'P16 WMI COMMAND PROOF: target=30/30;resolution=100;samples=2;baselineQuery=1;query=3;queryStarted=101;commandCompleted=100;baselineRpm=2600/2400;rpm=3000/3000',
 'P16 WMI COMMAND PROOF: target=40/40;resolution=100;samples=2;baselineQuery=4;query=6;queryStarted=201;commandCompleted=200;baselineRpm=3000/3000;rpm=4000/4000',
 'P16 WMI COMMAND PROOF: target=30/30;resolution=100;samples=2;baselineQuery=7;query=9;queryStarted=301;commandCompleted=300;baselineRpm=4000/4000;rpm=3000/3000'
)
Assert-P16WmiCommandProof -Lines $proofs
foreach($replacement in @('samples=1','query=1','queryStarted=99')){
 $bad=@($proofs);$needle=$(if($replacement.StartsWith('samples')){'samples=2'}elseif($replacement.StartsWith('query=')){'query=3'}else{'queryStarted=101'})
 $bad[0]=$bad[0].Replace($needle,$replacement);$blocked=$false
 try{Assert-P16WmiCommandProof -Lines $bad}catch{$blocked=$true}
 Assert-True $blocked ("P16 WMI proof must reject $replacement.")
}
Write-Host 'P16 exact-head CI and WMI proof audit self-test: PASS' -ForegroundColor Green
