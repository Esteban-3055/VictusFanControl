# Scenario C: protocol anomalies, 2026-10-03

Target: HP Victus 15-fa1013la, board 8C40, BIOS F.18. This is not the historical 88F8 target.

Source ZIP: VFC-WMI-Diagnostico_2026-10-03_183347_decc6e.zip.
SHA-256: bd08521714a943e014e43f801564cda69e45d27c68eb1f0a7cc21325a5233137.
All 217 manifest entries match their byte counts and SHA-256 hashes.
The separately attached ACPI id 15.evtx contains the same five new warnings.

## Observed results

CLI ready: 21:34:10 UTC / 18:34:10 America/Santiago.
CLI finished: 21:44:46 UTC / 18:44:46 local; about 10 min 36 s.
No HP fan commands were emitted; M2/M3/M4 remained stopped in sampled isolation checks.

- 122 complete residual EC batches; start spacing 5.025-6.094 s, median 5.082 s.
- 771 EC register attempts: 745 succeeded, 26 failed with output-buffer-full timeouts.
- The existing bounded transaction retries allowed all 122 batches to finish despite these failures.
- Batch duration median 1.507 ms, maximum 796.418 ms.
- 314 native WMI RPM queries, all completed, maximum native duration 549.373 ms.
- Five new ACPI 15 warnings; zero new ACPI 13 events.
- Three disconnect/reconnect cycles; the first two ACPI warnings preceded the first power change.
- Exit code 0 means orderly exit, not an absence of protocol faults.

| New ACPI 15, local time | Record ID | EC observation |
|---|---:|---|
| 18:34:15.469817 | 4850 | Near batch 2 start (about 0.07 ms later) |
| 18:34:21.231174 | 4851 | About 8.12 ms before batch 3 start; that batch later times out on F4 |
| 18:34:37.608158 | 4854 | About 4.73 ms before batch 6 start |
| 18:34:58.503161 | 4855 | About 3.67 ms before batch 10 start |
| 18:35:31.284461 | 4856 | During batch 16, which lasts 523.383 ms |

Windows' rendered ACPI 15 message reports unsolicited EC data and a possible unsynchronized BIOS access. The message does not identify the responsible software. Correlation with C is evidence for investigating shared EC access, not proof that a particular byte was consumed by a particular participant or that this reproduces the original ACPI 13 failure.

The values FF/FF/00/00 were observed in every complete batch. Successful protocol completion is insufficient to establish that every byte came from the intended transaction.

## Collector limitations found

Console progress was configured at 30 s, independently of the 5 s EC schedule; child output was redirected. Missing console lines did not mean missing EC samples.
Parent isolation/process observations have a roughly six-minute gap between 18:37:52 and 18:44:04. The child EC and telemetry files remain continuous. The cause of that parent gap is not established, and isolation/event detection must not be claimed continuous throughout it.

Detector version 2 observed only ACPI 13 and ignored ACPI 15. The valid-isolation summary describes the process boundary, not hardware health. Batches marked complete concealed recovered transaction failures.

## Corrective implementation

B/C detector version 3 watches new ACPI 13 and 15, retaining the record cursor and independent XML UTC timestamp checks. ACPI 13 compatibility fields remain separate from the full event list. A retains its existing default ACPI 13 filter.

C stops on the first failed EC protocol transaction, without a transaction retry. Production retains its five-attempt retry policy and C retains the bounded coherent setpoint sampling on successful transactions. Failed CSV rows and the offline trace remain available.

C console progress targets five-second reporting and shows actual EC CSV count, timestamp, status, duration, setpoints and guards. It is a sampled display, not a hard real-time guarantee. ObservationHealthy is distinct from ValidIsolation.

Do not advance to scenario D/Manual on this evidence. Investigate a firmware/ACPI-mediated replacement for residual ownership/guard reads and validate equivalent restoration evidence before enabling that path.
