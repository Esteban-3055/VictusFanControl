# VictusFanControl

Proyecto experimental de control adaptativo de ventiladores para notebooks HP Victus, comenzando por el equipo de desarrollo HP **88F8** ya caracterizado.

> **Estado actual: v0.3, validación previa al control.** La GUI y la ruta normal del controlador siguen siendo de solo lectura y el firmware HP mantiene la autoridad. El repositorio ya contiene comandos BIOS/WMI experimentales y explícitos para validar la restauración a HP Auto y una primera prueba de escritura estrictamente acotada.

## Equipo validado

La autorización de escritura es más estricta que comprobar solamente `88F8`:

- HP Victus 16-d0515la
- producto del sistema: `Victus by HP Laptop 16-d0xxx`
- prefijo de SKU: `62C37LA`
- placa HP `88F8`, versión `88.58`
- Intel Core i7-11800H
- NVIDIA GeForce RTX 3060 Laptop GPU
- configuración de referencia: monitor externo conectado y RTX 3060 activa

No se guardan números de serie únicos en el repositorio.

## Telemetría

VictusFanControl usa:

- PawnIO + `IntelMSR.bin` para temperatura y potencia del CPU;
- PawnIO + `LpcACPIEC.bin` para ambos tacómetros y diagnósticos EC;
- NVIDIA NVML para temperatura, potencia y uso de GPU;
- API de Windows para carga total del CPU.

El lector EC usa reintentos acotados, backoff, el mutex compartido `Global\Access_EC` y lecturas coherentes repetidas para los valores RPM de 16 bits.

## Datos ya medidos en este equipo

| Nivel WMI solicitado | Ventilador CPU | Ventilador GPU | Observación |
|---:|---:|---:|---|
| 14 | ~1.400 RPM | ~1.400 RPM | Punto bajo estable |
| 30 | ~3.000 RPM | ~3.000 RPM | Sigue el objetivo correctamente |
| 50 | ~4.330 RPM | ~4.670 RPM | Techos físicos diferentes |

Estos valores corresponden al equipo de desarrollo y no se extrapolan automáticamente a cualquier notebook que reporte Product ID `88F8`.

## Inicio rápido

```powershell
cd VictusFanControl
.\scripts\bootstrap.ps1
.\scripts\setup-pawnio-modules.ps1
.\scripts\probe-backends.ps1
.\scripts\run-gui.ps1
```

La GUI sigue siendo de solo lectura.

Antes de considerar apta la primera prueba `30,30`, la versión actual del lector EC y del manejo de suspensión/reanudación debe superar nuevamente las pruebas de estabilidad en el hardware real.

Consulta `docs/PRE_CONTROL_CHECKLIST.md`, `docs/SAFETY.md`, `docs/OMENMON_COMPAT_AUDIT.md` y `docs/FIRST_FAN_WRITE_TEST.md`.
