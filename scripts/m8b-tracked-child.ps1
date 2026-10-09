# HP 8C40 M8B qualification-only process lifetime/ExitCode helper.
# Pure host process management: no fan/EC/WMI/journal/service operations.
# Uses a directly owned System.Diagnostics.Process rather than the PowerShell
# Start-Process -PassThru wrapper, whose ExitCode was unavailable in a real run.

function ConvertTo-M8BProcessArgument {
    param([Parameter(Mandatory=$true)][AllowEmptyString()][string]$Value)

    # These versioned argument values are paths/options without literal quotes.
    # Refuse inputs we cannot safely encode with simple Windows quoting.
    if($Value.Contains([char]34) -or
       $Value.Contains([char]13) -or
       $Value.Contains([char]10) -or
       $Value.EndsWith('\')){
        throw 'M8B child argument contains unsupported quote/newline/trailing backslash.'
    }

    return [string]::Concat('"',$Value,'"')
}

function Start-M8BTrackedChild {
    param(
        [Parameter(Mandatory=$true)][string]$Executable,
        [Parameter(Mandatory=$true)][string[]]$Arguments,
        [Parameter(Mandatory=$true)][string]$WorkingDirectory
    )

    if(-not (Test-Path -LiteralPath $Executable -PathType Leaf)){
        throw "M8B child executable does not exist: $Executable"
    }

    if(-not (Test-Path -LiteralPath $WorkingDirectory -PathType Container)){
        throw "M8B child working directory does not exist: $WorkingDirectory"
    }

    $quoted=@(
        foreach($argument in $Arguments){
            ConvertTo-M8BProcessArgument -Value $argument
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
            throw 'M8B native tracked child process could not be started.'
        }

        return $process
    }
    catch {
        $process.Dispose()
        throw
    }
}

function Wait-M8BTrackedChildExitCode {
    param(
        [Parameter(Mandatory=$true)][System.Diagnostics.Process]$Process,
        [ValidateRange(1,300)][int]$Seconds
    )

    if(-not $Process.WaitForExit($Seconds*1000)){
        throw "M8B tracked controller did not exit within $Seconds s after READY."
    }

    # The process was created through Process.Start() on this same instance.
    # Wait for native exit + pending output completion before reading ExitCode.
    $Process.WaitForExit()
    $Process.Refresh()

    try {
        $exitCode=$Process.ExitCode
    }
    catch {
        throw ("M8B tracked child ExitCode could not be read after exit: {0}" -f
            $_.Exception.Message)
    }

    if($null -eq $exitCode -or
       [string]::IsNullOrWhiteSpace([string]$exitCode)){
        throw 'M8B tracked child ExitCode remains unavailable after native WaitForExit.'
    }

    return [int]$exitCode
}
