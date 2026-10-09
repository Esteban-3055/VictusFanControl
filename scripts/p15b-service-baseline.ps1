function Resolve-P15BServiceBaselineMode {
    param(
        [Parameter(Mandatory=$true)][string]$State,
        [Parameter(Mandatory=$true)][string]$StartMode,
        [Parameter(Mandatory=$true)][string]$StartName,
        [Parameter(Mandatory=$true)][int]$ProcessId,
        [Parameter(Mandatory=$true)][bool]$JournalPresent,
        [Parameter(Mandatory=$true)][bool]$ReadyVerified
    )

    if($StartMode -cne 'Manual' -or $StartName -notmatch 'LocalSystem|Local System'){
        throw "P15B service baseline requires Manual/LocalSystem; observed $StartMode/$StartName."
    }

    if($JournalPresent){
        throw 'P15B service baseline refuses a retained watchdog journal.'
    }

    if($State -ceq 'Stopped' -and $ProcessId -eq 0){
        return 'Stopped'
    }

    if($State -ceq 'Running' -and $ProcessId -gt 0 -and $ReadyVerified){
        return 'Running'
    }

    if($State -ceq 'Running' -and $ProcessId -gt 0 -and -not $ReadyVerified){
        throw 'P15B inherited Running watchdog baseline requires exact Ready verification.'
    }

    throw "P15B unsupported watchdog baseline: state=$State startMode=$StartMode pid=$ProcessId ready=$ReadyVerified."
}
