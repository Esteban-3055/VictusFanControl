param(
    [Parameter(Mandatory=$true)][string]$InnerZipPath,
    [Parameter(Mandatory=$true)][string]$DestinationPath
)

$ErrorActionPreference='Stop'
$zip=[IO.Path]::GetFullPath($InnerZipPath)
$destination=[IO.Path]::GetFullPath($DestinationPath)
if(-not (Test-Path -LiteralPath $zip -PathType Leaf)){throw "P15A inner RC ZIP missing: $zip"}
if(Test-Path -LiteralPath $destination){
    $existing=@(Get-ChildItem -LiteralPath $destination -Force -ErrorAction Stop)
    if($existing.Count -ne 0){throw "P15A payload extraction destination must be empty: $destination"}
}else{
    New-Item -ItemType Directory -Force -Path $destination | Out-Null
}

Expand-Archive -LiteralPath $zip -DestinationPath $destination
$expectedRootName=[IO.Path]::GetFileNameWithoutExtension($zip)
$roots=@(Get-ChildItem -LiteralPath $destination -Directory -Force)
$topFiles=@(Get-ChildItem -LiteralPath $destination -File -Force)
if($roots.Count -ne 1 -or $topFiles.Count -ne 0 -or $roots[0].Name -cne $expectedRootName){
    throw "P15A audited RC payload layout mismatch. Expected exactly one package root '$expectedRootName'."
}
$root=$roots[0].FullName
$manifest=Join-Path $root 'PACKAGE-MANIFEST.json'
$appExe=Join-Path $root 'app\VictusFanControl.App.exe'
$modules=Join-Path $root 'app\modules'
$intel=Join-Path $modules 'IntelMSR.bin'
$ec=Join-Path $modules 'LpcACPIEC.bin'
foreach($required in @($manifest,$appExe,$intel,$ec)){
    if(-not (Test-Path -LiteralPath $required -PathType Leaf)){
        throw "P15A audited RC payload required file missing after package-root resolution: $required"
    }
}

[pscustomobject]@{
    PackageRoot=$root
    ManifestPath=$manifest
    AppExe=$appExe
    ModulesDirectory=$modules
}
