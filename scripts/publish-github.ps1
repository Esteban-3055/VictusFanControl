param(
    [ValidateSet('private','public')]
    [string]$Visibility = 'private',
    [string]$RepositoryName = 'VictusFanControl'
)

$ErrorActionPreference = 'Stop'

if (-not (Get-Command git -ErrorAction SilentlyContinue)) {
    throw 'git was not found.'
}
if (-not (Get-Command gh -ErrorAction SilentlyContinue)) {
    throw 'GitHub CLI (gh) was not found. Install it, then run: gh auth login'
}

gh auth status | Out-Null
if ($LASTEXITCODE -ne 0) {
    throw 'GitHub CLI is not authenticated. Run: gh auth login'
}

if (-not (Test-Path .git)) {
    git init -b main
}

git add .

git rev-parse --verify HEAD 2>$null | Out-Null
$hasHead = ($LASTEXITCODE -eq 0)

if (-not $hasHead) {
    git commit -m 'Initial telemetry-only scaffold'
}

$visibilityFlag = if ($Visibility -eq 'public') { '--public' } else { '--private' }

gh repo create $RepositoryName $visibilityFlag --source . --remote origin --push

Write-Host "Repository created and pushed: $RepositoryName" -ForegroundColor Green
