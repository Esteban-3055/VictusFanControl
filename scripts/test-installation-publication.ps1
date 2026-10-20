$ErrorActionPreference='Stop'
# Execute only the production directory-publication block in an isolated tree.
# The real installer, startup registration and application are never invoked.
$source=Join-Path (Split-Path -Parent $PSScriptRoot) 'release/Install-VictusFanControl.ps1'
$tokens=$null;$errors=$null
$ast=[Management.Automation.Language.Parser]::ParseFile($source,[ref]$tokens,[ref]$errors)
if($errors.Count){throw 'Installer parse failed.'}
$block=$ast.FindAll({param($node) $node -is [Management.Automation.Language.IfStatementAst] -and $node.Extent.Text.StartsWith('if(-not (Test-Path -LiteralPath $destination))')},$true)
if($block.Count -ne 1){throw 'Production publication block not found.'}
$publish=[ScriptBlock]::Create($block[0].Extent.Text.Replace('$PSScriptRoot','$fixtureSource'))
$root=Join-Path ([IO.Path]::GetTempPath()) ('vfc-installation-publication-'+[Guid]::NewGuid().ToString('N'))
$fixtureSource=Join-Path $root 'source';$releasesRoot=Join-Path $root 'releases'
[IO.Directory]::CreateDirectory($fixtureSource) | Out-Null
[IO.Directory]::CreateDirectory($releasesRoot) | Out-Null
[IO.File]::WriteAllText((Join-Path $fixtureSource 'payload.txt'),'same verified payload')
# A competing publication occurs after production observed an absent destination,
# but before its rename. This is the interleaving from the original audit.
[IO.File]::WriteAllText((Join-Path $fixtureSource 'Start-ProductGui.ps1'),@'
param($Mode)
if($Mode -ne 'Verify'){throw 'Fixture accepts verification only.'}
if($env:VFC_PUBLICATION_RACE_DESTINATION){[IO.Directory]::CreateDirectory($env:VFC_PUBLICATION_RACE_DESTINATION) | Out-Null}
'@)
try {
    foreach($race in @($false,$true)) {
        $destination=Join-Path $releasesRoot ('build-'+$race)
        $env:VFC_PUBLICATION_RACE_DESTINATION=if($race){$destination}else{''}
        & $publish
        if(-not [IO.Directory]::Exists($destination)){throw 'Publication did not produce a destination.'}
        if(@(Get-ChildItem -LiteralPath $destination -Directory).Count){throw 'Staging was nested inside destination.'}
        if(-not $race -and [IO.File]::ReadAllText((Join-Path $destination 'payload.txt')) -ne 'same verified payload'){throw 'Published content mismatch.'}
        & $publish
        if(@(Get-ChildItem -LiteralPath $releasesRoot -Filter '*.staging-*').Count){throw 'Owned staging was not removed.'}
    }
    $lockPath=Join-Path $root 'installation.lock'
    $owner=[IO.File]::Open($lockPath,[IO.FileMode]::OpenOrCreate,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
    try {
        $refused=$false
        try {$second=[IO.File]::Open($lockPath,[IO.FileMode]::OpenOrCreate,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None);$second.Dispose()}
        catch [IO.IOException] {$refused=$true}
        if(-not $refused){throw 'Second installer acquired the active installation lock.'}
    } finally {$owner.Dispose()}
    Write-Host 'PASS installation publication: actual production block; absent/idempotent destination, racing directory never nests staging; competing installer lock refused; zero installation/hardware changes.'
} finally {
    Remove-Item Env:VFC_PUBLICATION_RACE_DESTINATION -ErrorAction SilentlyContinue
    $resolved=[IO.Path]::GetFullPath($root)
    if([IO.Path]::GetDirectoryName($resolved) -ne [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\','/')){throw 'Unexpected fixture cleanup path.'}
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
