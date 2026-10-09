param(
    [Parameter(Mandatory = $true)]
    [string]$OutputRoot,
    [switch]$ProductRelease
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$outputFull = [IO.Path]::GetFullPath($OutputRoot)

if (Test-Path -LiteralPath $outputFull) {
    $existing = @(Get-ChildItem -LiteralPath $outputFull -Force -ErrorAction Stop)
    if ($existing.Count -ne 0) {
        throw "P14.2 publish output must be absent or empty: $outputFull"
    }
}
else {
    New-Item -ItemType Directory -Force -Path $outputFull | Out-Null
}

$appOut = Join-Path $outputFull 'app'
$watchdogOut = Join-Path $outputFull 'watchdog'
New-Item -ItemType Directory -Force -Path $appOut, $watchdogOut | Out-Null

function Publish-P14Project {
    param([string]$Project,[string]$Destination)

    $args = @(
        'publish', $Project,
        '-c', 'Release',
        '-r', 'win-x64',
        '--self-contained', 'false',
        '-o', $Destination,
        '--nologo',
        '-p:UseAppHost=true',
        '-p:PublishSingleFile=false',
        '-p:PublishReadyToRun=false',
        '-p:DebugType=None',
        '-p:DebugSymbols=false',
        '-p:ContinuousIntegrationBuild=true'
    )

    if($ProductRelease){$args += '-p:VictusProductRelease=true'}
    & dotnet @args
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed for '$Project' with exit code $LASTEXITCODE."
    }
}

Publish-P14Project -Project (Join-Path $repoRoot 'src\VictusFanControl.App\VictusFanControl.App.csproj') -Destination $appOut
Publish-P14Project -Project (Join-Path $repoRoot 'src\VictusFanControl.Watchdog\VictusFanControl.Watchdog.csproj') -Destination $watchdogOut

Publish-P14Project -Project (Join-Path $repoRoot 'src\VictusFanControl.PerformanceGuardian\VictusFanControl.PerformanceGuardian.csproj') -Destination (Join-Path $appOut 'performance-guardian')

Write-Host "P14.2 win-x64 publish layout built: $outputFull" -ForegroundColor Green
