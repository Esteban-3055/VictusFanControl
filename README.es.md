# VictusFanControl

Proyecto experimental de control adaptativo de ventiladores para notebooks HP Victus, comenzando por el equipo de desarrollo HP **88F8** ya caracterizado.

> **Estado actual: v0.4, integración del backend.** La ruta HP 88F8 validada en hardware ya está integrada detrás del coordinador central de seguridad/autoridad. La política automática de ventiladores sigue **DESACTIVADA**, por lo que abrir la GUI no toma autoridad ni envía niveles de ventilador.

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

## Arquitectura actual

```text
Telemetría
  PawnIO Intel MSR / ACPI EC
  NVIDIA NVML
  carga CPU de Windows
        |
        v
Runtime state + SafetyGate
        |
        v
FanControlCoordinator
  Firmware / Custom / Restoring / Faulted
        |
        v
Hp88F8FanControlBackend
  WMI SetFanLevel
  confirmación EC
  confirmación de ambos tacómetros
  restore FF,FF -> LegacyDefault
        |
        v
Firmware HP / ventiladores
```

La GUI construye esta ruta, pero todavía no existe una política adaptativa que llame automáticamente a `TryEnterCustomAsync` o `ApplyAsync`. En uso normal de la GUI, HP conserva la autoridad.

## Datos validados en este equipo

| Nivel WMI solicitado | Ventilador CPU | Ventilador GPU | Observación |
|---:|---:|---:|---|
| 14 | ~1.400 RPM | ~1.400 RPM | Punto bajo estable; falta validar arranque desde reposo |
| 30 | ~3.000 RPM | ~3.000 RPM | Prueba real de escritura/ack/restore aprobada |
| 50 | ~4.330 RPM | ~4.670 RPM | Techos físicos diferentes |

A nivel 30 ambos ventiladores convergieron casi a las mismas RPM físicas aunque el EC mostró aproximadamente 75% CPU / 68% GPU. El controlador final utilizará por eso un objetivo físico de RPM compartido con realimentación/compensación independiente por ventilador.

## Validaciones de hardware completadas

- telemetría directa PawnIO/NVML y ambos tacómetros;
- soak de 30 minutos con 1629/1629 muestras completas;
- suspensión/reanudación post-fix probada correctamente en 2 ciclos (los 3 ciclos adicionales planificados fueron omitidos explícitamente, por lo que la validación de lifecycle sigue siendo parcial);
- WMI `SetFanLevel(30,30)`;
- ownership mediante EC 0x34/0x35;
- respuesta estable cercana a 3000 RPM en ambos ventiladores;
- liberación `SetFanLevel(FF,FF) -> FanMode=LegacyDefault`;
- OMEN Gaming Hub abierto y undervolt CPU conservado antes/después.

## Seguridad integrada

La ruta v0.4 falla de forma cerrada ante identidad incorrecta, telemetría inválida, emergencia térmica, comandos fuera de 14-50, ownership externo, falta de ACK del setpoint o de cualquiera de los tacómetros, sobrescritura externa, límites de suspensión/reanudación y excepciones del backend.

Suspensión, pérdida de seguridad y salida devuelven la autoridad a HP mientras el proceso siga ejecutándose. Un cierre forzado del proceso no puede ejecutar cleanup administrado; aún debemos caracterizar el countdown/watchdog del firmware antes de habilitar control automático desatendido.

## Inicio rápido

```powershell
cd VictusFanControl
.\scripts\setup-pawnio-modules.ps1
.\scripts\probe-backends.ps1
.\scripts\run-gui.ps1
```

La GUI muestra disponibilidad del backend y autoridad actual, pero la **política automática sigue desactivada**.

Consulta `docs/BACKEND_INTEGRATION_V0.4.md`, `docs/PRE_CONTROL_CHECKLIST.md`, `docs/SAFETY.md` y `docs/OMENMON_COMPAT_AUDIT.md`.
