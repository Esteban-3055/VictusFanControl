# HP 8C40 P15B qualification-only process lifetime/ExitCode helper.
# Pure host process management: no fan/EC/WMI/journal/service operations.
# Uses a directly owned System.Diagnostics.Process because earlier physical
# gates proved Start-Process -PassThru can lose ExitCode observability.

function ConvertTo-P15BProcessArgument {
    param([Parameter(Mandatory=$true)][AllowEmptyString()][string]$Value)

    if($Value.Contains([char]34) -or
       $Value.Contains([char]13) -or
       $Value.Contains([char]10) -or
       $Value.EndsWith('\')){
        throw 'P15B child argument contains unsupported quote/newline/trailing backslash.'
    }

    return [string]::Concat('"',$Value,'"')
}

function Start-P15BTrackedChild {
    param(
        [Parameter(Mandatory=$true)][string]$Executable,
        [Parameter(Mandatory=$true)][string[]]$Arguments,
        [Parameter(Mandatory=$true)][string]$WorkingDirectory
    )

    if(-not (Test-Path -LiteralPath $Executable -PathType Leaf)){
        throw "P15B child executable does not exist: $Executable"
    }
    if(-not (Test-Path -LiteralPath $WorkingDirectory -PathType Container)){
        throw "P15B child working directory does not exist: $WorkingDirectory"
    }

    $quoted=@(
        foreach($argument in $Arguments){
            ConvertTo-P15BProcessArgument -Value $argument
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
            throw 'P15B native tracked child process could not be started.'
        }

        return $process
    }
    catch {
        $process.Dispose()
        throw
    }
}

function Wait-P15BTrackedChildExitCode {
    param(
        [Parameter(Mandatory=$true)][System.Diagnostics.Process]$Process,
        [ValidateRange(1,300)][int]$Seconds
    )

    if(-not $Process.WaitForExit($Seconds*1000)){
        throw "P15B tracked controller did not exit within $Seconds s after READY."
    }

    $Process.WaitForExit()
    $Process.Refresh()

    try {
        $exitCode=$Process.ExitCode
    }
    catch {
        throw ("P15B tracked child ExitCode could not be read after exit: {0}" -f
            $_.Exception.Message)
    }

    if($null -eq $exitCode -or
       [string]::IsNullOrWhiteSpace([string]$exitCode)){
        throw 'P15B tracked child ExitCode remains unavailable after native WaitForExit.'
    }

    return [int]$exitCode
}
