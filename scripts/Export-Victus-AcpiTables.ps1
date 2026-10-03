# Exports OS-exposed static firmware tables; never evaluates AML or accesses EC ports.
[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$Destination,[switch]$SelfTest)
$ErrorActionPreference='Stop'
$utf8=New-Object Text.UTF8Encoding($false)
function Table-Header([byte[]]$Bytes) {
    if($Bytes.Length -lt 36){throw 'ACPI table shorter than its 36-byte header.'}
    $length=[BitConverter]::ToUInt32($Bytes,4)
    if($length -lt 36 -or $length -gt $Bytes.Length){throw 'ACPI declared length is outside captured data.'}
    $sum=0;for($j=0;$j -lt $length;$j++){$sum=($sum+[int]$Bytes[$j]) -band 255}
    [pscustomobject]@{Signature=[Text.Encoding]::ASCII.GetString($Bytes,0,4);DeclaredBytes=$length;CapturedBytes=$Bytes.Length;Revision=$Bytes[8];ChecksumValid=($sum -eq 0);OEM=[Text.Encoding]::ASCII.GetString($Bytes,10,6);OEMTable=[Text.Encoding]::ASCII.GetString($Bytes,16,8);OEMRevision=[BitConverter]::ToUInt32($Bytes,24)}
}
[IO.Directory]::CreateDirectory($Destination)|Out-Null
if($SelfTest){
    $fixture=New-Object byte[] 36
    [Text.Encoding]::ASCII.GetBytes('DSDT').CopyTo($fixture,0);[BitConverter]::GetBytes([uint32]36).CopyTo($fixture,4)
    $sum=0;foreach($b in $fixture){$sum=($sum+[int]$b) -band 255};$fixture[9]=[byte]((256-$sum) -band 255)
    $h=Table-Header $fixture;if($h.Signature -ne 'DSDT' -or -not $h.ChecksumValid){throw 'ACPI header/checksum fixture failed.'}
    $fixture[35]=1;if((Table-Header $fixture).ChecksumValid){throw 'Invalid ACPI checksum was accepted.'}
    $rejected=$false;try{Table-Header (New-Object byte[] 8)|Out-Null}catch{$rejected=$true};if(-not $rejected){throw 'Short ACPI table was accepted.'}
    'PASS: ACPI header, declared length and checksum fixtures; no firmware API call.';return
}
if($env:OS -ne 'Windows_NT'){throw 'Windows required.'}
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class VfcStaticFirmware {
 [DllImport("kernel32.dll", SetLastError=true)] public static extern uint EnumSystemFirmwareTables(uint provider, [Out] byte[] buffer, uint size);
 [DllImport("kernel32.dll", SetLastError=true)] public static extern uint GetSystemFirmwareTable(uint provider, uint id, [Out] byte[] buffer, uint size);
}
'@
$provider=[uint32]0x41435049
$size=[VfcStaticFirmware]::EnumSystemFirmwareTables($provider,$null,0)
if($size -eq 0 -or $size -gt 65536 -or $size%4 -ne 0){throw ('ACPI enumeration unavailable/invalid: size='+$size+'; Win32='+[Runtime.InteropServices.Marshal]::GetLastWin32Error())}
$ids=New-Object byte[] ([int]$size)
if([VfcStaticFirmware]::EnumSystemFirmwareTables($provider,$ids,$size) -ne $size){throw 'ACPI enumeration size changed; no partial list accepted.'}
$list=@();for($i=0;$i -lt $ids.Length;$i+=4){$list+=[pscustomobject]@{Id=[BitConverter]::ToUInt32($ids,$i);Signature=[Text.Encoding]::ASCII.GetString($ids,$i,4)}}
[IO.File]::WriteAllText((Join-Path $Destination 'enumeration.json'),(ConvertTo-Json -InputObject @($list) -Depth 5),$utf8)
$rows=@();$total=0L
foreach($group in @($list|Group-Object Id)){
 $id=[uint32]$group.Group[0].Id;$signature=$group.Group[0].Signature
 # MSDM contains an OEM Windows product key: do not export it.
 if($signature -eq 'MSDM'){$rows+=[pscustomobject]@{Signature=$signature;Occurrences=$group.Count;Status='excluded-product-key'};continue}
 if($signature -notmatch '^[A-Z0-9_! ]{4}$'){$rows+=[pscustomobject]@{Signature=$signature;Occurrences=$group.Count;Status='invalid-filename-signature'};continue}
 try{
  $length=[VfcStaticFirmware]::GetSystemFirmwareTable($provider,$id,$null,0)
  if($length -eq 0 -or $length -gt 4194304 -or $total+$length -gt 33554432){throw ('Table unavailable/over limit: '+$length)}
  $bytes=New-Object byte[] ([int]$length)
  if([VfcStaticFirmware]::GetSystemFirmwareTable($provider,$id,$bytes,$length) -ne $length){throw 'Table size changed during capture.'}
  $h=Table-Header $bytes
  $file=$signature.Trim()+'.dat';[IO.File]::WriteAllBytes((Join-Path $Destination $file),$bytes);$total+=$length
  $rows+=[pscustomobject]@{Signature=$signature;Occurrences=$group.Count;Status='exported-first-instance';File=$file;Header=$h;AdditionalInstancesUnavailable=([Math]::Max(0,$group.Count-1))}
  Write-Host ('ACPI '+$signature+': '+$length+' bytes; occurrences='+$group.Count+'; checksum='+$h.ChecksumValid)
 }catch{$rows+=[pscustomobject]@{Signature=$signature;Occurrences=$group.Count;Status='unavailable';Error=$_.Exception.Message};Write-Warning ($signature+': '+$_.Exception.Message)}
}
[IO.File]::WriteAllText((Join-Path $Destination 'tables.json'),(ConvertTo-Json -InputObject @($rows) -Depth 8),$utf8)
[IO.File]::WriteAllText((Join-Path $Destination 'LIMITES.txt'),'Static OS firmware API capture. No AML evaluation, EC reads, physical-memory access, driver installation or BIOS changes. GetSystemFirmwareTable exposes only the first table of each signature, even when enumeration lists multiple SSDTs. Missing instances are explicitly counted; this is not a complete ACPI namespace dump. MSDM excluded because it contains an OEM product key. Table checksum/header metadata is descriptive; it does not prove EC hardware health.',$utf8)
