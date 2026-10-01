param(
    [Parameter(Mandatory = $true)][string]$ArtifactDirectory,
    [Parameter(Mandatory = $true)][ValidatePattern('^[0-9a-fA-F]{40}$')][string]$SourceHead,
    [Parameter(Mandatory = $true)][string]$Repository,
    [Parameter(Mandatory = $true)][string]$Ref,
    [Parameter(Mandatory = $true)][ValidateRange(1, [long]::MaxValue)][long]$RunId,
    [Parameter(Mandatory = $true)][ValidateRange(1, [int]::MaxValue)][int]$RunNumber,
    [Parameter(Mandatory = $true)][string]$ArtifactName
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$dir = [IO.Path]::GetFullPath($ArtifactDirectory)
$release = Get-Content -LiteralPath (Join-Path $root 'release\p14-software-rc.json') -Raw | ConvertFrom-Json

if ([string]$release.version -ne '0.4.0-rc.1') { throw 'Unexpected P14 RC version.' }
if (-not [bool]$release.productization.packageClosure.closed) { throw 'P14.4 requires the formal P14.3 package closure.' }
if ([string]$release.productization.packageClosure.result -ne 'PASS') { throw 'P14.4 requires P14.3 package PASS.' }
if ([bool]$release.productization.finalSoftwareRcAuditClosed) { throw 'P14.5 final audit must remain open during P14.4.' }
if (-not [bool]$release.safetyBoundary.softwareOnly) { throw 'P14.4 must remain software-only.' }
if ([bool]$release.safetyBoundary.controlEnabledByDefault -or [bool]$release.safetyBoundary.automaticPolicyEnabled -or [bool]$release.safetyBoundary.manualExecutionAuthorized -or [bool]$release.safetyBoundary.automaticExecutionAuthorized) { throw 'P14.4 hardware/control boundary is unexpectedly open.' }

$zipName = [string]$release.productization.packageName
$zipPath = Join-Path $dir $zipName
$shaPath = $zipPath + '.sha256'
if (-not (Test-Path -LiteralPath $zipPath -PathType Leaf)) { throw "P14.4 RC ZIP missing: $zipPath" }
if (-not (Test-Path -LiteralPath $shaPath -PathType Leaf)) { throw "P14.4 RC SHA-256 file missing: $shaPath" }

& (Join-Path $root 'scripts\verify-p14-package.ps1') -ZipPath $zipPath -Sha256Path $shaPath -ExpectedSourceHead $SourceHead

$zipHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $zipPath).Hash.ToLowerInvariant()
$shaText = ([IO.File]::ReadAllText($shaPath)).Trim()
$declaredHash = ($shaText -split '\s+')[0].ToLowerInvariant()
if ($zipHash -ne $declaredHash) { throw 'P14.4 staged .sha256 does not authenticate the RC ZIP.' }

$attestationPath = Join-Path $dir 'P14-ARTIFACT-ATTESTATION.json'
if (Test-Path -LiteralPath $attestationPath) { throw "P14.4 attestation output already exists: $attestationPath" }

$attestation = [ordered]@{
    schemaVersion = 1
    kind = 'VictusFanControl.P14.4.CiArtifactAttestation'
    version = [string]$release.version
    targetProfileId = [string]$release.targetProfileId
    sourceHead = $SourceHead.ToLowerInvariant()
    github = [ordered]@{
        repository = $Repository
        ref = $Ref
        runId = $RunId
        runNumber = $RunNumber
        artifactName = $ArtifactName
    }
    package = [ordered]@{
        fileName = $zipName
        sha256 = $zipHash
        sha256FileName = [IO.Path]::GetFileName($shaPath)
        sha256FileSha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $shaPath).Hash.ToLowerInvariant()
    }
    pinnedExternalInputs = [ordered]@{
        pawnIoModulesVersion = [string]$release.productization.pawnIoModulesVersion
        pawnIoModulesArchiveSha256 = ([string]$release.productization.pawnIoModulesArchiveSha256).ToLowerInvariant()
    }
    safetyBoundary = [ordered]@{
        softwareOnly = $true
        hardwareExecution = $false
        controlEnabledByDefault = $false
        automaticPolicyEnabled = $false
        manualExecutionAuthorized = $false
        automaticExecutionAuthorized = $false
    }
}

$utf8 = New-Object System.Text.UTF8Encoding($false)
[IO.File]::WriteAllText($attestationPath, (($attestation | ConvertTo-Json -Depth 8) + [Environment]::NewLine), $utf8)
Write-Host "P14.4 external artifact attestation staged: $attestationPath" -ForegroundColor Green
Write-Host "P14.4 RC payload SHA-256: $zipHash"
