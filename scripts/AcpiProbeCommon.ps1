# Shared ACPI probe guards; importing this file does not query hardware.
. (Join-Path $PSScriptRoot 'ScenarioAcpiEventFilter.ps1')
function Assert-Gm26Response([int]$Code,[byte[]]$Data) {
    if($Code -ne 0){throw ('HP return code '+$Code)}
    if($Data.Length -ne 4){throw ('Expected four bytes; received '+$Data.Length)}
    if($Data[0] -gt 1 -or $Data[1] -ne 0 -or $Data[2] -ne 0 -or $Data[3] -ne 0){throw 'Response outside the AML GM26 contract.'}
}
function Assert-Gm26Isolation {
    if(@(Get-Process -Name 'VictusFanControl*','OmenMon','HWiNFO*','RW*' -ErrorAction SilentlyContinue).Count){throw 'Close VictusFanControl and other hardware polling tools first.'}
    if(@(Get-CimInstance Win32_Service -Filter "Name LIKE 'VictusFanControl%'" -OperationTimeoutSec 5 | Where-Object {$_.State -ne 'Stopped'}).Count){throw 'VictusFanControl services must be stopped.'}
    $base=Join-Path $env:ProgramData 'VictusFanControl'
    if(Test-Path $base){
        foreach($directory in @(Get-ChildItem $base -Directory)){
            if(Test-Path (Join-Path $directory.FullName 'state\lease.json')){throw 'Pending fan lease: return to Firmware and close the app; preserve the lease file.'}
        }
    }
}
function Assert-Gm26Hardware {
    $board=Get-CimInstance Win32_BaseBoard -OperationTimeoutSec 5
    $system=Get-CimInstance Win32_ComputerSystem -OperationTimeoutSec 5
    $bios=Get-CimInstance Win32_BIOS -OperationTimeoutSec 5
    if($board.Manufacturer -ne 'HP' -or $board.Product -ne '8C40' -or $board.Version -ne '63.43' -or
        $system.Manufacturer -ne 'HP' -or $system.Model -ne 'Victus by HP Gaming Laptop 15-fa1xxx' -or
        $system.SystemSKUNumber -notmatch '^9D0R1LA(?:#.*)?$' -or $bios.SMBIOSBIOSVersion -ne 'F.18'){
        throw 'This probe requires HP 8C40 / 63.43 / 9D0R1LA / Victus 15-fa1xxx / F.18.'
    }
    return [pscustomobject]@{Board=$board.Product;Version=$board.Version;Model=$system.Model;Sku=$system.SystemSKUNumber;Bios=$bios.SMBIOSBIOSVersion}
}
