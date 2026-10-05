param([ValidateSet('Verify','SelfTest','Soak','Open','AutomaticReview')][string]$Mode = 'Open')
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$manifest = Get-Content -LiteralPath (Join-Path $root 'PRODUCT-GUI-MANIFEST.json') -Raw | ConvertFrom-Json
if ($manifest.schemaVersion -ne 1 -or $manifest.kind -ne 'VictusFanControl.ProductGuiReview') { throw 'Invalid product GUI manifest.' }
foreach ($entry in $manifest.files) {
    $relative = [string]$entry.path
    if ([IO.Path]::IsPathRooted($relative) -or $relative -match '(^|[/\\])\.\.([/\\]|$)') { throw 'Invalid manifest path.' }
    $file = Join-Path $root $relative
    if (-not (Test-Path -LiteralPath $file -PathType Leaf)) { throw "Missing package file: $relative" }
    if ((Get-FileHash -Algorithm SHA256 -LiteralPath $file).Hash.ToLowerInvariant() -ne $entry.sha256 -or (Get-Item -LiteralPath $file).Length -ne $entry.size) { throw "Package integrity failed: $relative" }
}
Write-Host "Verified GUI build $($manifest.sourceHead). Physical validation remains pending."
if ($Mode -eq 'Verify') { return }
if ($Mode -eq 'AutomaticReview' -and $manifest.productAutomaticReview -ne 'explicit-only-300s-30-to-50') { throw 'This package does not authorize the supervised Automatic review entry.' }
$app = Join-Path $root 'VictusFanControl-0.4.0-rc.1-win-x64/app'
Push-Location $app
try {
    $start = New-Object System.Diagnostics.ProcessStartInfo
    $start.FileName = Join-Path $app 'VictusFanControl.App.exe'
    $start.UseShellExecute = $false
    $start.WorkingDirectory = $app
    $start.Arguments = if ($Mode -eq 'SelfTest') { '--product-gui-self-test' } elseif ($Mode -eq 'Soak') { '--product-gui-soak-self-test' } else { '--modules-dir "' + (Join-Path $app 'modules') + '"' }
    if ($Mode -eq 'AutomaticReview') {
        $start.Arguments += ' --product-automatic-review'
        Write-Host 'Supervised Automatic review: exact HP 8C40/F.18, levels 30-50, maximum 5 minutes per activation. Starts in Firmware; select Automatic explicitly. CPU/GPU limits must be released.'
    }
    $process = [System.Diagnostics.Process]::Start($start)
    try {
        $process.WaitForExit()
        if ($process.ExitCode -ne 0) { throw "GUI exited with code $($process.ExitCode); retain logs and recovery journals." }
    } finally { $process.Dispose() }
} finally { Pop-Location }
