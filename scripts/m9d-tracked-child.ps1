# HP 8C40 M9D qualification-only process lifetime/ExitCode helper.
# Pure host process management: no fan/EC/WMI/journal/service operations.
$ErrorActionPreference='Stop'

function ConvertTo-M9DProcessArgument {
    param([Parameter(Mandatory=$true)][AllowEmptyString()][string]$Value)

    if($Value.Contains([char]34) -or
       $Value.Contains([char]13) -or
       $Value.Contains([char]10) -or
       $Value.EndsWith('\')){
        throw 'M9D child argument contains unsupported quote/newline/trailing backslash.'
    }

    return [string]::Concat('"',$Value,'"')
}

function Start-M9DTrackedChild {
    param(
        [Parameter(Mandatory=$true)][string]$Executable,
        [Parameter(Mandatory=$true)][string[]]$Arguments,
        [Parameter(Mandatory=$true)][string]$WorkingDirectory
    )

    if(-not (Test-Path -LiteralPath $Executable -PathType Leaf)){
        throw "M9D child executable does not exist: $Executable"
    }
    if(-not (Test-Path -LiteralPath $WorkingDirectory -PathType Container)){
        throw "M9D child working directory does not exist: $WorkingDirectory"
    }

    $quoted=@(
        foreach($argument in $Arguments){
            ConvertTo-M9DProcessArgument -Value $argument
        }
    )

    $startInfo=New-Object System.Diagnostics.ProcessStartInfo
    $startInfo.FileName=$Executable
    $startInfo.Arguments=[string]::Join(' ',$quoted)
    $startInfo.WorkingDirectory=$WorkingDirectory
    $startInfo.UseShellExecute=$false
    $startInfo.CreateNoWindow=$false
    $startInfo.RedirectStandardOutput=$false
    $startInfo.RedirectStandardError=$false

    $process=New-Object System.Diagnostics.Process
    $process.StartInfo=$startInfo

    try {
        if(-not $process.Start()){
            throw 'M9D native tracked App process could not be started.'
        }
        return $process
    }
    catch {
        $process.Dispose()
        throw
    }
}

function Wait-M9DTrackedChildExitCode {
    param(
        [Parameter(Mandatory=$true)][System.Diagnostics.Process]$Process,
        [ValidateRange(1,900)][int]$Seconds
    )

    if(-not $Process.WaitForExit($Seconds*1000)){
        throw "M9D tracked App did not exit within $Seconds s."
    }

    $Process.WaitForExit()
    $Process.Refresh()

    try {$exitCode=$Process.ExitCode}
    catch {throw ("M9D tracked child ExitCode unavailable: {0}" -f $_.Exception.Message)}

    if($null -eq $exitCode -or [string]::IsNullOrWhiteSpace([string]$exitCode)){
        throw 'M9D tracked child ExitCode remains unavailable after native WaitForExit.'
    }

    return [int]$exitCode
}
