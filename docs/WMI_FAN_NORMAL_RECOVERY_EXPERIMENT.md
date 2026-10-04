# HP 8C40 / F.18: operación normal y recuperación sin EC directo

Ruta experimental independiente del backend productivo. No instala el driver ACPI, no cambia Secure Boot/HVCI y no habilita Automatic en la GUI. CPU sigue con MSR/PawnIO y GPU con NVML. Windows/HP WMI/firmware pueden seguir usando el EC internamente.

## Diseño

- Normal: curva candidata existente, con mínimo experimental 30 y máximo 50, niveles CPU/GPU iguales, subida gradual y bajada confirmada. Se escribe sólo cuando cambia el nivel. El mismo SafetyGate exige telemetría completa, fresca, plausible y temperaturas inferiores a los umbrales de entrega a firmware. No hay lectura de consignas ni guardas EC, tampoco sustitutos ficticios.
- Cada ciclo, tanto en simulación como en control, espera una nueva lectura RPM `20008h/2Dh` con presupuesto total de 3 segundos usando el lector/broker existente. Sólo después se muestrean CPU/GPU y se evalúa la curva. El snapshot consume la publicación de esa lectura sin programar otra consulta periódica. Una consulta rechazada, cancelada o vencida detiene el ciclo sin reutilizar el cache como sustituto. La antigüedad RPM sigue empezando antes de adquirir la respuesta WMI; los timestamps CPU/GPU comienzan después de esa espera. No se modifica el polling de la GUI ni de las demás rutas.
- Recuperación: se cierra permanentemente la admisión normal y el supervisor envía `FF/FF → LegacyDefault` después de confirmar salida del worker y drenaje de sus consultas. Se intenta LegacyDefault aunque falle la liberación. La aceptación de ambas llamadas **no demuestra independientemente propiedad del firmware**.
- Supervisor separado: observa heartbeat, salida del worker y eventos ACPI 13/15. El worker detecta pérdida del supervisor y trata de liberar localmente si había intención de escritura. No protege frente a bloqueo de Windows, apagado, doble muerte o firmware bloqueado.
- Un mutex global serializa todas las llamadas HP de ambos procesos de esta prueba, incluidas RPM. Se vuelve a comprobar stop/whitelist después de adquirirlo. No coordina clientes HP externos ni el firmware.
- Antes de cada cambio se publica una intención durable. Antes de invocar el método nativo se publica un marcador de llamada pendiente. Si el transporte lanza una excepción sin retorno nativo confirmado, o se abandona el mutex, no se admiten llamadas posteriores: se conserva el lease. Un timeout no se interpreta como cancelación del firmware.
- El lease experimental impide pruebas simultáneas o reinicio después de una recuperación pendiente. Sólo se retira con salida del worker confirmada, ninguna llamada nativa pendiente y, además, sin intención de escritura o con ambas solicitudes de liberación aceptadas. Una lectura pendiente en simulación también conserva el lease. Es un journal de solicitudes pendientes, no una certificación de restauración. No borrarlo para saltar bloqueos.

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

## Captura de simulación ded92d, 2026-10-04

ZIP SHA-256: `cccd8b11d7ff380d1c98788b220839796963a67914af3142ad3b24f3e355492e`. Se verificaron los 143 archivos del manifiesto. Ventana normal de unos 33 segundos; 30 snapshots y 28 decisiones admitidas. Las dos cronologías son consistentes: 17 consultas `20008h/2Dh`, todas con retorno cero y respuesta de 128 bytes; duración nativa mediana 361,69 ms, máxima 383,64 ms. Cero lecturas EC o escrituras de ventilación registradas; no se detectaron nuevos ACPI 13/15. La simulación no llegó a completar los cinco minutos.

La parada fue una pérdida de admisión de telemetría causada por un cálculo de edad: la muestra de RPM tenía 2531 ms al terminar la lectura de sensores, pero se volvía a sumar el tiempo desde el inicio de esa lectura (~505 ms). Se superaba falsamente el límite de 3000 ms. Se añade `FanAgeCapturedAtUtc` al snapshot/CSV para medir únicamente el tiempo posterior a capturar esa edad. Se conserva el límite original, la edad desde el inicio de adquisición WMI y el timestamp CPU/GPU. Una prueba con los datos de esta captura reproduce el rechazo anterior, admite la muestra corregida y comprueba el vencimiento exacto y el rechazo de CPU/GPU antiguos.

