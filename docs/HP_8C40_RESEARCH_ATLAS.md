# HP Victus 15-fa1013la / 8C40 research atlas

**Consolidated:** 2026-10-12 · [Documentación técnica completa en español](HP_8C40_RESEARCH_ATLAS.es.md)

This is an **evidence-oriented index** of the reverse engineering, physical qualifications and unresolved hypotheses conducted on one HP Victus 15-fa1013la. It is **not HP documentation**, a universal Victus compatibility claim, or a license to write to embedded-controller (EC) registers.

## Exact machine, not a laptop-family fingerprint

| Field | Investigated unit |
|---|---|
| System | Victus by HP Gaming Laptop 15-fa1xxx (15-fa1013la) |
| SKU | 9D0R1LA / documented suffix #AKH |
| Board | HP 8C40, revision 63.43 |
| BIOS | F.18 |
| CPU | Intel Core i7-13700H, 6 P + 8 E physical cores |
| GPU | NVIDIA GeForce RTX 4060 Laptop GPU |
| Sleep | Modern Standby / S0 Low Power Idle |
| Target profile | `HP-8C40-9D0R1LA-F18` |

The former HP 88F8 / Victus 16-d0515la / i7-11800H / RTX 3060 is **historical, not an interchangeable target**. A different SKU, board, BIOS, GPU or sleep platform needs its own verification.

Read [target qualification and historical sweeps](HARDWARE_8C40.md) and [product validation status](PRODUCT_VALIDATION_STATUS.md).

## Source and evidence hierarchy

- **Physical observation:** a measured event tied to a session, timestamp, platform, commit and input artifacts. It is not a general compatibility guarantee.
- **Reproduced statistical characterization:** supports an observed pattern, but not necessarily a complete explanation.
- **AML/DSDT trace:** establishes a field, method and conversion in the specific BIOS, not the physical semantics of every named field.
- **CI/replay PASS:** checks software and fixtures, not sound levels, cooling equivalence or hardware safety.
- **Firmware model:** explicitly hypothetical unless independently validated. The OEM imitation research currently remains **NO-GO for physical fan control**.

