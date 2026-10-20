# VictusFanControl v1.3.0

[Español](README.es.md) · [Stable release](https://github.com/Esteban-3055/VictusFanControl/releases/tag/v1.3.0) · [Release notes](release/RELEASE-NOTES-v1.3.0.md) · [Operating guide](docs/PRODUCT_V1.md)

Windows fan control and CPU/GPU preferences for **HP 8C40 / 9D0R1LA / BIOS F.18** (Victus 15-fa1xxx, board revision 63.43, i7-13700H, RTX 4060 Laptop GPU). Other models and BIOS versions are outside this release's control authorization.

## Install and update

Download **VictusFanControl-1.3.0-Setup-win-x64.exe** from Releases or check for updates in the application. Requires Windows 11 x64, .NET Desktop Runtime 8 x64, PawnIO and NVIDIA/NVML. The installer preserves saved curves and profiles; unresolved sessions retain their recovery records and block new control.

The ZIP alternative is **VictusFanControl-1.3.0-win-x64.zip**. Extract all files, exit the previous app from its tray menu, and run `Start-ProductGui.ps1 -Mode Install` as administrator with the same Windows account. Closing the window minimizes it; **Exit** in the tray releases control.

## Current behavior

- 1.3.0 fixes skipped final cleanup, recovery after a returned GPU release error and a lost final Guardian reply. It validates battery startup settings, separates update-cache reads from startup, and serializes installation. [Audit and validation](docs/RELEASE-1.3.0-VALIDATION.md).
- AC/Battery profiles, internal fan curve, CPU/GPU limits and live demand/applied markers. Internal logical fan levels are CPU 10–54 and GPU up to 52; external output is independent, 0–255.
- Optional minimized startup and Automatic activation after three distinct fresh observations. Preparation refreshes stale telemetry through the coordinated reader. Late interface states cannot replace a newer published state.
- AC/Battery reconciliation requires fresh Guardian confirmation and retains its bounded transition. A Windows battery indication alone does not prove physical unplugging.
- Internal CPU thermal protections remain active. GPU handoff is strictly above 95 °C; CPU consumption above 60 W alone does not disable habitual Automatic. GPU range is configurable up to 2535 MHz.
- Definitive session recovery and bounded automatic resumption retain evidence and refuse uncertain native reentry. A driver call that never returns cannot be universally recovered without restarting Windows.
- No TZ01/DTT3 acquisition or direct EC access in normal product operation.

## ESP32 external base

Firmware code is unchanged from 1.2.17. An ESP32 already running that firmware does not need reflashing for these Windows fixes.

Download **VictusBase-ESP32C3-v1.3.0-Arduino-sketch.zip** from the same release. Extract and open `VictusBaseESP32C3/VictusBaseESP32C3.ino`; PWM is enabled. Use Espressif core **3.3.2**, ArduinoJson **6.21.5**, **ESP32C3 Dev Module**, flash **4 MB**, USB CDC enabled, and full flash erase disabled to retain pairing/NVS.

The external controller runs EMA, hysteresis and ramps on ESP32; Windows supplies the settings and telemetry. Ambient temperature is informational. On disconnect, output ramps toward **130/255**. LCD cycle: CPU/GPU + ambient 30 s; CPU/GPU/RAM usage 5 s; demand + applied PWM 5 s; PC/Wi-Fi 5 s. Missing or stale readings show `--`.

GPIO3 inverted PWM 18 Hz/14 bits, GPIO0 NTC, GPIO6/7 I2C, Wi-Fi 8.5 dBm and HMAC TCP 8765 are preserved. Public archives contain no private pairing key, SSID or password. [Firmware build, wiring and provisioning](firmware/esp32-c3/README.md).

## Validation and limits

Historical qualification profiles retain `control.enabledByDefault=false` and
`automaticPolicyEnabled=false`. These experimental hosts remain separate from
the target-authorized product GUI and its explicit startup preferences.

Release assets carry SHA-256 and source provenance. Windows fixtures, loopback communication, native C++ controller/protocol, PlatformIO and Arduino compilation are software evidence. They do not certify physical LCD, NTC, PWM, fan rotation, thermal response, Windows startup or AC/Battery behavior on this build. Pending physical checks remain in `PRODUCT-RELEASE.json`.

[Coordination review](docs/AUTOMATIC-COORDINATION-REVIEW.md) · [Recovery](docs/FORCED-SESSION-RECOVERY.md) · [External base policy](docs/EXTERNAL-BASE-RAMPED-LOCAL.md) · [Third-party notices](THIRD_PARTY_NOTICES.md)
