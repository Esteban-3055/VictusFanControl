# VictusFanControl

VictusFanControl es una aplicación experimental de control de ventiladores con diseño **fail-closed** para el objetivo exacto validado **HP 8C40 / 9D0R1LA / BIOS F.18**.

## Equipo objetivo actual

El desarrollo productivo actual está limitado a:

- familia HP Victus 15-fa1xxx, prefijo de SKU validado `9D0R1LA`
- placa `HP 8C40`, revisión `63.43`
- BIOS `F.18`
- Intel Core i7-13700H, 14 núcleos físicos
- NVIDIA GeForce RTX 4060 Laptop GPU
- comandos CPU/GPU siempre iguales, con niveles **10 a 50** físicamente validados

El objetivo HP 88F8 anterior permanece en el repositorio como soporte/evidencia histórica. No es el objetivo del trabajo post-M9 actual.

## Estado actual del control

La ruta productiva M9 con watchdog ya fue promovida para el objetivo HP 8C40 exacto. La construcción normal del backend utiliza el lease M4 ligado al objetivo y mantiene el protocolo fail-closed de ownership y restauración.

El control de ventiladores para el usuario continúa deliberadamente **apagado por defecto**:

- `control.enabledByDefault=false`
- `automaticPolicyEnabled=false`
- el arranque normal no envía comandos de curva automática
- los gates de cualificación M9C/M9D siguen cerrados
- la interfaz P13 Firmware/Manual/Automatic está completa a nivel de software
- los gates de ejecución Manual y Automatic siguen cerrados
- la validación física post-M9 de control manual/automático será un gate separado

Por lo tanto, abrir la GUI por sí solo no debe solicitar autoridad Custom.

## Contrato de seguridad productivo

La ruta de escritura admitida es deliberadamente estrecha:

```text
Telemetría (PawnIO Intel + ACPI EC + NVIDIA NVML + carga Windows)
        |
        v
Estado runtime + SafetyGate + confirmación térmica HP 8C40
        |
        v
FanControlCoordinator
        |
        v
Lease del watchdog productivo HP 8C40
        |
        v
Hp8C40FanControlBackend
        |
        v
HP WMI SetFanLevel -> ACK de setpoint EC -> feedback de ambos tacómetros
```

Invariantes principales:

- Los niveles de CPU y GPU siempre son iguales.
- El rango validado es 10..50; no se utiliza nivel 0/fan-stop.
- EC 0x62/0x63 son solo diagnóstico/lectura; no se permiten escrituras EC arbitrarias.
- GPU >= 87 C y CPU >= 99 C fuerzan handoff inmediato al firmware.
- En HP 8C40, CPU 95..98.x C exige cinco muestras nuevas consecutivas antes de la preempción térmica efectiva.
- Telemetría ausente/antigua/implausible, pérdida de ownership, watchdog, lifecycle o backend hacen fail-closed.
- Un setpoint sin cambios no debe reenviarse continuamente por WMI.
- Strong restore: FF/FF + LegacyDefault + FF/FF estable + RELEASE del watchdog + journal ausente.

## Política adaptativa

El motor adaptativo independiente del hardware y el replay/shadow offline ya existen y están probados. Utilizan temperatura, potencia y carga de CPU/GPU para producir un único nivel igual, con slew limitado, confirmación de bajada, deadband y rechazo de telemetría duplicada, fuera de orden o con gaps.

La curva productiva todavía **no está físicamente validada** y la política automática sigue desactivada. Ver `docs/ADAPTIVE_POLICY_PREPARATION.md` y `docs/POST_M9_SOFTWARE_ROADMAP.md`.

## Entorno de desarrollo

Requisitos: Windows 11 x64, .NET 8 SDK, terminal de Administrador para operaciones de hardware/servicio, PawnIO 2.2+ y controlador NVIDIA/NVML.

```powershell
.\scripts\setup-pawnio-modules.ps1
.\scripts\probe-backends.ps1
.\scripts\run-gui.ps1
```

Los self-tests/CI de software no autorizan ejecución física. Los gates físicos se abren por separado y de forma explícita.

## Licencia

MIT. Ver [LICENSE](LICENSE) y [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).
