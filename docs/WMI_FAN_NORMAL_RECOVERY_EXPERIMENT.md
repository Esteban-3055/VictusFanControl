# HP 8C40 / F.18: operación normal y recuperación sin EC directo

Ruta experimental independiente del backend productivo. No instala el driver ACPI, no cambia Secure Boot/HVCI y no habilita Automatic en la GUI. CPU sigue con MSR/PawnIO y GPU con NVML. Windows/HP WMI/firmware pueden seguir usando el EC internamente.

## Diseño

- Normal: curva candidata existente, con mínimo experimental 30 y máximo 50 y niveles CPU/GPU iguales. Una única EMA suaviza la demanda máxima de las seis curvas calculadas con sensores originales; la actuación normal confirma subidas y bajadas y avanza un nivel por cambio. El calor alto activa la excepción térmica descrita abajo. Se escribe sólo cuando cambia el nivel. El mismo SafetyGate exige telemetría completa, fresca, plausible y temperaturas inferiores a los umbrales de entrega a firmware. No hay lectura de consignas ni guardas EC, tampoco sustitutos ficticios.
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

Adjuntar ambos ZIP y SHA256. Cada sesión conserva CSV de sensores originales, decisiones con motivo, demanda original, demanda suavizada y excepción térmica, configuración (`policy.json`, `inertia.json`, `final-demand-filter.json`), intención de escritura, heartbeat, resultados del worker, resumen del supervisor, dos cronologías nativas y los EVTX/ETL habituales. Los eventos en la recuperación cuentan también. Se informa explícitamente `FirmwareRestorationVerified=false`, incluso con solicitudes aceptadas.

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

## Validación de coordinación 942b0e / 41d69e

Las dos capturas con la adquisición secuencial completaron cinco minutos. `942b0e` fue simulación: 190 consultas RPM, máximo nativo 389,57 ms, edad RPM capturada máxima 907 ms y ninguna orden. `41d69e` fue control activo: 191 consultas RPM, 190 decisiones admitidas y tres órdenes normales `30 → 32 → 31`; edad RPM capturada máxima 875 ms y duración nativa máxima 397,84 ms. Ambas capturas fueron válidas, sin nuevos ACPI 13/15 detectados, sin acceso EC directo registrado ni finalización nativa desconocida o lease retenido. En control, las solicitudes de liberación `FF/FF → LegacyDefault` retornaron cero; no se verificó independientemente propiedad del firmware.

SHA-256:

- `942b0e`: `28b975c3cfa2723809cada441cc8b012567b9f03df078a4d8f9ec95a5bbef9fd`.
- `41d69e`: `2142cc285e67c4669d55b64338bc02b88504d9436f8ecc9609c79c92f781c307`.

Estos resultados respaldan la coordinación en esas ventanas con carga ligera. No validan todas las cargas, uso diario o eliminación definitiva de los episodios ACPI.

## Inercia experimental actual

`WmiFanInertiaPolicy` se usa únicamente en este arnés, tanto en simulación como con `-Control`. Las seis curvas, el rango 30–50 y los sensores originales permanecen iguales. El motor compartido conserva su comportamiento en la GUI y otras rutas. La actuación normal depende únicamente de la demanda final suavizada descrita abajo:

- Primera decisión: demanda original conocida, sin asumir que el equipo empieza frío ni imponer una rampa desde el mínimo.
- Subida normal: al menos dos segundos continuos de demanda suavizada superior al nivel actual. Se sube un nivel, sin superar el menor nivel solicitado durante esa ventana. Cada paso necesita otra confirmación. Ni una variación de temperatura moderada ni una demanda grande de potencia/carga omiten esta regla.
- Excepción térmica: CPU original desde 85 °C o GPU original desde 78 °C actualizan inmediatamente el filtro a la demanda original. La subida omite la confirmación temporal, conservando el paso máximo existente de cuatro niveles. Se identifica explícitamente en las decisiones.
- Bajada: la demanda suavizada debe permanecer por debajo de la banda de un nivel durante al menos doce segundos. Se baja un nivel y la siguiente bajada necesita otra ventana continua. Volver a la banda o pedir una subida reinicia la espera. Se usan timestamps de muestras consecutivas válidas, no una cantidad fija de muestras.
- Entradas inválidas, duplicadas, fuera de orden o con huecos mayores de tres segundos producen rechazo y borran el filtro y las confirmaciones. No se devuelve un mantenimiento aceptado ante datos inválidos. El worker conserva su parada y recuperación ante estos rechazos.

