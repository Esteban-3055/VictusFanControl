param([switch]$SkipBuild,[switch]$OfflinePackages)
$ErrorActionPreference='Stop'
$repo=Split-Path -Parent $PSScriptRoot
Push-Location $repo
try {
    $evidence=Join-Path $repo 'logs/external-cooling-self-test'
    New-Item -ItemType Directory -Force -Path $evidence | Out-Null
    if(-not $SkipBuild) {
        $build=@('build','VictusFanControl.sln','-c','Release','-m:1','-warnaserror')
        if($OfflinePackages){$build+='-p:NuGetAudit=false'}
        & dotnet @build *> (Join-Path $evidence 'release-build.log')
        if($LASTEXITCODE -ne 0){Get-Content (Join-Path $evidence 'release-build.log') -Tail 30;throw 'Release build failed.'}
    }
    $results=@()
    function Invoke-Fixture([string]$assembly,[string]$option,[string]$name) {
        $log=Join-Path $evidence ($name+'.log')
        if($option){& dotnet $assembly $option *> $log}else{& dotnet $assembly *> $log}
        $code=$LASTEXITCODE
        if($code -ne 0){Get-Content $log -Tail 30;throw ($name+' failed: '+$code)}
        Write-Host ('PASS '+$name)
        [pscustomobject]@{name=$name;exitCode=$code}
    }
    $results+=Invoke-Fixture 'tools/ExternalCooling.Tests/bin/Release/net8.0/ExternalCooling.Tests.dll' '' 'protocol-transport-resources'
    $app='src/VictusFanControl.App/bin/Release/net8.0-windows/VictusFanControl.App.dll'
    foreach($flag in @('external-cooling-self-test','external-cooling-ui-self-test','external-curve-ui-self-test','product-gui-self-test','telemetry-coordination-self-test','performance-settings-self-test','performance-gui-self-test','wmi-fan-gui-self-test')) {
        $results+=Invoke-Fixture $app ('--'+$flag) $flag
    }
    $cli='src/VictusFanControl/bin/Release/net8.0-windows/VictusFanControl.dll'
    foreach($flag in @('safety-self-test','control-self-test','adaptive-policy-self-test','fan-wmi-telemetry-self-test','wmi-fan-backend-self-test','bios-contract-self-test','hp-backend-self-test','watchdog-lease-client-self-test')) {
        $results+=Invoke-Fixture $cli ('--'+$flag) $flag
    }
    $guardian='src/VictusFanControl.PerformanceGuardian/bin/Release/net8.0-windows/VictusFanControl.PerformanceGuardian.dll'
    foreach($flag in @('gui-recovery-self-test','product-fan-recovery-self-test')) {
        $results+=Invoke-Fixture $guardian ('--'+$flag) $flag
    }
    $sha=(& git rev-parse HEAD).Trim()
    [ordered]@{sourceHead=$sha;recordedAtUtc=[DateTimeOffset]::UtcNow.ToString('O');offlinePackageAuditSkipped=[bool]$OfflinePackages;
        physicalEsp32Pass=$false;physicalNotebookWrites=$false;checks=$results} |
        ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $evidence 'validation.json') -Encoding utf8
} finally {Pop-Location}
