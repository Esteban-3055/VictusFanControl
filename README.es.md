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
- OMEN Gaming Hub abierto y undervolt CPU conservado antes/después;
- watchdog Gate A validado como servicio Windows en Session 0: LocalService quedó bloqueado por `Global\Access_EC`, mientras LocalSystem pasó 3/3 ciclos read-only con PawnIO EC + HP WMI y EC permaneciendo FF/FF;
- watchdog Gate B validado físicamente: con la GUI terminada a la fuerza y 30/30 huérfano, el servicio LocalSystem en Session 0 ejecutó por sí solo `FF,FF -> LegacyDefault`, verificó FF/FF en ~443 ms, una lectura independiente volvió a confirmar FF/FF y el undervolt de OMEN Gaming Hub permaneció sin cambios.

## Seguridad integrada

La ruta v0.4 falla de forma cerrada ante identidad incorrecta, telemetría inválida, emergencia térmica, comandos fuera de 14-50, ownership externo, falta de ACK del setpoint o de cualquiera de los tacómetros, sobrescritura externa, límites de suspensión/reanudación y excepciones del backend.

Suspensión, pérdida de seguridad y salida devuelven la autoridad a HP mientras el proceso siga ejecutándose. La terminación forzada ya fue caracterizada físicamente: después de matar la GUI con 30/30 activo, el fixed setpoint permaneció 30/30 y un componente externo HP/OMEN refrescó EC 0x63 aproximadamente cada 30 s. Por tanto, el countdown no puede considerarse un crash fail-safe fiable. Gate B ya demostró que el servicio watchdog independiente puede devolver ese estado huérfano a `FF,FF -> LegacyDefault`; lo que falta antes del control desatendido es Gate C-G: lease autenticado, journal durable, detección de muerte/timeout y recuperación del propio servicio.

## Inicio rápido

```powershell
cd VictusFanControl
.\scripts\setup-pawnio-modules.ps1
.\scripts\probe-backends.ps1
.\scripts\run-gui.ps1
```

La GUI muestra disponibilidad del backend y autoridad actual, pero la **política automática sigue desactivada**.

La ruta integrada, la suspensión real mientras `Custom` estaba activo y la terminación forzada del proceso ya fueron caracterizadas físicamente. El resultado del forced-kill es deliberadamente conservador: EC 0x63 fue refrescado externamente mientras 30/30 seguía activo, por lo que el siguiente bloqueo de seguridad es implementar un watchdog/lease independiente de la GUI. El harness de caracterización queda disponible en `scripts/test-forced-kill-watchdog.ps1`; consulta `docs/FORCED_KILL_WATCHDOG_TEST.md` y `docs/CRASH_WATCHDOG_DESIGN.md`.

Consulta `docs/BACKEND_INTEGRATION_V0.4.md`, `docs/PRE_CONTROL_CHECKLIST.md`, `docs/SAFETY.md` y `docs/OMENMON_COMPAT_AUDIT.md`.
