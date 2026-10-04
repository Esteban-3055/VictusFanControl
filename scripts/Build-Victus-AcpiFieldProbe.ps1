[CmdletBinding()]
param([string]$OutputRoot='', [string]$PackageRoot='')
$ErrorActionPreference='Stop'
if($env:OS -ne 'Windows_NT'){throw 'Windows/MSVC is required; this script builds only and never installs a driver.'}
$repo=Split-Path -Parent $PSScriptRoot
if(-not $OutputRoot){$OutputRoot=Join-Path $repo 'artifacts\acpi-field-probe'}
if(-not $PackageRoot){$PackageRoot=Join-Path $OutputRoot 'packages'}
$OutputRoot=[IO.Path]::GetFullPath($OutputRoot)
$PackageRoot=[IO.Path]::GetFullPath($PackageRoot)
New-Item $OutputRoot -ItemType Directory -Force | Out-Null
$vswhere=Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
$vs=& $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if(-not $vs){throw 'Visual Studio C++ x64 tools unavailable.'}
Import-Module (Join-Path $vs 'Common7\Tools\Microsoft.VisualStudio.DevShell.dll')
Enter-VsDevShell -VsInstallPath $vs -SkipAutomaticLocation -DevCmdArguments '-arch=x64 -host_arch=x64'
$version='10.0.26100.1'
& nuget.exe install Microsoft.Windows.WDK.x64 -Version $version -OutputDirectory $PackageRoot -Source https://api.nuget.org/v3/index.json -NonInteractive -DirectDownload
if($LASTEXITCODE -ne 0){throw 'Pinned WDK package restore failed.'}
$headers=@(Get-ChildItem $PackageRoot -Filter wdf.h -Recurse | Where-Object {$_.DirectoryName -match '[\\/]kmdf[\\/]'} | Sort-Object {[version]$_.Directory.Name} -Descending)
if(-not $headers.Count){throw 'KMDF headers unavailable.'}
$wdf=$headers[0].DirectoryName
$wdfVersion=$headers[0].Directory.Name
$km=(Get-ChildItem $PackageRoot -Filter ntddk.h -Recurse | Select-Object -First 1).DirectoryName
$acpi=(Get-ChildItem $PackageRoot -Filter acpiioct.h -Recurse | Select-Object -First 1).DirectoryName
$shared=(Get-ChildItem $PackageRoot -Filter ntdef.h -Recurse | Select-Object -First 1).DirectoryName
$kmLib=(Get-ChildItem $PackageRoot -Filter ntoskrnl.lib -Recurse | Where-Object {$_.DirectoryName -match '[\\/]x64$'} | Select-Object -First 1).DirectoryName
$wdfLib=(Get-ChildItem $PackageRoot -Filter WdfDriverEntry.lib -Recurse | Where-Object {$_.Directory.Name -eq $wdfVersion -and $_.DirectoryName -match '[\\/]x64[\\/]'} | Select-Object -First 1).DirectoryName
if(-not $acpi -or -not $km -or -not $shared -or -not $kmLib -or -not $wdfLib){throw 'WDK include/library layout incomplete.'}
$source=Join-Path $repo 'drivers\AcpiFieldProbe'
Push-Location $OutputRoot
try {
    $minor=([version]$wdfVersion).Minor
    & cl.exe /nologo /c /TC /kernel /W4 /WX /GS /guard:cf /D_AMD64_ /DAMD64 /D_WIN64 /D_WIN32_WINNT=0x0A00 /DNTDDI_VERSION=0x0A000000 /DKMDF_VERSION_MAJOR=1 "/DKMDF_VERSION_MINOR=$minor" "/I$km" "/I$shared" "/I$acpi" "/I$wdf" /Fodriver.obj (Join-Path $source 'driver.c')
    if($LASTEXITCODE -ne 0){throw 'Kernel source compilation failed.'}
    & link.exe /nologo /driver /subsystem:native /entry:FxDriverEntry /nodefaultlib /machine:x64 /guard:cf /out:AcpiFieldProbe.sys driver.obj "/libpath:$kmLib" "/libpath:$wdfLib" ntoskrnl.lib hal.lib BufferOverflowFastFailK.lib WdfLdr.lib WdfDriverEntry.lib
    if($LASTEXITCODE -ne 0){throw 'Kernel link failed.'}
    & cl.exe /nologo /TC /W4 /WX /Fecontract-test.exe (Join-Path $source 'contract-test.c')
    if($LASTEXITCODE -ne 0){throw 'Contract fixture build failed.'}
    & .\contract-test.exe
    if($LASTEXITCODE -ne 0){throw 'Contract fixture failed.'}
    & cl.exe /nologo /TC /W4 /WX /wd4505 /Feprobe-client.exe (Join-Path $source 'probe-client.c') /link setupapi.lib
    if($LASTEXITCODE -ne 0){throw 'Control client build failed.'}
    & .\probe-client.exe --invalid
    if($LASTEXITCODE -ne 2){throw 'Client usage rejection failed.'}
    & .\probe-client.exe --control
    if($LASTEXITCODE -ne 4){throw 'CI must have no installed probe; absence was not preserved as a separate error.'}
    $binaries=@('AcpiFieldProbe.sys','contract-test.exe','probe-client.exe')
    [ordered]@{WdkPackage=$version;Kmdf=$wdfVersion;FieldProbesEnabled=$false;DriverInstalled=$false;DriverSigned=$false;ProductionReady=$false;Binaries=@($binaries | ForEach-Object {[ordered]@{file=$_;sha256=(Get-FileHash $_ -Algorithm SHA256).Hash}})} | ConvertTo-Json -Depth 6 | Set-Content build-manifest.json -Encoding UTF8
} finally {Pop-Location}
