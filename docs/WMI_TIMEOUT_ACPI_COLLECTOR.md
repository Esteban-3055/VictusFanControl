# Manual timeout: collector with ACPI evidence

This diagnostic package extends `scripts/Collect-Victus-WmiTimeout.ps1` and requires the adjacent `Export-Victus-AcpiTables.ps1`. The CMD launcher starts a 15-minute capture; right-click and run as administrator. It displays each stage, creates a desktop directory plus ZIP/SHA-256, and finishes early with Q. Keep the console open; after a failure allow approximately one minute for recovery. Open the normal app separately once the collector prints `Observando`.

## Included evidence

- Live app/watchdog logs (16 most recent per source/stage, bounded tails of 8 MiB); journal changes; processes at two-second intervals with UTC and a collector-relative monotonic clock.
- Existing System, Application, WMI Activity, Kernel Power Thermal and Kernel PnP channels: XML plus rendered message/PID/record ID; up to 10,000 text events per channel/stage. EVTX is exported for the selected period without that text row limit. Missing/disabled channels remain unchanged.
- Static firmware ACPI tables via Windows EnumSystemFirmwareTables/GetSystemFirmwareTable. Headers, declared sizes, checksums and multiplicity are recorded. MSDM is excluded because it contains an OEM Windows key. The API returns only the first instance for each signature; additional SSDTs are explicitly counted as unavailable from that API. A read-only scan of HKLM\HARDWARE\ACPI\DSDT and SSDT supplements boot-time registry snapshots where Windows exposes them, with source paths, checksums, SHA-256 and duplicate reporting. Registry coverage is not guaranteed; scanning is bounded to 512 keys/depth 8 and a separate 32 MiB budget. No physical-memory reader or kernel driver is installed. Limits: 4 MiB per table and 32 MiB total.
- Temporary ETW sessions for the available Microsoft-Windows-Kernel-Acpi and Microsoft-Windows-WMI-Activity providers. Each has a unique name, 128 MiB circular output, bounded buffers and a finally cleanup. No existing sessions are stopped or event channels enabled. `-SkipAcpiTrace` disables this extra measurement; tracing can affect timing and provider coverage varies by Windows build. If the console is forcibly closed, the generated `etw/STOP-IF-CONSOLE-WAS-CLOSED.cmd` contains only this capture's session names.
- ACPI PnP device/driver properties, service status, driver versions, power configuration, Git identity and binary/module hashes (including core and App DLLs). No generic process command lines or memory dumps.

## Optional iASL

`-IaslPath C:\Tools\ACPICA\iasl.exe` disassembles only exported DSDT/SSDT files using `-d`. No methods are evaluated, no tables loaded or patched. The tool is optional and not downloaded automatically. Missing duplicate SSDTs may prevent complete disassembly. Existing ACPI tools may provide a fuller static dump, but the command depends on that specific tool/version.

## Opt-in application chronology

Run `scripts/Start-Victus-WmiInvestigation.ps1` from an elevated PowerShell after
returning to Firmware and closing the app. It builds Release, launches the normal
GUI with `VFC_EC_WMI_DIAGNOSTICS=1` inherited only by that child, then runs the
collector. Wait for `Observando` before using Manual. It never applies a target,
changes firmware, reinstalls or restarts the watchdog. Existing Manual safeguards
and their deadlines still apply. The existing watchdog binary remains unchanged;
its normal logs and identity are captured, but its EC reads do not gain detailed
chronology in this application-only diagnostic session.

The app creates `ec-wmi-<pid>-<utc>.log` in the existing local application logs
folder. The collector includes it automatically. Records contain UTC, Windows
uptime milliseconds, QPC/frequency, process identity, managed thread and operation
IDs. QPC orders cross-thread events even if disk publication is delayed.

Recorded observations use existing reads only:

- HP WMI connection/discovery, preparation, parameter lookup, native invoke and
  return-code/response-size phases; command/type identifies read versus restore.
- Direct EC read start, register/value, completion or original handshake failure,
  plus named-mutex acquisition/release chronology. No extra status polling.
- Actual setpoint/MaxFan/FanSwitch snapshots and unexpected-guard reconfirmation.
- ACK baseline, target, expected direction, each raw sample, freshness rejection,
  per-fan decision, consecutive count and active-time deadline.

Disk writes occur on a background thread, never inside an EC transaction. The
queue is capped at 4096 records and drops are reported explicitly. The file is
capped at 8 MiB and marks a reached limit as incomplete. Failed/full disk disables
only diagnostic recording. The thread flushes every 200 ms; an abrupt process
termination can lose the last buffered records. Instrumentation/ETW can alter
scheduling, so overlap is evidence to investigate, not proof of interference.
The flag is off by default and is not persisted to machine/user settings.

## Limits of the investigation

Without the optional application chronology, this file cannot reconstruct `MaxFan`, `FanSwitch`, setpoints or individual WMI method stage timestamps absent from the application logs. Those require the optional application instrumentation described above. ETW/table data supplements it and does not establish a physical cause by itself. Event metadata may contain machine/user/path information and unrelated provider operations. The original application behavior, timeouts, command confirmation and restore protections are unchanged.

## Validation

PowerShell 5.1 and 7 fixture checks cover shared-file copying, bounded reads, child timeout, packaging/checksum and ACPI header validation. Windows CI also runs a zero-duration real OS capture, checks actual firmware table export (DSDT when enumerated), rendered System messages, EVTX, ZIP and cleanup of every attempted ETW session. This is a Windows VM software check, not HP EC qualification.

## References

- https://learn.microsoft.com/en-us/windows/win32/api/sysinfoapi/nf-sysinfoapi-enumsystemfirmwaretables
- https://learn.microsoft.com/en-us/windows/win32/api/sysinfoapi/nf-sysinfoapi-getsystemfirmwaretable
- https://learn.microsoft.com/en-us/windows-server/administration/windows-commands/logman-create-trace
- https://www.intel.com/content/www/us/en/developer/topic-technology/open/acpica/download.html

- https://www.microsoft.com/en-us/msrc/blog/2022/03/exploring-a-new-class-of-kernel-exploit-primitive
