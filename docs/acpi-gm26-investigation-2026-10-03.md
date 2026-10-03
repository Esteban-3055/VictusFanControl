# ACPI coordination investigation: HP 8C40 / BIOS F.18

## Finding and scope

The existing AML contains a narrow path for reading the FFFS bit through HP WMI and an EmbeddedControl field. It does not expose an explicit getter for the complete residual state needed by the current control/readback logic. This is static evidence, not physical qualification. Scenario C direct EC access remains suspended; this probe does not resume it or qualify Manual/Automatic operation.

Reviewed input: 1 DSDT and 16 SSDT DSL files from `Victus_ACPI_8C40_F18_20261002-203841.zip`. DSDT SHA-256: `f4fcd9a76b150c82771b279a0b3620f966ed4bce84aee17cdcf6acff0307a929`.

| Value/path | AML evidence | Consequence |
|---|---|---|
| SRP1/SRP2, EC 34h/35h | Field declarations 134608/134609; assignments in GM2E 128918/128919 | No explicit named getter found in these 17 tables. SMM behavior remains opaque. |
| FFFS, EC ECh bit 2 | Field 134802; GM26 128699–128714 | Getter returns only one bit, not the whole guard byte. |
| SFAN, EC F4h | Field 134812; FSSP assignments 136671/136675 | No explicit getter found. The SSDT13 SFAN buffer field is a different namespace/object. |
| RPIO / WPIO / ECMD | 135644–135681 | RPIO writes EI01/EI02 before reading EI03. ECMD writes and polls without an explicit iteration bound. These are not arbitrary read-only register getters. |
| CMSR | 125151 | CMOS index/data interface, not the EC ERAM field. |
| WSMI | 125314–125361 | Writes shared SMI request fields, triggers OSMI, waits up to 50 AML iterations with Sleep(5). SMM execution itself is opaque; this loop is not a hard call deadline. |

## Verified GM26 contract

`WMAA` (125488) is Serialized and acquires MUTZ around HWMC. HWMC (123999) parses the SECU signature, command, type and input size. Command 20008h/type 26h dispatches GM26 (124830). GM26 returns `{0,4,Buffer(4)}`; if ECOK equals one it copies FFFS to output byte zero. The other three bytes stay zero.

HWMC accepts output selector 2 as capacity 4. GM26 does not inspect the input payload or require a nonzero input size; zero-length input avoids copying input into WBUF. The common HWMC epilogue still clears shared WBUF. Thus this is a getter without fan/EC field writes in the inspected route, not a claim that the entire invocation has no shared-memory writes. GM26 itself has no WSMI call.

Expected request: instance `ACPI\PNP0C14\0_0`, namespace `root\wmi`, input class `hpqBDataIn`, signature bytes `53 45 43 55`, Command `0x20008`, CommandType `0x26`, Size `0`, empty hpqBData, method `hpqBIOSInt4`. Expected response: rwReturnCode 0 and exactly four bytes, `00-00-00-00` or `01-00-00-00`. Provider acceptance of empty input remains to be tested; rejection stops the test without trying alternative commands/payloads.

ECOK is initialized to zero and `_REG` (134828) sets it to one on EmbeddedControl region connection. This implementation does not reset it on disconnect. Therefore it is not a current availability health signal. GM26 returns zero without exposing whether the conditional EC read ran; a zero result cannot by itself prove a successful physical read or cleared guard.

The EC field uses ByteAcc/NoLock/Preserve, and there is no `_GLK` declaration in the inspected tables. This does not establish that Acpi.sys lacks EC transaction serialization. A userspace mutex or an invented AML mutex cannot coordinate independent port access with Acpi.sys. Evaluating a firmware method is the investigated alternative.

## Bounded physical probe

`Test-Victus-AcpiGm26.ps1` makes at most three GM26 calls, with at least five seconds after each completed call. It does not load PawnIO, query RPM, read Intel MSR/NVML, or call fan setters. Exact board/model/SKU/BIOS checks and isolation checks run in a separate Windows PowerShell child before HP provider discovery and before each call. Known polling tools, active VFC services and retained leases block execution; these snapshots cannot detect every third-party driver or prevent another process from starting between checks.

The parent displays progress every five seconds, records the System event cursor, observes new ACPI 13/15 using original XML UTC, and stops the child on observation failure, new warnings or its 45-second deadline. It observes two seconds after child completion. Killing the child does not guarantee cancellation of an already running WMI/firmware call. An error or malformed response ends the child on the first occurrence, without retries. Provider calls are logged before invocation and after response; evidence is retained even when validation fails.

Before running: return VFC to Firmware, close it and other monitoring tools, ensure its watchdog/services are stopped through their existing lifecycle, keep the current BIOS options and AC connection unchanged. Do not remove retained lease files to bypass isolation. This probe does not change any BIOS option or power setting.

From an elevated Windows PowerShell in the updated repository:

```powershell
.\scripts\Test-Victus-AcpiGm26.ps1
```

Send the generated `diagnostics\ACPI-GM26_*.zip`. It contains summary, identity, raw call phases, valid sample CSV and any child error. No new warning in this brief window means only that these three requests completed without the observed events; it does not establish long-term stability, complete EC coordination, or sufficient readback coverage for the production controller.

CI checks PowerShell syntax and accepted/rejected response fixtures in PowerShell 7 and 5.1 without hardware access. This environment cannot run the HP physical probe.

Primary background: [Windows ACPI driver](https://learn.microsoft.com/windows-hardware/drivers/kernel/acpi-driver), [ACPI EC interface specification](https://uefi.org/specs/ACPI/6.5/12_Embedded_Controller_Interface_Specification.html). Device-specific conclusions above come from the supplied AML, not generic documentation.