El mensaje «Cronología incompleta o contiene EC directo» era otro defecto del lanzador: leía los campos en la raíz del informe offline, aunque están dentro de `Analysis`. Se corrige esa lectura, se distinguen inventario ausente, PID incorrecto, cronología inconsistente y lecturas EC reales, y se conserva el motivo previo del fallo. El supervisor ahora valida y copia el motivo del worker; ya no sustituye una salida temprana por `duration`. Las próximas pérdidas de admisión guardan `telemetry-fault.json` con la edad y sus timestamps.

Esta captura diagnostica defectos del arnés; no valida control activo, restauración por firmware ni ausencia definitiva del error ACPI. El siguiente paso sigue siendo repetir sólo la simulación de cinco minutos antes de evaluar `-Control`.

## Simulación 64fcdb y control d3dffc / 76d955

La simulación corregida `64fcdb` completó cinco minutos: captura válida, 151 consultas WMI, máximo nativo 394,58 ms, ningún nuevo ACPI 13/15 detectado y ninguna escritura de ventilación o lectura EC registrada.

Los intentos activos se interrumpieron sin completar cinco minutos. `d3dffc` duró unos nueve segundos y envió dos órdenes; perdió el cache RPM porque la siguiente consulta se programó sólo al finalizar el muestreo CPU/GPU, cuando la adquisición anterior ya había vencido. `76d955` duró unos 216 segundos y envió nueve órdenes: un snapshot con edad RPM de 2704 ms quedó vencido mientras el setter esperaba la lectura periódica, que terminó correctamente unos 385 ms después. No se detectaron nuevos ACPI 13/15 ni lecturas EC en ninguna captura. Ambos supervisores registraron retorno cero de `FF/FF` y `LegacyDefault`, sin lease retenido ni finalización nativa desconocida; esto no verifica independientemente propiedad del firmware.

SHA-256 de los ZIP revisados:

- `d3dffc`: `7ab0680f6d1554e48dd72e142d33986efe0baa0d2de0f4509aaae377921d9b73`.
- `76d955`: `7db768901abe06f6a88a074d9c40322695528d862cfd869f1f5e702f9f482fb1`.

La adquisición secuencial descrita arriba elimina esa dependencia del polling respecto al tiempo de muestreo y escrituras del ciclo. Las pruebas sin hardware reproducen el cache vencido y la espera que agota una admisión de 2704 ms; exigen una adquisición real nueva, comprueban que el snapshot no programa polling propio y conservan el vencimiento exacto a 3000 ms. También rechazan respuestas inválidas, consultas completadas demasiado tarde y stop antes/después de adquirir RPM. Se mantienen las comprobaciones de stop y frescura bajo el mutex y justo antes del método nativo: un retraso real todavía puede cerrar la admisión. No se añade reintento de setters ni se cambia la curva o su inercia.

La corrección necesita otra captura física completa antes de avanzar a uso diario. Repetir primero simulación con esta nueva coordinación; pasar a control sólo después de revisar su resultado.

## Interpretación y límites

Una sesión de control sin ACPI 13/15 muestra que el episodio no se reprodujo en esa ventana; no demuestra que se haya eliminado. La variante cambia tanto el transporte de comprobación como el modo/ritmo de control y pausa M4, por lo que es una comparación diagnóstica, no causalidad aislada ni validación para uso diario. Si aparece un evento se detiene el control normal y se intenta la recuperación descrita. Si la finalización nativa es desconocida, no se envían llamadas potencialmente superpuestas: se conserva la evidencia y no se anuncia éxito.

Las RPM supervisan movimiento, no son ACK de consigna ni prueba de restauración. El mínimo 30 es provisional para la primera sesión, no una nueva curva definitiva. No se modifican los gates productivos ni se confiere a este arnés la robustez ya comprobada del watchdog productivo. Primero se revisa esta prueba y luego se decide la integración diaria.
