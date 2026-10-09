# PnP metadata only: no ACPI evaluation, EC ports, fan commands or driver installation.
[CmdletBinding()]
param([string]$OutputRoot='', [switch]$RecoveryMetadata, [switch]$SelfTest)
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'AcpiProbeCommon.ps1')

function Get-BrokerPnpArguments([string]$InstanceId, [int]$BuildNumber) {
    if($InstanceId -notmatch '^ACPI\\PNP0C(?:09|14)\\[A-Za-z0-9_&.\\-]+$'){
        throw 'Only literal EC/ACPI-WMI device instance IDs are accepted.'
    }
    if($BuildNumber -lt 18362){throw 'PnPUtil device enumeration requires Windows build 18362 or later.'}
    $arguments='/enum-devices /instanceid "'+$InstanceId+'" /relations'
    if($BuildNumber -ge 19041){$arguments+=' /drivers'}
    if($BuildNumber -ge 22000){$arguments+=' /services /stack /interfaces'}
    return $arguments
}

function Invoke-BrokerPnpListing([string]$InstanceId, [int]$BuildNumber) {
    $arguments=Get-BrokerPnpArguments $InstanceId $BuildNumber
    return Invoke-BrokerMetadataProcess 'pnputil.exe' $arguments
}

function Invoke-BrokerMetadataProcess([string]$Executable, [string]$Arguments) {
    # Only these fixed read-only metadata operations are allowed.
    if(-not (($Executable -eq 'pnputil.exe' -and $Arguments -match '^/enum-devices /instanceid "ACPI\\PNP0C(?:09|14)\\[A-Za-z0-9_&.\\-]+" /relations(?: /drivers)?(?: /services /stack /interfaces)?$') -or
        ($Executable -eq 'reagentc.exe' -and $Arguments -eq '/info'))){throw 'Unsupported metadata operation.'}
    $process=New-Object Diagnostics.Process
    try {
        $process.StartInfo.FileName=Join-Path $env:SystemRoot ('System32\'+$Executable)
        $process.StartInfo.Arguments=$Arguments
        $process.StartInfo.UseShellExecute=$false
        $process.StartInfo.CreateNoWindow=$true
        $process.StartInfo.RedirectStandardOutput=$true
        $process.StartInfo.RedirectStandardError=$true
        if(-not $process.Start()){throw ($Executable+' did not start.')}
        $stdout=$process.StandardOutput.ReadToEndAsync()
        $stderr=$process.StandardError.ReadToEndAsync()
        $watch=[Diagnostics.Stopwatch]::StartNew()
        while(-not $process.WaitForExit(5000)){
            Write-Host ('Inventario '+$Executable+': '+[int]$watch.Elapsed.TotalSeconds+' s; no se leen registros EC.')
            if($watch.Elapsed.TotalSeconds -ge 30){
                $process.Kill()
                $null=$process.WaitForExit(5000)
                throw ($Executable+' exceeded the 30-second metadata deadline.')
            }
        }
        if(-not $stdout.Wait(5000) -or -not $stderr.Wait(5000)){throw 'Metadata output did not complete.'}
        return [pscustomobject]@{Executable=$Executable;Arguments=$Arguments;ExitCode=$process.ExitCode;Stdout=$stdout.Result;Stderr=$stderr.Result}
    } finally {$process.Dispose()}
}

function Invoke-BrokerMetadataQuery([scriptblock]$Query) {
    try {return [pscustomobject]@{Collected=$true;Data=(& $Query);Error=$null}}
    catch {return [pscustomobject]@{Collected=$false;Data=$null;Error=$_.Exception.Message}}
}

function Get-BrokerRecoveryMetadata {
    # Never query key protectors/recovery passwords or change boot settings.
    return [ordered]@{
        SecureBoot=(Invoke-BrokerMetadataQuery {Confirm-SecureBootUEFI -ErrorAction Stop})
        SystemVolume=(Invoke-BrokerMetadataQuery {
            Get-BitLockerVolume -MountPoint $env:SystemDrive -ErrorAction Stop |
                Select-Object MountPoint,VolumeStatus,ProtectionStatus,EncryptionPercentage,LockStatus
        })
        DeviceGuard=(Invoke-BrokerMetadataQuery {
            Get-CimInstance -Namespace root\Microsoft\Windows\DeviceGuard -ClassName Win32_DeviceGuard -OperationTimeoutSec 5 -ErrorAction Stop |
                Select-Object VirtualizationBasedSecurityStatus,SecurityServicesConfigured,SecurityServicesRunning,CodeIntegrityPolicyEnforcementStatus,UsermodeCodeIntegrityPolicyEnforcementStatus
        })
        WinRE=(Invoke-BrokerMetadataQuery {Invoke-BrokerMetadataProcess 'reagentc.exe' '/info'})
        RecoveryVerified=$false
        InstallationReady=$false
        SecuritySettingsChanged=$false
        RecoveryKeysCollected=$false
    }
}

if($SelfTest){
    $id='ACPI\PNP0C09\1'
    $old=Get-BrokerPnpArguments $id 19045
    $new=Get-BrokerPnpArguments $id 26100
    if($old -match '/stack|/services|/interfaces' -or $old -notmatch '/drivers'){throw 'Windows 10 flags invalid.'}
    if($new -notmatch '/stack' -or $new -notmatch '/instanceid "ACPI\\PNP0C09\\1"'){throw 'Windows 11 command invalid.'}
    foreach($invalid in @('ROOT\OTHER\1','ACPI\PNP0C09\1" /remove-device','ACPI\PNP0C14\*')){
        $rejected=$false
        try {$null=Get-BrokerPnpArguments $invalid 26100} catch {$rejected=$true}
        if(-not $rejected){throw 'Unexpected instance ID accepted.'}
    }
    $rejected=$false
    try {$null=Get-BrokerPnpArguments $id 17763} catch {$rejected=$true}
    if(-not $rejected){throw 'Unsupported enumeration version accepted.'}
    $falseValue=Invoke-BrokerMetadataQuery {$false}
    if(-not $falseValue.Collected -or $falseValue.Data -ne $false -or $null -ne $falseValue.Error){throw 'Valid false metadata lost.'}
    $failedQuery=Invoke-BrokerMetadataQuery {throw 'metadata fixture error'}
    if($failedQuery.Collected -or $null -ne $failedQuery.Data -or $failedQuery.Error -ne 'metadata fixture error'){throw 'Metadata failure converted into data.'}
    foreach($invalidOperation in @(@('reagentc.exe','/enable'),@('bcdedit.exe','/set testsigning on'),@('pnputil.exe','/add-driver example.inf'),@('pnputil.exe','/enum-devices /instanceid "ACPI\PNP0C09\1" /relations /remove-device'))){
        $rejected=$false
        try {$null=Invoke-BrokerMetadataProcess $invalidOperation[0] $invalidOperation[1]} catch {$rejected=$true}
        if(-not $rejected){throw 'Mutating metadata command accepted.'}
    }
    Write-Host 'Broker metadata command fixtures passed; no hardware queried.'
    return
}

if($env:OS -ne 'Windows_NT'){throw 'Windows is required.'}
if(-not $OutputRoot){$OutputRoot=Join-Path (Split-Path -Parent $PSScriptRoot) 'diagnostics'}
$folder=Join-Path ([IO.Path]::GetFullPath($OutputRoot)) ('ACPI-BrokerPreflight_'+(Get-Date -Format 'yyyyMMdd_HHmmss')+'_'+[Guid]::NewGuid().ToString('N').Substring(0,6))
New-Item $folder -ItemType Directory -Force | Out-Null
$summary=[ordered]@{SchemaVersion=1;StartedUtc=[DateTimeOffset]::UtcNow.ToString('o');FinishedUtc=$null;MetadataCollected=$false;Reason='incomplete';EcDeviceCount=0;WmiDeviceCount=0;AcpiEvaluationPerformed=$false;DirectEcAccess=$false;FanSetters=$false;DriverInstalled=$false;FieldUnitSupport='unproven';CompleteEcGuardSupport='unproven';ProductionReady=$false}
$failure=$null
try {
    Write-Host 'Paso 1/3: identidad y Windows; solo inventario del sistema.'
    $identity=Assert-Gm26Hardware
    $identity | ConvertTo-Json | Set-Content (Join-Path $folder 'hardware.json') -Encoding UTF8
    $os=Get-CimInstance Win32_OperatingSystem -OperationTimeoutSec 5
    $build=[int]$os.BuildNumber
    [ordered]@{Caption=$os.Caption;Version=$os.Version;BuildNumber=$build;Architecture=$os.OSArchitecture;PowerShell=$PSVersionTable.PSVersion.ToString();AcpiSysVersion=(Get-Item (Join-Path $env:SystemRoot 'System32\drivers\acpi.sys')).VersionInfo.FileVersion} | ConvertTo-Json | Set-Content (Join-Path $folder 'windows.json') -Encoding UTF8
    Write-Host 'Paso 2/3: dispositivos EC y ACPI-WMI presentes, propiedades PnP.'
    Import-Module PnpDevice
    $devices=@(Get-PnpDevice -PresentOnly | Where-Object {$_.InstanceId -match '^ACPI\\PNP0C(?:09|14)\\'})
    $summary.EcDeviceCount=@($devices | Where-Object {$_.InstanceId -match '^ACPI\\PNP0C09\\'}).Count
    $summary.WmiDeviceCount=@($devices | Where-Object {$_.InstanceId -match '^ACPI\\PNP0C14\\'}).Count
    $devices | Select-Object InstanceId,Class,FriendlyName,Status | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $folder 'devices.json') -Encoding UTF8
    if($summary.EcDeviceCount -eq 0){throw 'No present ACPI PNP0C09 device found; do not infer an EC PDO.'}
    $keys=@('DEVPKEY_Device_HardwareIds','DEVPKEY_Device_CompatibleIds','DEVPKEY_Device_PDOName','DEVPKEY_Device_Parent','DEVPKEY_Device_Service','DEVPKEY_Device_UpperFilters','DEVPKEY_Device_LowerFilters','DEVPKEY_Device_ClassGuid','DEVPKEY_Device_DriverInfPath','DEVPKEY_Device_DriverVersion','DEVPKEY_Device_ProblemCode')
    $records=@()
    foreach($device in $devices){
        $properties=@()
        foreach($key in $keys){
            try {
                $property=Get-PnpDeviceProperty -InstanceId $device.InstanceId -KeyName $key
                $properties += [pscustomobject]@{Key=$key;Type=[string]$property.Type;Data=$property.Data;Error=$null}
            } catch {$properties += [pscustomobject]@{Key=$key;Type=$null;Data=$null;Error=$_.Exception.Message}}
        }
        $records += [pscustomobject]@{InstanceId=$device.InstanceId;Properties=$properties}
    }
    ConvertTo-Json -InputObject $records -Depth 8 | Set-Content (Join-Path $folder 'pnp-properties.json') -Encoding UTF8
    Write-Host 'Paso 3/3: pila, relaciones y controladores mediante PnPUtil /enum-devices.'
    $listings=@()
    foreach($device in $devices){
        try {$listing=Invoke-BrokerPnpListing $device.InstanceId $build;$listings += [pscustomobject]@{InstanceId=$device.InstanceId;Result=$listing;Error=$null}}
        catch {$listings += [pscustomobject]@{InstanceId=$device.InstanceId;Result=$null;Error=$_.Exception.Message}}
    }
    ConvertTo-Json -InputObject $listings -Depth 6 | Set-Content (Join-Path $folder 'pnputil.json') -Encoding UTF8
    if($RecoveryMetadata){
        Write-Host 'Recuperacion: Secure Boot, cifrado del sistema, DeviceGuard y WinRE; solo lectura.'
        Get-BrokerRecoveryMetadata | ConvertTo-Json -Depth 8 | Set-Content (Join-Path $folder 'recovery-metadata.json') -Encoding UTF8
    }
    $summary.MetadataCollected=$true
    $summary.Reason='inventory-only; review per-property errors and PnPUtil exit codes; no broker viability conclusion'
} catch {
    $failure=$_
    $summary.Reason=$_.Exception.Message
    $_ | Out-String | Set-Content (Join-Path $folder 'error.txt') -Encoding UTF8
} finally {
    $summary.FinishedUtc=[DateTimeOffset]::UtcNow.ToString('o')
    $summary | ConvertTo-Json -Depth 6 | Set-Content (Join-Path $folder 'summary.json') -Encoding UTF8
    $manifest=@(Get-ChildItem $folder -File | Sort-Object Name | ForEach-Object {[pscustomobject]@{file=$_.Name;bytes=$_.Length;sha256=(Get-FileHash $_.FullName -Algorithm SHA256).Hash}})
    ConvertTo-Json -InputObject $manifest -Depth 4 | Set-Content (Join-Path $folder 'manifest.json') -Encoding UTF8
    Compress-Archive -Path (Join-Path $folder '*') -DestinationPath ($folder+'.zip')
    (Get-FileHash ($folder+'.zip') -Algorithm SHA256).Hash | Set-Content ($folder+'.zip.sha256') -Encoding ASCII
    Write-Host ('Evidencia: '+$folder+'.zip')
}
if($null -ne $failure){throw $failure}