`policy.json` conserva la curva candidata y sus parámetros originales; `inertia.json` documenta las confirmaciones temporales y el paso normal de subida. `decisions.ndjson` añade `Detail` para explicar cada mantenimiento o cambio. No se amplía la edad permitida de ninguna lectura. SafetyGate continúa evaluando los sensores originales antes de la política y antes de despachar; CPU a 95 °C o GPU a 87 °C cierran la admisión normal sin esperar la inercia. La coordinación WMI, las comprobaciones bajo el mutex y la recuperación permanecen iguales.

La versión inicial de inercia de niveles permitía subir inmediatamente por cualquier demanda térmica o por diferencias grandes; la versión actual restringe esa excepción al calor alto. Las capturas siguientes corresponden a aquella versión inicial. Su reproducción offline de `41d69e` mantenía tres intenciones `30 → 32 → 31`, con la bajada después de 13,28 segundos de demanda baja sostenida.

### Capturas f56cad / e43bab con inercia de niveles

La simulación `f56cad` completó cinco minutos: 191 consultas RPM exitosas, 190 decisiones, edad RPM máxima 891 ms y ninguna escritura. La curva simulada recorrió niveles 30–38; sus bajadas confirmaron demanda baja durante 12,02–13,68 segundos. No hubo acceso EC directo, nuevos ACPI 13/15 registrados, finalización nativa desconocida ni lease retenido. SHA-256: `b901c11d1bfe8dd89bf864f85a166e8782c4e3edf5a1df441978633ccb7fe83f`.

El control `e43bab` se detuvo por umbral térmico a unos 156 segundos: 98 consultas RPM y nueve órdenes normales, todas con retorno cero; edad RPM máxima 890 ms. La última muestra pasó de CPU efectiva 54 °C, potencia 14,794 W y carga 6,686 % a CPU efectiva 97 °C (package 96 °C / hottest-core 97 °C), 55,166 W y 61,203 %, con GPU a 43 °C. La admisión se cerró por el umbral CPU de 95 °C, sin fallo de frescura ni eventos ACPI nuevos registrados. `FF/FF → LegacyDefault` retornó cero y no quedaron llamadas desconocidas ni lease. La captura no completó cinco minutos y no verifica propiedad del firmware ni establece qué provocó el salto de carga. SHA-256: `6411d5634f40bdeb8ba77f085d1f427f77773715252bfb693d7bdb0509d651a1`.

## Filtro únicamente de demanda final

Se elimina `WmiCpuTemperatureFilter`, introducido en `bbf8654`. Ya no hay una temperatura CPU de curva diferente de la original ni filtros individuales de sensores. Primero se calcula la demanda máxima de las seis curvas originales: temperatura, potencia y carga de CPU y GPU. `WmiFinalDemandFilter` aplica una única EMA a ese resultado, antes de redondear hacia arriba al nivel solicitado y aplicar las confirmaciones de actuación.

Para cada muestra válida se calcula `alpha = 1 - exp(-dt/tau)` y `Dsuavizada = Danterior + alpha * (Doriginal - Danterior)`. Las constantes de tiempo experimentales son **seis segundos al subir y veinte segundos al bajar**. Son constantes de respuesta, no esperas fijas; `dt` procede de los timestamps reales. Un escalón sostenido alcanza aproximadamente el 63 % de su variación tras una constante de tiempo. La primera muestra usa la demanda original. La excepción CPU 85 °C / GPU 78 °C también actualiza el estado a la demanda original para no conservar una historia fría ante calor alto.

Todas las rutas normales pasan por ese mismo filtro, aunque cambie la curva que domina la demanda. Los sensores y sus timestamps no se modifican. La validación de inputs y continuidad ocurre antes de filtrar. NaN, entradas fuera de rango, timestamps repetidos/fuera de orden y huecos mayores de tres segundos se rechazan; no se reutiliza una demanda suavizada para sustituir una muestra inválida. SafetyGate y la admisión bajo el mutex siguen recibiendo el snapshot original. Los umbrales CPU 95 °C / GPU 87 °C, la frescura y la recuperación no cambian. La GUI y el motor compartido tampoco cambian.