**Version caveat:** at this documentation cut, `main` still displays a v1.0.0 source snapshot, while [v1.2.3 is the latest published release](https://github.com/Esteban-3055/VictusFanControl/releases/tag/v1.2.3) on a different commit. Historical `Automatic=false` statements apply to old milestones; consult the matching tag and build before describing current behavior.

## Fans: HP BIOS/WMI versus real tachometers

The 8C40 production path uses HP BIOS/WMI, including:

| Operation | HP command | Meaning |
|---|---|---|
| SetFanMode | `0x1A` | Firmware mode, including `LegacyDefault` for release |
| GetFanLevel | `0x2D` | Reported effective CPU/GPU **level**, not exact tachometer RPM |
| SetFanLevel | `0x2E` | Bounded CPU/GPU level request |
| Release sequence | `FF/FF` + `LegacyDefault` | Return authority to HP firmware |

An old UI may display `level * 100` as **nominal RPM**. This is not independently measured physical RPM, and a nonzero level does not alone prove rotation. Early physically bounded tachometer studies used EC B0–B3 for research; they are not the current production fan-sensor source.

Historically, an equal-level sweep 10..50 was physically characterized on this machine (research tachometer endpoints about 1028/1013 RPM at level 10 and 4991/4991 RPM at 50). VFC's **equal CPU/GPU command** does not mean the two rotors turn at identical RPM. Under OEM firmware, WMI can show **different CPU and GPU levels**.

The production choice is **WMI-only** for fan commands/levels; direct EC I/O is excluded. See [WMI isolation](WMI_ONLY_EC_ISOLATION.md), [WMI telemetry](HP_8C40_WMI_FAN_TELEMETRY.md) and [fan-control evidence](HP_8C40_WMI_CONTROL_PROOF.md).

## Thermal surfaces: key findings

| Signal | Supported interpretation on BIOS F.18 | Qualification boundary |
|---|---|---|
| ACPI TZ01 | AML-traced to EC `RTMP 0x58` | ACPI values may be cached/slow |
| Intel DTT1 / DTT2 | CPU package/core-like domains | Not a universal sensor mapping |
| Intel DTT3 | Filtered/virtual GPU/platform domain | Not a direct instantaneous GPU reading |
| EC `GPTM 0xB7` | Strongly tracks GPU heat in laboratory sweeps | **Not a production EC input** |
| EC `TNT1 0x59` | Slowly changing platform-related thermal field | Interpretation from observed dynamics |
| EC `CPUT 0x57` and `TNT4 0x49` | Matched 528/529 clean sweeps | Neither equals raw CPU Package |
| EC `GPUT 0x89` | Mostly 4 during clean sweeps | **Not GPU temperature** in this unit |

The DSDT's TZ01 conversion is approximately `_TMP = 2732 + 10 * RTMP` (tenths of kelvin): an EC field of 70 may therefore appear as **70.05 °C** in Windows. That display precision is not sensor resolution.

DTT3 exposed `AC1=55 °C` and `AC0=67 °C`. It changed from a ~39 °C median to ~64 °C under GPU-only loading, lagging the graphics domain. Those trip-point values **must not be asserted as the exact OEM fan-switch thresholds**.

Direct EC sweeps suffered variable latency and inconsistent readings: 529/786 completed in 20 ms or less, 241 took at least 250 ms, and the worst exceeded 7 s. A transient Windows battery report showed 0% while AC was still connected; a causal link to EC probing was **not conclusively proven**. Windows `acpi.sys` coordination is a plausible shared-resource concern. **No direct EC access in the production loop.**

See [OEM shadow research](OEM_SHADOW_MODEL_8C40.md), [ACPI/EC isolation](SCENARIO_C_RESIDUAL_EC.md) and [TZ01/DTT3 analysis](PLATFORM_THERMAL_POLICY_2026-10-08.md).

## OEM fan behavior: observed, not fully decoded

The v10/v11 WMI observations identified recurring **fan-level** plateaus (not physical tachometer RPM):

| Initially recognized state | CPU level | GPU level |
|---|---:|---:|
| A | 21–22 | 19–20 |
| B | 25–26 | 23–24 |
| C | 33–34 | 28–29 |
| D | 39–40 | 35–36 |

Fan response often continued after the load ended. v11 captured A→B at approximately **14:14:21 UTC** and B→A at approximately **14:18:42 UTC**, using true fan-acquisition epochs. A short ~97 °C CPU spike did not immediately leave A; a sustained hot condition did. B→A occurred around CPU 41 °C, GPU 35 °C, DTT3 39 °C and TZ01 42 °C. This supports hysteresis/persistence, **not a proven internal HP state machine**.

A later hour-long session revealed approximately **46–47 CPU / 40–41 GPU** levels lasting minutes, outside A/B/C/D. Therefore those seed plateaus are **not an exhaustive list**. The observed-state classifier now distinguishes `Unmapped` from genuine transitions; a stable unmapped candidate is not automatically a newly qualified OEM state.

The original shadow model scored **24.24% known-state accuracy on v10** and **86.77% on v11**, with absent C/D support and late transitions in v11. Whole-session-held-out comparisons favored causal thermal history over instantaneous-only models, but still failed to explain high ventilation robustly. The OEM shadow model is **NO-GO for fan-control authority**.

Sources: [original replay](OEM_SHADOW_MODEL_8C40.md), [classification correction](OEM_SHADOW_CLASSIFICATION_2026-10-08.md), [held-out comparison](OEM_SHADOW_MODEL_COMPARISON_2026-10-08.md).

## Separate VFC product policy and performance controls

VFC's own adaptive demand is **not** a claim about HP's OEM algorithm. It combines the highest of six demands (CPU/GPU temperature, power and load), filtering/inertia and independent raw thermal protections. The optional TZ01/DTT3 retention is experimentally bounded and disabled by default; lower simulated demand is not evidence of lower measured noise or equivalent cooling.

CPU RAPL research physically observed `PL1/PL2=45/115 W` as the then-current baseline and qualified a bounded **20/40 W** limit under similar ~100% load, with approximate observed package power changing from **39.91 W to 19.29 W** and verified restoration. This baseline is **not universal**.

GPU control uses NVIDIA NVML locked graphics clocks (AC **210..1850 MHz**; battery **210..1200 MHz**). NVML does not supply a documented independent getter for the installed arbitrary locked range in this implementation; successful Set is `ActiveUnverified`, **not exact ownership proof**. The detached PerformanceGuardian and separate CPU/GPU journals keep recovery and authority domains separate. An earlier manual ~1800 MHz cap coincided with a preliminary GPU hotspot reduction from ~105 to ~92 °C; this **does not diagnose or rule out a mechanical cooling issue**.

Sources: [CPU RAPL](INTEL_RAPL_P1.md), [Performance Guardian](INTEL_RAPL_P2B_GUARDIAN.md), [current product limits](PRODUCT_VALIDATION_STATUS.md), [experimental retention](PRODUCT_PLATFORM_RETENTION_2026-10-09.md).

## Research map and remaining work

| Topic | Primary record |
|---|---|
| Machine identity, historical fan sweep | [HARDWARE_8C40.md](HARDWARE_8C40.md) |
| WMI/EC reliability and isolation | [WMI_ONLY_EC_ISOLATION.md](WMI_ONLY_EC_ISOLATION.md) |
| OEM state/replay research | [OEM_SHADOW_MODEL_8C40.md](OEM_SHADOW_MODEL_8C40.md) |
| Unmapped classification and fresh hour-long capture | [OEM_SHADOW_CLASSIFICATION_2026-10-08.md](OEM_SHADOW_CLASSIFICATION_2026-10-08.md) |
| Causal model comparisons | [OEM_SHADOW_MODEL_COMPARISON_2026-10-08.md](OEM_SHADOW_MODEL_COMPARISON_2026-10-08.md) |
| Interrupted CPU+RAM workload and sensor-epoch fix | [PLATFORM_CPU_RAM_INTERRUPTION_2026-10-08.md](PLATFORM_CPU_RAM_INTERRUPTION_2026-10-08.md) |
| Current distribution and validation limits | [PRODUCT_VALIDATION_STATUS.md](PRODUCT_VALIDATION_STATUS.md) |
| Latest tagged release instructions | [v1.2.3 README](https://github.com/Esteban-3055/VictusFanControl/blob/v1.2.3/README.md) |

**Research backlog:** independently characterize stable Unmapped/high fan regimes; test whole-session held-out OEM hypotheses; validate the *exact distributed build* under comparable thermal/acoustic and lifecycle conditions; distinguish WMI fan levels from independently measured tachometers; and keep `main` documentation discoverable without silently rewriting release provenance.

**Safe publication practice:** remove identifying machine/user paths and raw personal data from logs; preserve checksums, timestamps and exact commit provenance. Treat EC register maps as research findings only. No physical write tests or destructive failure tests are authorized merely by listing them in this atlas.
