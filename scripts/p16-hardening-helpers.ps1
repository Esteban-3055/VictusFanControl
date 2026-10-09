$ErrorActionPreference='Stop'

function Test-P16EcMutexContentionText {
    param([Parameter(Mandatory=$true)][string]$Text)
    return $Text.IndexOf(
        'Timed out waiting for Global\Access_EC.',
        [StringComparison]::OrdinalIgnoreCase) -ge 0
}

function Test-P16OrdinalContains {
    param(
        [string]$Text,
        [Parameter(Mandatory=$true)][string]$Needle
    )
    if($null -eq $Text){ return $false }
    return $Text.IndexOf($Needle,[StringComparison]::Ordinal) -ge 0
}

function Invoke-P16BoundedEcContentionRetry {
    param(
        [Parameter(Mandatory=$true)][scriptblock]$Operation,
        [int]$MaximumAttempts=3,
        [int]$DelayMilliseconds=75
    )
    if($MaximumAttempts -lt 1){throw 'P16 bounded retry requires MaximumAttempts >= 1.'}
    if($DelayMilliseconds -lt 0){throw 'P16 bounded retry delay cannot be negative.'}

    for($attempt=1;$attempt -le $MaximumAttempts;$attempt++){
        try{
            return & $Operation
        }catch{
            $detail=$_.Exception.ToString()
            $transient=Test-P16EcMutexContentionText $detail
            if(-not $transient -or $attempt -ge $MaximumAttempts){
                throw
            }
            if($DelayMilliseconds -gt 0){
                Start-Sleep -Milliseconds $DelayMilliseconds
            }
        }
    }

    throw 'P16 bounded EC contention retry exhausted unexpectedly.'
}

function Assert-P16QualificationSessionUninterrupted {
    param([AllowEmptyCollection()][string[]]$Lines)
    foreach($line in $Lines){
        if(Test-P16OrdinalContains -Text ([string]$line) -Needle 'P16 QUALIFICATION INTERRUPTED:'){
            throw ("P16 qualification session was permanently interrupted: {0}" -f $line)
        }
    }
}

function Resolve-P16InteractionOutcome {
    param(
        [Parameter(Mandatory=$true)][string[]]$Lines,
        [Parameter(Mandatory=$true)][string]$SuccessPattern,
        [Parameter(Mandatory=$true)][string]$FailurePattern
    )

    # A success followed by interruption cannot qualify this session.
    Assert-P16QualificationSessionUninterrupted -Lines $Lines
    foreach($line in $Lines){
        $text=[string]$line
        if($text -match $FailurePattern){
            return [pscustomobject]@{Kind='Failure';Line=$text}
        }
        if($text -match $SuccessPattern){
            return [pscustomobject]@{Kind='Success';Line=$text}
        }
    }

    return [pscustomobject]@{Kind='Pending';Line=$null}
}

function New-P16AuthorizationAttemptFence {
    param(
        [Parameter(Mandatory=$true)][string]$Root,
        [Parameter(Mandatory=$true)][string]$AuthorizationHead,
        [string]$EvidencePath
    )

    if($AuthorizationHead -notmatch '^[0-9a-f]{40}$'){
        throw "P16 attempt fence requires a lowercase 40-hex authorization HEAD; observed '$AuthorizationHead'."
    }

    [IO.Directory]::CreateDirectory([IO.Path]::GetFullPath($Root)) | Out-Null
    $path=Join-Path $Root ("{0}.json" -f $AuthorizationHead)
    $payload=[ordered]@{
        schemaVersion=1
        gate='P16'
        authorizationHead=$AuthorizationHead
        claimedUtc=(Get-Date).ToUniversalTime().ToString('O')
        claimantProcessId=$PID
        oneShot=$true
    }
    $json=$payload|ConvertTo-Json -Depth 4
    $bytes=[Text.Encoding]::UTF8.GetBytes($json)

    $stream=$null
    try{
        $stream=[IO.File]::Open(
            $path,
            [IO.FileMode]::CreateNew,
            [IO.FileAccess]::Write,
            [IO.FileShare]::None)
        $stream.Write($bytes,0,$bytes.Length)
        $stream.Flush($true)
    }catch [IO.IOException]{
        if(Test-Path -LiteralPath $path -PathType Leaf){
            throw "P16 authorization HEAD $AuthorizationHead was already consumed by a prior physical attempt. A fresh authorization commit is required."
        }
        throw
    }finally{
        if($stream){$stream.Dispose()}
    }

    if(-not [string]::IsNullOrWhiteSpace($EvidencePath)){
        $parent=Split-Path -Parent $EvidencePath
        if(-not [string]::IsNullOrWhiteSpace($parent)){
            [IO.Directory]::CreateDirectory($parent)|Out-Null
        }
        Copy-Item -LiteralPath $path -Destination $EvidencePath
    }

    return [pscustomobject]@{
        Path=$path
        AuthorizationHead=$AuthorizationHead
        Claimed=$true
    }
}
function Assert-P16AuthorizationCiRun {
    param($Run,[string]$ExpectedHead,[long]$ExpectedRunId)
    if($ExpectedHead -notmatch '^[0-9a-f]{40}$' -or $ExpectedRunId -le 0 -or
       [string]$Run.head_sha -cne $ExpectedHead -or [long]$Run.id -ne $ExpectedRunId -or
       [string]$Run.status -cne 'completed' -or [string]$Run.conclusion -cne 'success' -or
       [string]$Run.path -cne '.github/workflows/build.yml' -or
       [string]$Run.head_branch -cne 'feature/victus-8c40-p16-normal-manual' -or
       [string]$Run.event -cne 'push' -or
       [string]$Run.repository.full_name -cne 'Esteban-3055/VictusFanControl'){
        throw 'P16 PHYSICAL BLOCKED: exact authorization HEAD must have completed successful repository build CI.'
    }
}

function Assert-P16WmiCommandProof {
    param([string[]]$Lines)
    $proofs=@($Lines|Where-Object{Test-P16OrdinalContains -Text ([string]$_) -Needle 'P16 WMI COMMAND PROOF:'})
    if($proofs.Count -ne 3){throw 'P16 requires exactly three committed WMI command proofs.'}
    $levels=@(30,40,30);$previous=0L
    for($i=0;$i -lt 3;$i++){
        $pattern='P16 WMI COMMAND PROOF: target=(\d+)/(\d+);resolution=100;samples=2;baselineQuery=(\d+);query=(\d+);queryStarted=(\d+);commandCompleted=(\d+);baselineRpm=(\d+)/(\d+);rpm=(\d+)/(\d+)$'
        $m=[regex]::Match([string]$proofs[$i],$pattern)
        if(-not $m.Success){throw 'P16 malformed WMI command proof.'}
        $v=@(1..10|ForEach-Object{[long]$m.Groups[$_].Value})
        if($v[0] -ne $levels[$i] -or $v[1] -ne $levels[$i] -or $v[2] -le 0 -or
           $v[3] -le $v[2] -or $v[3] -le $previous -or $v[4] -lt $v[5] -or
           $v[8] -le 0 -or $v[9] -le 0){throw 'P16 stale, duplicate, pre-command or wrong-target WMI proof.'}
        $previous=$v[3]
    }
}
