$ErrorActionPreference='Stop'
$helper=Join-Path $PSScriptRoot 'expand-p15-rc-payload.ps1'
$temp=Join-Path ([IO.Path]::GetTempPath()) ('VFC-P15A-PayloadLayout-'+[Guid]::NewGuid().ToString('N'))
$zip=Join-Path $temp 'VictusFanControl-0.4.0-rc.1-win-x64.zip'
$extract=Join-Path $temp 'extract'
try{
    New-Item -ItemType Directory -Force -Path $temp | Out-Null
    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $stream=[IO.File]::Open($zip,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None)
    try{
        $archive=New-Object IO.Compression.ZipArchive($stream,[IO.Compression.ZipArchiveMode]::Create,$false)
        try{
            foreach($entryName in @(
                'VictusFanControl-0.4.0-rc.1-win-x64/PACKAGE-MANIFEST.json',
                'VictusFanControl-0.4.0-rc.1-win-x64/app/VictusFanControl.App.exe',
                'VictusFanControl-0.4.0-rc.1-win-x64/app/modules/IntelMSR.bin',
                'VictusFanControl-0.4.0-rc.1-win-x64/app/modules/LpcACPIEC.bin'
            )){
                $entry=$archive.CreateEntry($entryName,[IO.Compression.CompressionLevel]::NoCompression)
                $writer=New-Object IO.StreamWriter($entry.Open())
                try{$writer.Write('P15A-layout-self-test')}finally{$writer.Dispose()}
            }
        }finally{$archive.Dispose()}
    }finally{$stream.Dispose()}

    $resolved=& $helper -InnerZipPath $zip -DestinationPath $extract
    $expectedRoot=Join-Path $extract 'VictusFanControl-0.4.0-rc.1-win-x64'
    if([string]$resolved.PackageRoot -cne $expectedRoot){throw 'P15A resolver returned the wrong package root.'}
    if([string]$resolved.AppExe -cne (Join-Path $expectedRoot 'app\VictusFanControl.App.exe')){throw 'P15A resolver returned the wrong GUI path.'}
    if([string]$resolved.ModulesDirectory -cne (Join-Path $expectedRoot 'app\modules')){throw 'P15A resolver returned the wrong modules path.'}
    Write-Host 'P15A RC payload layout self-test: PASS' -ForegroundColor Green
}finally{
    Remove-Item -LiteralPath $temp -Recurse -Force -ErrorAction SilentlyContinue
}
