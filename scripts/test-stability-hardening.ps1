param([switch]$SkipBuild,[switch]$OfflinePackages)
$ErrorActionPreference='Stop'
$repo=Split-Path -Parent $PSScriptRoot
Push-Location $repo
try {
    $evidence=Join-Path $repo 'logs/stability-hardening'
    New-Item -ItemType Directory -Force -Path $evidence | Out-Null
    if(-not $SkipBuild) {
        foreach($project in @('VictusFanControl.sln','tools/ProductSetup/ProductSetup.csproj','tools/ControlIsolationPrototype/ControlIsolationPrototype.csproj')) {
            $arguments=@('build',$project,'-c','Release','-m:1','-warnaserror','-p:UseSharedCompilation=false','-nr:false')
            if($project -eq 'VictusFanControl.sln'){$arguments+='-p:VictusProductRelease=true'}
            if($OfflinePackages){$arguments+='-p:NuGetAudit=false'}
            $name=[IO.Path]::GetFileNameWithoutExtension($project)
            & dotnet @arguments *> (Join-Path $evidence ($name+'-build.log'))
            if($LASTEXITCODE -ne 0){throw ($name+' build failed.')}
        }
    }
    $checks=@()
    $runs=@(
        @('src/VictusFanControl.App/bin/Release/net8.0-windows/VictusFanControl.App.dll','--stability-pipeline-self-test','pipeline'),
        @('src/VictusFanControl.App/bin/Release/net8.0-windows/VictusFanControl.App.dll','--stability-gui-self-test','gui'),
        @('src/VictusFanControl.App/bin/Release/net8.0-windows/VictusFanControl.App.dll','--nvml-telemetry-self-test','nvml-isolation'),
        @('tools/ProductSetup/bin/Release/net8.0-windows/VictusSetup.dll','--self-test','installer-fixtures'),
        @('tools/ControlIsolationPrototype/bin/Release/net8.0-windows/VfcIsolationFixture.dll','--self-test','isolation-prototype')
    )
    foreach($run in $runs) {
        $log=Join-Path $evidence ($run[2]+'.log')
        & dotnet $run[0] $run[1] *> $log
        $code=$LASTEXITCODE
        $checks+= [ordered]@{name=$run[2];exitCode=$code;sha256=(Get-FileHash -LiteralPath $log -Algorithm SHA256).Hash.ToLowerInvariant()}
        if($code -ne 0){Get-Content -LiteralPath $log -Tail 20;throw ($run[2]+' failed: '+$code)}
        Write-Host ('PASS '+$run[2])
    }
    $sourceHead=(& git rev-parse HEAD).Trim()
    $changes=@(& git diff --name-only HEAD -- src tools scripts release Directory.Build.props)
    $newSources=@(& git ls-files --others --exclude-standard -- src tools scripts release)
    [ordered]@{
        schemaVersion=1;sourceHead=$sourceHead;sourceDirty=($changes.Count+$newSources.Count -gt 0)
        changedSourceFiles=$changes;untrackedSourceFiles=$newSources
        recordedAtUtc=[DateTimeOffset]::UtcNow.ToString('O');offlinePackageAuditSkipped=[bool]$OfflinePackages
        physicalHardwareWrites=$false;physicalEsp32Pass=$false;productionTaskRegistrationChanged=$false
        isolationPrototypePromotedToProduction=$false;checks=$checks
    } | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $evidence 'validation.json') -Encoding utf8
} finally {Pop-Location}
