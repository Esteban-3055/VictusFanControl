param([Parameter(Mandatory=$true)][string]$SourceDirectory)
$ErrorActionPreference='Stop'
Add-Type -AssemblyName System.Drawing
$assetTaskRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../assets'))
$inputTaskRoot=(Resolve-Path -LiteralPath $SourceDirectory).Path
$sizes=@(16,20,24,32,40,48,64,128,256)
$mapping=[ordered]@{
    'program'='icono programa victus.png'
    'tray-default'='icono programa victus.png'
    'tray-automatic'='icono automatico.png'
    'tray-automatic-muted'='icono automatico y silenciado.png'
    'tray-error'='icono fallo programa.png'
}
$manifest=@()
foreach($name in $mapping.Keys){
    $source=Join-Path $inputTaskRoot $mapping[$name]
    if(-not(Test-Path -LiteralPath $source)){throw "Falta el recurso original: $source"}
    $png=Join-Path $assetTaskRoot "icons/$name.png"
    $ico=Join-Path $assetTaskRoot "icons/$name.ico"
    $originalHash=(Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash
    Copy-Item -LiteralPath $source -Destination $png -Force
    $image=[Drawing.Bitmap]::FromFile($source)
    $frames=New-Object 'System.Collections.Generic.List[byte[]]'
    try {
        foreach($size in $sizes){
            $bitmap=New-Object Drawing.Bitmap($size,$size,[Drawing.Imaging.PixelFormat]::Format32bppArgb)
            $graphics=[Drawing.Graphics]::FromImage($bitmap)
            $stream=New-Object IO.MemoryStream
            try {
                $graphics.Clear([Drawing.Color]::Transparent)
                $graphics.CompositingMode=[Drawing.Drawing2D.CompositingMode]::SourceCopy
                $graphics.InterpolationMode=[Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
                $graphics.PixelOffsetMode=[Drawing.Drawing2D.PixelOffsetMode]::HighQuality
                $graphics.DrawImage($image,(New-Object Drawing.Rectangle(0,0,$size,$size)))
                $bitmap.Save($stream,[Drawing.Imaging.ImageFormat]::Png)
                $frames.Add($stream.ToArray())
            } finally {$graphics.Dispose();$bitmap.Dispose();$stream.Dispose()}
        }
        $file=[IO.File]::Open($ico,[IO.FileMode]::Create,[IO.FileAccess]::Write,[IO.FileShare]::None)
        $writer=New-Object IO.BinaryWriter($file)
        try {
            $writer.Write([UInt16]0);$writer.Write([UInt16]1);$writer.Write([UInt16]$sizes.Count)
            $offset=6+16*$sizes.Count
            for($i=0;$i -lt $sizes.Count;$i++){
                $dimension=if($sizes[$i] -eq 256){0}else{$sizes[$i]}
                $writer.Write([byte]$dimension);$writer.Write([byte]$dimension)
                $writer.Write([byte]0);$writer.Write([byte]0);$writer.Write([UInt16]1);$writer.Write([UInt16]32)
                $writer.Write([UInt32]$frames[$i].Length);$writer.Write([UInt32]$offset);$offset+=$frames[$i].Length
            }
            foreach($bytes in $frames){$writer.Write([byte[]]$bytes)}
        } finally {$writer.Dispose();$file.Dispose()}
        if((Get-FileHash -LiteralPath $png -Algorithm SHA256).Hash -ne $originalHash){throw 'La copia PNG no coincide con el original.'}
        $manifest+=[ordered]@{name=$name;source=$mapping[$name];originalSha256=$originalHash;pngSha256=$originalHash;
            icoSha256=(Get-FileHash -LiteralPath $ico -Algorithm SHA256).Hash;sizes=$sizes;colors='Original RGBA; no recoloring'}
    } finally {$image.Dispose()}
}
$rotor=Join-Path $inputTaskRoot 'Rotor de ventilador.png'
Copy-Item -LiteralPath $rotor -Destination (Join-Path $assetTaskRoot 'rotors/home-fan.png') -Force
$rotorHash=(Get-FileHash -LiteralPath $rotor -Algorithm SHA256).Hash
if((Get-FileHash -LiteralPath (Join-Path $assetTaskRoot 'rotors/home-fan.png') -Algorithm SHA256).Hash -ne $rotorHash){throw 'El rotor no coincide con el original.'}
[ordered]@{source='User supplied November 2026 artwork';icons=$manifest;rotorSha256=$rotorHash;conversion='Lossless original PNG copies and multi-resolution PNG-in-ICO packaging'} |
    ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $assetTaskRoot 'icons/manifest.json') -Encoding UTF8
Write-Output 'PASS: exact PNG copies, original colors/transparency, five multi-resolution ICO containers and exact rotor.'