El CSV conserva los sensores originales. Las decisiones incluyen `RawDemandLevel`, `SmoothedDemandLevel` y `ThermalOverride`. `final-demand-filter.json` guarda los parámetros efectivos; la consola muestra `demand` y `smooth` junto a CPU/GPU originales. Los fallos de admisión guardan las temperaturas originales. Así puede revisarse la respuesta lenta sin ocultar picos térmicos. El filtro no limita potencia ni elimina por sí mismo el pico de 97 °C.

Las pruebas sin hardware cubren escalones de subida y bajada, equivalencia con cadencias de 0,5 y dos segundos, rechazo de discontinuidad/inputs inválidos y cada una de las seis curvas dominando por separado. Todas las rutas normales requieren confirmación y avanzan un nivel por subida; CPU 85 °C y GPU 78 °C activan la excepción térmica. La muestra real de 97 °C de `e43bab` sigue cerrando SafetyGate y conserva las dos solicitudes de recuperación.

### Capturas 2e9836 / f937a9 y reproducción de demanda final

Estas capturas usaban el filtro CPU anterior de `bbf8654`. La simulación `2e9836` completó cinco minutos: 193 consultas RPM exitosas, 192 decisiones, edad RPM capturada máxima 922 ms y ninguna orden. CPU original máxima 94 °C. No se registraron EC directo, nuevos ACPI 13/15, finalización nativa desconocida ni lease retenido. Se verificaron los 246 archivos del manifiesto. SHA-256: `8224a7be8d9ba36c5c082e7c981f6f87bd4b43d3f39ebc8496ab120063e574bd`.

El control `f937a9` se detuvo por umbral térmico tras unos 186 segundos: 119 consultas RPM exitosas, 117 decisiones y doce órdenes normales con retorno cero; edad RPM capturada máxima 906 ms. La muestra final tenía CPU efectiva 97 °C (package 93 °C / hottest-core 97 °C), GPU 48 °C y RPM frescas de 375 ms. La interrupción fue térmica, no por muestras vencidas. `FF/FF → LegacyDefault` retornó cero, sin lease ni llamada desconocida; esto no verifica independientemente propiedad del firmware. No se registraron EC directo ni nuevos ACPI 13/15. Se verificaron los 209 archivos del manifiesto. SHA-256: `d74b18dd480902ef342f0fe265694e7072a0bc44a8dd473dcb29a908f6838687`.

Al reproducir los sensores originales con el nuevo filtro final, `f937a9` pasa de doce a seis intenciones de nivel y conserva la parada por CPU 97 °C. `2e9836` pasa de 25 a 27 intenciones: la subida por pasos de un nivel puede producir más cambios pequeños. El suavizado no garantiza menos órdenes en toda secuencia. Esta reproducción no envía llamadas de hardware y no predice las temperaturas ni las RPM que tendrá el equipo con una ventilación distinta.

La versión actual necesita otra simulación física de cinco minutos; revisar el ZIP antes de pasar a `-Control`. El suavizado puede reducir cambios bruscos y mantener ventilación más tiempo al enfriar, pero no soluciona por sí mismo el vencimiento de telemetría. Niveles CPU/GPU iguales tampoco garantizan RPM iguales.

## Interpretación y límites

Una sesión de control sin ACPI 13/15 muestra que el episodio no se reprodujo en esa ventana; no demuestra que se haya eliminado. La variante cambia tanto el transporte de comprobación como el modo/ritmo de control y pausa M4, por lo que es una comparación diagnóstica, no causalidad aislada ni validación para uso diario. Si aparece un evento se detiene el control normal y se intenta la recuperación descrita. Si la finalización nativa es desconocida, no se envían llamadas potencialmente superpuestas: se conserva la evidencia y no se anuncia éxito.

Las RPM supervisan movimiento, no son ACK de consigna ni prueba de restauración. El mínimo 30 es provisional para la primera sesión, no una nueva curva definitiva. No se modifican los gates productivos ni se confiere a este arnés la robustez ya comprobada del watchdog productivo. Primero se revisa esta prueba y luego se decide la integración diaria.
