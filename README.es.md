# VictusFanControl

Proyecto experimental de control adaptativo de ventiladores para notebooks HP Victus, comenzando por la placa HP **88F8**.

> **Estado actual: v0.1 solo telemetría.** Esta versión **no** escribe velocidades de ventilador, registros EC, ajustes BIOS de ventiladores ni límites de potencia.

La meta es desarrollar un controlador que no dependa únicamente de la temperatura. La estrategia futura combinará temperatura, potencia del CPU/GPU, uso, tendencia térmica, RPM reales, histéresis y protecciones de seguridad.

## Equipo inicial

- HP Victus 16-d0515la
- Intel Core i7-11800H
- NVIDIA GeForce RTX 3060 Laptop GPU
- Placa HP `88F8`, versión `88.58`

No se guardan números de serie únicos en el repositorio.

## Datos ya medidos

| Nivel solicitado | Ventilador CPU | Ventilador GPU | Observación |
|---:|---:|---:|---|
| 14 | ~1.400 RPM | ~1.400 RPM | Punto bajo estable |
| 30 | ~3.000 RPM | ~3.000 RPM | Sigue el objetivo correctamente |
| 50 | ~4.330 RPM | ~4.670 RPM | Ambos reportan 100%; techo físico alcanzado |

Estos datos corresponden al equipo de desarrollo y no deben asumirse idénticos en todos los Victus.

## Inicio rápido

```powershell
cd VictusFanControl
.\scripts\bootstrap.ps1
```

Para listar sensores:

```powershell
.\scripts\list-sensors.ps1
```

Para registrar 15 minutos en reposo:

```powershell
.\scripts\run-baseline.ps1 -Scenario idle -Minutes 15
```

## Etapas

La v0.1 registra telemetría sin controlar hardware. Luego añadiremos RPM de ventiladores en modo lectura, después el supervisor de seguridad y recién entonces un backend de control experimental.

Consulta [docs/ROADMAP.md](docs/ROADMAP.md) y [docs/SAFETY.md](docs/SAFETY.md).
