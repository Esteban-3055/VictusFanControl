param(
    [Parameter(Mandatory = $true)]
    [string]$Cli,

    [Parameter(Mandatory = $true)]
    [string]$RepoRoot,

    [Parameter(Mandatory = $true)]
    [string]$LogPath,

    [ValidateRange(30, 600)]
    [int]$DelaySeconds = 120
)

$ErrorActionPreference = 'Stop'

function Log-Line {
    param([string]$Text)
    Add-Content -Path $LogPath -Value ("{0:O}  {1}" -f (Get-Date), $Text)
}

try {
    Start-Sleep -Seconds $DelaySeconds
    Set-Location -LiteralPath $RepoRoot

    $probe = (& dotnet $Cli --probe-88f8-ec-state 2>&1 | Out-String)
    Log-Line ("Probe after delay: " + ($probe.Trim() -replace "[\r\n]+", ' | '))

    $line = ($probe -split "[\r\n]+" |
        Where-Object { $_ -match '^level CPU=' } |
        Select-Object -Last 1)

    $match = [regex]::Match($line, '^level CPU=(\d+) GPU=(\d+)')
    if (-not $match.Success) {
        Log-Line 'FAILSAFE REFUSED: could not parse EC setpoints.'
        exit 2
    }

    $cpu = [int]$match.Groups[1].Value
    $gpu = [int]$match.Groups[2].Value

    if ($cpu -eq 255 -and $gpu -eq 255) {
        Log-Line 'FAILSAFE NO-OP: EC already FF/FF.'
        exit 0
    }

    if ($cpu -ne 30 -or $gpu -ne 30) {
        Log-Line ("FAILSAFE REFUSED: unexpected external setpoint {0}/{1}; ownership is ambiguous." -f $cpu, $gpu)
        exit 3
    }

    Log-Line 'FAILSAFE TAKEOVER: owned test setpoint 30/30 is still present; invoking validated HP-auto restore.'
    $restore = (& dotnet $Cli --restore-hp-auto 2>&1 | Out-String)
    Log-Line ("Restore output: " + ($restore.Trim() -replace "[\r\n]+", ' | '))
    exit $LASTEXITCODE
}
catch {
    Log-Line ("FAILSAFE ERROR: " + $_.Exception.ToString())
    exit 4
}
