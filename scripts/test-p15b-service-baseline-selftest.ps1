$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'p15b-service-baseline.ps1')

function Assert-Equal([string]$Expected,[string]$Actual,[string]$Message){
    if($Expected -cne $Actual){throw "$Message expected=$Expected actual=$Actual"}
}

function Assert-Throws([scriptblock]$Action,[string]$Message){
    $threw=$false
    try{& $Action}catch{$threw=$true}
    if(-not $threw){throw $Message}
}

Assert-Equal 'Stopped' (Resolve-P15BServiceBaselineMode -State 'Stopped' -StartMode 'Manual' -StartName 'LocalSystem' -ProcessId 0 -JournalPresent $false -ReadyVerified $false) 'Stopped baseline'
Assert-Equal 'Running' (Resolve-P15BServiceBaselineMode -State 'Running' -StartMode 'Manual' -StartName 'LocalSystem' -ProcessId 7980 -JournalPresent $false -ReadyVerified $true) 'Inherited Running baseline'
Assert-Throws {Resolve-P15BServiceBaselineMode -State 'Running' -StartMode 'Manual' -StartName 'LocalSystem' -ProcessId 7980 -JournalPresent $false -ReadyVerified $false | Out-Null} 'Running without Ready must fail.'
Assert-Throws {Resolve-P15BServiceBaselineMode -State 'Running' -StartMode 'Manual' -StartName 'LocalSystem' -ProcessId 7980 -JournalPresent $true -ReadyVerified $true | Out-Null} 'Running with retained journal must fail.'
Assert-Throws {Resolve-P15BServiceBaselineMode -State 'Stopped' -StartMode 'Manual' -StartName 'LocalSystem' -ProcessId 0 -JournalPresent $true -ReadyVerified $false | Out-Null} 'Stopped with retained journal must fail.'
Assert-Throws {Resolve-P15BServiceBaselineMode -State 'Stopped' -StartMode 'Automatic' -StartName 'LocalSystem' -ProcessId 0 -JournalPresent $false -ReadyVerified $false | Out-Null} 'Automatic service baseline must fail.'
Assert-Throws {Resolve-P15BServiceBaselineMode -State 'Stopped' -StartMode 'Manual' -StartName 'LocalSystem' -ProcessId 1234 -JournalPresent $false -ReadyVerified $false | Out-Null} 'Stopped service with nonzero PID must fail.'
Assert-Throws {Resolve-P15BServiceBaselineMode -State 'Paused' -StartMode 'Manual' -StartName 'LocalSystem' -ProcessId 1234 -JournalPresent $false -ReadyVerified $true | Out-Null} 'Unsupported service state must fail.'

Write-Host 'HP 8C40 P15B service-baseline resolver self-test: PASS' -ForegroundColor Green
