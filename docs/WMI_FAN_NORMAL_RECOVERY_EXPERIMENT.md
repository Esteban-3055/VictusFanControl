# HP 8C40 / F.18: operación normal y recuperación sin EC directo

Ruta experimental independiente del backend productivo. No instala el driver ACPI, no cambia Secure Boot/HVCI y no habilita Automatic en la GUI. CPU sigue con MSR/PawnIO y GPU con NVML. Windows/HP WMI/firmware pueden seguir usando el EC internamente.

## Diseño

- Normal: curva candidata existente, con mínimo experimental 30 y máximo 50, niveles CPU/GPU iguales, subida gradual y bajada confirmada. Se escribe sólo cuando cambia el nivel. El mismo SafetyGate exige telemetría completa, fresca, plausible y temperaturas inferiores a los umbrales de entrega a firmware. No hay lectura de consignas ni guardas EC, tampoco sustitutos ficticios.
- Recuperación: se cierra permanentemente la admisión normal y el supervisor envía `FF/FF → LegacyDefault` después de confirmar salida del worker y drenaje de sus consultas. Se intenta LegacyDefault aunque falle la liberación. La aceptación de ambas llamadas **no demuestra independientemente propiedad del firmware**.
- Supervisor separado: observa heartbeat, salida del worker y eventos ACPI 13/15. El worker detecta pérdida del supervisor y trata de liberar localmente si había intención de escritura. No protege frente a bloqueo de Windows, apagado, doble muerte o firmware bloqueado.
- Un mutex global serializa todas las llamadas HP de ambos procesos de esta prueba, incluidas RPM. Se vuelve a comprobar stop/whitelist después de adquirirlo. No coordina clientes HP externos ni el firmware.
- Antes de cada cambio se publica una intención durable. Antes de invocar el método nativo se publica un marcador de llamada pendiente. Si el transporte lanza una excepción sin retorno nativo confirmado, o se abandona el mutex, no se admiten llamadas posteriores: se conserva el lease. Un timeout no se interpreta como cancelación del firmware.
- El lease experimental impide pruebas simultáneas o reinicio después de una recuperación pendiente. Se retira cuando no hubo intención de escritura o se aceptaron ambas solicitudes de liberación; es un journal de solicitudes pendientes, no una certificación de restauración. No borrarlo para saltar bloqueos.

## Prueba en el Victus

Volver a Firmware y cerrar la GUI antes de comenzar. Mantener alimentación estable para la primera prueba, permanecer frente al equipo y usar carga ligera habitual. No abrir otra GUI/CLI VFC ni herramientas de control/consulta directa EC durante la sesión. La prueba registra procesos/servicios externos, pero no demuestra su ausencia entre muestras ni impide que un servicio HP cambie los ventiladores.

PowerShell **como administrador**, desde el repositorio:

```powershell
git pull --ff-only
# Primero comprobar telemetría y decisiones; no envía órdenes de ventilación.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\Start-Victus-WmiFanExperiment.ps1 -CaptureMinutes 5
# Luego prueba supervisada del automático WMI, limitado a 30–50.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\Start-Victus-WmiFanExperiment.ps1 -CaptureMinutes 5 -Control
```

No ejecutar ambas a la vez. Pasar a Control sólo si la primera terminó con captura completa, telemetría admitida y sin nuevos ACPI 13/15; ante cualquier fallo conservar y enviar el ZIP antes de repetir. Q en el recolector solicita cierre normal. No cerrar la consola a la fuerza. El watchdog M4 previamente activo se pausa para evitar sus lecturas EC y se reinicia después del ZIP si no queda lease ni proceso experimental activo; su reinicio queda fuera de la ventana de comparación.

Adjuntar ambos ZIP y SHA256. Cada sesión conserva CSV de sensores, decisiones, intención de escritura, heartbeat, resultados del worker, resumen del supervisor, dos cronologías nativas y los EVTX/ETL habituales. Los eventos en la recuperación cuentan también. Se informa explícitamente `FirmwareRestorationVerified=false`, incluso con solicitudes aceptadas.

## Interpretación y límites

Una sesión de control sin ACPI 13/15 muestra que el episodio no se reprodujo en esa ventana; no demuestra que se haya eliminado. La variante cambia tanto el transporte de comprobación como el modo/ritmo de control y pausa M4, por lo que es una comparación diagnóstica, no causalidad aislada ni validación para uso diario. Si aparece un evento se detiene el control normal y se intenta la recuperación descrita. Si la finalización nativa es desconocida, no se envían llamadas potencialmente superpuestas: se conserva la evidencia y no se anuncia éxito.

Las RPM supervisan movimiento, no son ACK de consigna ni prueba de restauración. El mínimo 30 es provisional para la primera sesión, no una nueva curva definitiva. No se modifican los gates productivos ni se confiere a este arnés la robustez ya comprobada del watchdog productivo. Primero se revisa esta prueba y luego se decide la integración diaria.
