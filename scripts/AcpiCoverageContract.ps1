# Exact contracts derived from the supplied HP 8C40/F.18 AML; no hardware on import.
function Get-AcpiCoverageRequest([string]$Kind) {
    switch -Exact ($Kind) {
        'gbif-control' { return @{Command=[uint32]1;Type=[uint32]7;Payload=[byte[]]@();Output=4} }
        'ecok' { return @{Command=[uint32]1;Type=[uint32]7;Payload=[byte[]](1,0,0,0);Output=4} }
        'fffs' { return @{Command=[uint32]0x20008;Type=[uint32]0x26;Payload=[byte[]]@();Output=4} }
        'rpm' { return @{Command=[uint32]0x20008;Type=[uint32]0x2D;Payload=[byte[]](0,0,0,0);Output=128} }
        'fan-status-cpu' { return @{Command=[uint32]0x20008;Type=[uint32]0x11;Payload=[byte[]](0,0,0,0);Output=4} }
        'fan-status-gpu' { return @{Command=[uint32]0x20008;Type=[uint32]0x11;Payload=[byte[]](1,0,0,0);Output=4} }
        default { throw 'Request is outside the read-only ACPI coverage whitelist.' }
    }
}
function Assert-AcpiCoverageResponse([string]$Kind,$Response) {
    $contract=Get-AcpiCoverageRequest $Kind
    if($null -eq $Response -or $null -eq $Response.Code -or $null -eq $Response.Data){throw 'Missing response fields.'}
    if($Response.Data.Length -ne $contract.Output){throw ('Unexpected output length for '+$Kind)}
    if($Kind -eq 'fffs'){Assert-Gm26Response $Response.Code $Response.Data;return}
    if($Kind -eq 'rpm'){
        if($Response.Code -ne 0 -or $Response.Data[0] -gt 100 -or $Response.Data[1] -gt 100){throw 'Invalid RPM response; expected rc=0 and speed bytes 0..100.'}
        if($null -eq $Response.NativeDurationMs -or $Response.NativeDurationMs -lt 0 -or $Response.NativeDurationMs -ge 3000){throw 'RPM response exceeded the existing 3 s freshness boundary.'}
        return
    }
    if($Kind -in @('fan-status-cpu','fan-status-gpu')){
        if($Response.Code -ne 0){throw ('Unexpected GM11 return code for '+$Kind)}
        return # Raw bytes only: no inferred ownership, setpoint or RPM semantics.
    }
    $expected=if($Kind -eq 'gbif-control'){5}else{6}
    if($Response.Code -ne $expected){
        if($Kind -eq 'ecok' -and $Response.Code -eq 13){throw 'GBIF reports ECOK false (0x0D); stop before further reads.'}
        throw ($Kind+': expected intentional diagnostic rc='+$expected+'; received '+$Response.Code)
    }
    if(@($Response.Data | Where-Object {$_ -ne 0}).Count){throw 'Diagnostic response contains unexpected data.'}
}
function Invoke-AcpiCoverageSequence([scriptblock]$Read,[scriptblock]$RecordRound,[scriptblock]$WaitRound,[bool]$IncludeFanStatus=$false,[bool]$Correlation=$false) {
    if($Correlation -and -not $IncludeFanStatus){throw 'Correlation requires GM11 reads.'}
    $sampleCount=if($Correlation){18}else{3}
    $control=& $Read 'gbif-control' 0
    Assert-AcpiCoverageResponse 'gbif-control' $control
    for($round=1;$round -le $sampleCount;$round++){
        $before=& $Read 'ecok' $round
        Assert-AcpiCoverageResponse 'ecok' $before
        $flag=& $Read 'fffs' $round
        Assert-AcpiCoverageResponse 'fffs' $flag
        $after=& $Read 'ecok' $round
        Assert-AcpiCoverageResponse 'ecok' $after
        $rpm=& $Read 'rpm' $round
        Assert-AcpiCoverageResponse 'rpm' $rpm
        $cpuStatus='';$gpuStatus='';$cpuCandidate=$null;$gpuCandidate=$null;$cpuUtc='';$gpuUtc=''
        $phase=if(-not $Correlation){'coverage'}elseif($round -le 6){'rest-planned'}elseif($round -le 12){'load-planned'}else{'recovery-planned'}
        if($IncludeFanStatus){
            $cpu=& $Read 'fan-status-cpu' $round
            Assert-AcpiCoverageResponse 'fan-status-cpu' $cpu
            $gpu=& $Read 'fan-status-gpu' $round
            Assert-AcpiCoverageResponse 'fan-status-gpu' $gpu
            $cpuCandidate=([int]$cpu.Data[2]*256)+[int]$cpu.Data[3]
            $gpuCandidate=([int]$gpu.Data[2]*256)+[int]$gpu.Data[3]
            $cpuUtc=$cpu.Utc;$gpuUtc=$gpu.Utc
            $cpuStatus=[BitConverter]::ToString($cpu.Data);$gpuStatus=[BitConverter]::ToString($gpu.Data)
        }
        & $RecordRound ([pscustomobject]@{phase=$phase;rpm_response_utc=$rpm.Utc;gm11_cpu_response_utc=$cpuUtc;gm11_gpu_response_utc=$gpuUtc;gm11_cpu_candidate_rpm=$cpuCandidate;gm11_gpu_candidate_rpm=$gpuCandidate;gm11_cpu_raw_hex=$cpuStatus;gm11_gpu_raw_hex=$gpuStatus;gm11_semantics='unqualified';round=$round;utc=[DateTimeOffset]::UtcNow.ToString('o');ecok_before_code=$before.Code;fffs=$flag.Data[0];ecok_after_code=$after.Code;cpu_nominal_rpm=([int]$rpm.Data[0]*100);gpu_nominal_rpm=([int]$rpm.Data[1]*100);fffs_gate_evidence='supported-by-supplied-AML-and-provider-codes';atomic_snapshot=$false})
        if($round -lt $sampleCount){& $WaitRound}
    }
}
function Test-AcpiCoverageContract {
    # Exercise the real sequence with a fake transport, including first-failure stop.
    foreach($failure in @('none','control','ecok','fffs','rpm','slow-rpm')){
        $state=@{Kinds=New-Object 'Collections.Generic.List[string]';Rounds=0;Failure=$failure}
        $read={param($kind,$round)
            $state.Kinds.Add($kind)
            $data=New-Object byte[] $(if($kind -eq 'rpm'){128}else{4})
            $code=if($kind -eq 'gbif-control'){5}elseif($kind -eq 'ecok'){6}else{0}
            $duration=1
            if($state.Failure -eq 'control' -and $kind -eq 'gbif-control'){$code=6}
            if($state.Failure -eq 'ecok' -and $kind -eq 'ecok'){$code=13}
            if($state.Failure -eq 'fffs' -and $kind -eq 'fffs'){$data[0]=2}
            if($state.Failure -eq 'rpm' -and $kind -eq 'rpm'){$data[0]=255}
            if($state.Failure -eq 'slow-rpm' -and $kind -eq 'rpm'){$duration=3000}
            return [pscustomobject]@{Code=$code;Data=$data;NativeDurationMs=$duration}
        }
        $rejected=$false
        try{Invoke-AcpiCoverageSequence $read {param($row) $state.Rounds++} {}}catch{$rejected=$true}
        if($failure -eq 'none'){
            if($rejected -or $state.Kinds.Count -ne 13 -or $state.Rounds -ne 3){throw 'Full sequence fixture failed.'}
        }else{
            $expected=if($failure -eq 'control'){1}elseif($failure -eq 'ecok'){2}elseif($failure -eq 'fffs'){3}else{5}
            if(-not $rejected -or $state.Rounds -ne 0 -or $state.Kinds.Count -ne $expected){throw ('First-failure stop fixture failed: '+$failure)}
        }
    }
    foreach($failure in @('none','cpu-code','gpu-length')){
        $state=@{Count=0;Rounds=0;Failure=$failure}
        $read={param($kind,$round)
            $state.Count++
            $request=Get-AcpiCoverageRequest $kind
            $data=New-Object byte[] $request.Output
            $code=if($kind -eq 'gbif-control'){5}elseif($kind -eq 'ecok'){6}else{0}
            if($kind -like 'fan-status-*'){$data=[byte[]](255,128,1,0)}
            if($state.Failure -eq 'cpu-code' -and $kind -eq 'fan-status-cpu'){$code=1}
            if($state.Failure -eq 'gpu-length' -and $kind -eq 'fan-status-gpu'){$data=[byte[]](0,0,0)}
            return [pscustomobject]@{Code=$code;Data=$data;NativeDurationMs=1}
        }
        $record={param($row)
            if($row.gm11_cpu_raw_hex -ne 'FF-80-01-00' -or $row.gm11_gpu_raw_hex -ne 'FF-80-01-00'){throw 'GM11 raw bytes changed.'}
            $state.Rounds++
        }
        $rejected=$false
        try{Invoke-AcpiCoverageSequence $read $record {} $true}catch{$rejected=$true}
        $expected=if($failure -eq 'none'){19}elseif($failure -eq 'cpu-code'){6}else{7}
        if($state.Count -ne $expected -or ($failure -eq 'none' -and ($rejected -or $state.Rounds -ne 3)) -or ($failure -ne 'none' -and (-not $rejected -or $state.Rounds -ne 0))){throw ('GM11 fixture failed: '+$failure)}
    }
    foreach($failure in @('none','late-gpu')){
        $state=@{Count=0;Rows=New-Object 'Collections.Generic.List[object]';Failure=$failure;Waits=0}
        $read={param($kind,$round)
            $state.Count++
            $data=New-Object byte[] (Get-AcpiCoverageRequest $kind).Output
            $code=if($kind -eq 'gbif-control'){5}elseif($kind -eq 'ecok'){6}else{0}
            if($kind -like 'fan-status-*'){$data=[byte[]](47,60,10,131)}
            if($state.Failure -eq 'late-gpu' -and $round -eq 7 -and $kind -eq 'fan-status-gpu'){$code=1}
            return [pscustomobject]@{Code=$code;Data=$data;NativeDurationMs=1;Utc='2026-10-03T23:00:00Z'}
        }
        $rejected=$false
        try{Invoke-AcpiCoverageSequence $read {param($row) $state.Rows.Add($row)} {$state.Waits++} $true $true}catch{$rejected=$true}
        if($failure -eq 'none'){
            if($rejected -or $state.Count -ne 109 -or $state.Rows.Count -ne 18 -or $state.Waits -ne 17){throw 'Correlation sequence fixture failed.'}
            foreach($phase in @('rest-planned','load-planned','recovery-planned')){
                if(@($state.Rows | Where-Object {$_.phase -eq $phase}).Count -ne 6){throw 'Correlation phase fixture failed.'}
            }
            if(@($state.Rows | Where-Object {$_.gm11_cpu_candidate_rpm -ne 2691 -or $_.gm11_gpu_candidate_rpm -ne 2691 -or $_.rpm_response_utc -ne '2026-10-03T23:00:00Z' -or $_.gm11_cpu_raw_hex -ne '2F-3C-0A-83'}).Count){throw 'Correlation decoding/evidence fixture failed.'}
        }elseif(-not $rejected -or $state.Count -ne 43 -or $state.Rows.Count -ne 6){throw 'Correlation late-failure fixture failed.'}
    }
    foreach($selector in @(@{Kind='fan-status-cpu';Byte=0},@{Kind='fan-status-gpu';Byte=1})){
        $request=Get-AcpiCoverageRequest $selector.Kind
        if($request.Command -ne 0x20008 -or $request.Type -ne 0x11 -or $request.Output -ne 4 -or [BitConverter]::ToString($request.Payload) -ne ($selector.Byte.ToString('00')+'-00-00-00')){throw 'GM11 request contract changed.'}
    }
    foreach($kind in @('gm27','restore','set-fan','arbitrary')){
        $rejected=$false;try{$null=Get-AcpiCoverageRequest $kind}catch{$rejected=$true}
        if(-not $rejected){throw 'Setter admitted by coverage whitelist.'}
    }
    Write-Host 'PASS: ACPI coverage sequence, diagnostic codes, malformed/stale responses, first-failure stop and setter exclusion; no hardware.'
}
