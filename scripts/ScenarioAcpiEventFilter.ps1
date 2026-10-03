# Shared read-only ACPI event filtering; no execution on import.
function New-ScenarioAEventQuery([long]$InitialRecordId) {
    if($InitialRecordId -lt 0){throw 'Cursor de eventos invalido.'}
    # Record IDs avoid localized date conversion in FilterHashtable. A second
    # independent check below uses the original XML timestamp, always UTC.
    return "*[System[Provider[@Name='ACPI'] and EventID=13 and EventRecordID > $InitialRecordId]]"
}
function Select-ScenarioANewEvents($Events,[long]$InitialRecordId,[DateTimeOffset]$StartedUtc) {
    foreach($event in @($Events)){
        [xml]$xml=$event.ToXml()
        $system=$xml.Event.System
        if(-not $system -or -not $system.TimeCreated.SystemTime -or -not $system.EventRecordID){throw 'Evento sin identidad o tiempo XML verificable.'}
        $record=[long]$system.EventRecordID
        $utc=[DateTimeOffset]::Parse([string]$system.TimeCreated.SystemTime,[Globalization.CultureInfo]::InvariantCulture)
        if($system.Provider.Name -eq 'ACPI' -and [int]$system.EventID -eq 13 -and $record -gt $InitialRecordId -and $utc -ge $StartedUtc){$event}
    }
}

