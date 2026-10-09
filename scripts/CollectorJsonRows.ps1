# Explicitly enumerate records: Windows PowerShell 5.1 sends a parsed JSON
# array as one pipeline object, while PowerShell 7 enumerates its elements.
function Convert-CollectorJsonRows([string]$JsonText) {
    if (-not $JsonText.TrimStart().StartsWith('[')) { throw 'Collector JSON must contain an array of records.' }
    $parserArguments = @{ InputObject = $JsonText; ErrorAction = 'Stop' }
    if ((Get-Command ConvertFrom-Json).Parameters.ContainsKey('NoEnumerate')) { $parserArguments.NoEnumerate = $true }
    $parsed = ConvertFrom-Json @parserArguments
    foreach ($row in $parsed) {
        if ($null -eq $row -or $row -isnot [pscustomobject]) { throw 'Collector JSON contains a non-record entry.' }
        $row
    }
}

function Get-CollectorFirmwareCommand([object[]]$Rows) {
    foreach ($row in $Rows) {
        if ($row -isnot [pscustomobject] -or $row.Destination -isnot [string] -or $row.Status -isnot [string]) { throw 'Malformed collector command record.' }
    }
    $matches = @($Rows | Where-Object { $_.Destination -match 'acpi[\\/]capture.txt$' })
    if ($matches.Count -ne 1) { throw ('Expected one static firmware command; found ' + $matches.Count) }
    if ($matches[0].Status -ne 'ok') { throw ('Static firmware command failed: ' + $matches[0].Status) }
    $matches[0]
}
