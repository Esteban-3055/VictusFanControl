# HP 8C40 / F.18: operación normal y recuperación sin EC directo

Ruta experimental independiente del backend productivo. No instala el driver ACPI, no cambia Secure Boot/HVCI y no habilita Automatic en la GUI. CPU sigue con MSR/PawnIO y GPU con NVML. Windows/HP WMI/firmware pueden seguir usando el EC internamente.

## Diseño

- Normal: curva candidata existente, con mínimo experimental 30 y máximo 50 y niveles CPU/GPU iguales. Una única EMA suaviza la demanda máxima de las seis curvas calculadas con sensores originales; la actuación normal confirma subidas y bajadas y avanza un nivel por cambio. El calor alto activa la excepción térmica descrita abajo. Se escribe sólo cuando cambia el nivel. SafetyGate exige telemetría completa, fresca y plausible; la admisión experimental añade la confirmación térmica acotada descrita abajo. No hay lectura de consignas ni guardas EC, tampoco sustitutos ficticios.
- Cada ciclo, tanto en simulación como en control, espera una nueva lectura RPM `20008h/2Dh` con presupuesto total de 3 segundos usando el lector/broker existente. Sólo después se muestrean CPU/GPU y se evalúa la curva. El snapshot consume la publicación de esa lectura sin programar otra consulta periódica. Una consulta rechazada, cancelada o vencida detiene el ciclo sin reutilizar el cache como sustituto. La antigüedad RPM sigue empezando antes de adquirir la respuesta WMI; los timestamps CPU/GPU comienzan después de esa espera. No se modifica el polling de la GUI ni de las demás rutas.
- Recuperación: se cierra permanentemente la admisión normal y el supervisor envía `FF/FF → LegacyDefault` después de confirmar salida del worker y drenaje de sus consultas. Se intenta LegacyDefault aunque falle la liberación. La aceptación de ambas llamadas **no demuestra independientemente propiedad del firmware**.
- Supervisor separado: observa heartbeat, salida del worker y eventos ACPI 13/15. El worker detecta pérdida del supervisor y trata de liberar localmente si había intención de escritura. No protege frente a bloqueo de Windows, apagado, doble muerte o firmware bloqueado.
- El heartbeat se publica mediante archivo temporal durable y reemplazo atómico (`File.Move` sólo para la primera publicación; `File.Replace` para actualizar una existente). El supervisor abre la publicación con `FileShare.Read | FileShare.Delete`, de modo que su lectura no bloquea el reemplazo. Un archivo aún ausente o un conflicto Windows de compartición/bloqueo (32/33) omite sólo esa observación: se vuelve a leer en el siguiente tick de 500 ms, sin renovar la vigilancia. Sólo un `ElapsedMs` creciente con PID correcto renueva el plazo: 30 segundos sin primera publicación y ocho segundos sin progreso posterior, medidos por el reloj monotónico del supervisor. JSON inválido, PID incorrecto, contador negativo u otros errores de I/O siguen rechazando la sesión. No se reintentan órdenes de hardware ni se amplían los límites de frescura RPM.
- Un mutex global serializa todas las llamadas HP de ambos procesos de esta prueba, incluidas RPM. Se vuelve a comprobar stop/whitelist después de adquirirlo. No coordina clientes HP externos ni el firmware.
- Antes de cada cambio se publica una intención durable. Antes de invocar el método nativo se publica un marcador de llamada pendiente. Si el transporte lanza una excepción sin retorno nativo confirmado, o se abandona el mutex, no se admiten llamadas posteriores: se conserva el lease. Un timeout no se interpreta como cancelación del firmware.
- El lease experimental impide pruebas simultáneas o reinicio después de una recuperación pendiente. Sólo se retira con salida del worker confirmada, ninguna llamada nativa pendiente y, además, sin intención de escritura o con ambas solicitudes de liberación aceptadas. Una lectura pendiente en simulación también conserva el lease. Es un journal de solicitudes pendientes, no una certificación de restauración. No borrarlo para saltar bloqueos.

## Confirmación térmica acotada, sólo en este experimento

Se reutiliza `Hp8C40ThermalEmergencyConfirmation` sin modificarla ni cambiar el SafetyGate compartido. `WmiFanThermalAdmission` añade un plazo monotónico máximo de **2000 ms desde la primera muestra alta admitida**. El plazo no se renueva con comprobaciones, escrituras o nuevas muestras altas. Se exige primero una muestra completa, válida y por debajo de CPU 95 °C / GPU 87 °C; no se inicia control sobre una muestra alta. Los contadores de potencia/carga incompletos durante el warmup frío no autorizan escrituras.

| Lectura original y estado | Admisión experimental |
| --- | --- |
| CPU <95 °C, GPU <87 °C, muestra válida antes del vencimiento | Continúa; borra la racha CPU alta y su plazo. |
| CPU ≥95 °C y <99 °C, GPU <87 °C, después de un inicio sano | Confirma con adquisiciones nuevas; entrega al firmware al alcanzar cinco consecutivas o 2000 ms, lo que ocurra primero. |
| CPU ≥99 °C o GPU ≥87 °C | Cierre inmediato de admisión, incluso si otro sensor está incompleto. |
| Muestra inválida, vencida, duplicada, fuera de orden o con discontinuidad >3 s tras admitir control | Cierre; no se sustituye con cache ni demanda filtrada. |
| Muestra fría que llega al vencer el plazo o después | No reabre admisión; comienza la recuperación. |

CPU sigue siendo el mayor valor entre package y núcleo físico más caliente. No se cambia a Average ni se filtran temperaturas para decidir seguridad. Mientras se confirma calor, la política conserva su excepción térmica original (CPU ≥85 °C o GPU ≥78 °C): demanda original, subida de hasta cuatro niveles y sin esperar la confirmación normal de dos segundos. La confirmación térmica no debe mantener artificialmente un nivel bajo.

Cada adquisición se cuenta una sola vez con `Observe`. `Preview` vuelve a comprobar el mismo snapshot, sin añadir muestras, antes del envío, después de esperar el mutex global y justo antes de la llamada nativa. Durante una confirmación pendiente se elimina únicamente la pausa artificial de un segundo entre ciclos y se limita la espera de adquisición al presupuesto térmico restante. RPM y CPU/GPU siguen siendo secuenciales; no se crean consultas paralelas, polling adicional ni reintentos de órdenes. Fuera de la confirmación se mantiene la pausa normal.

Los dos segundos limitan **la admisión de nuevas órdenes normales**, no la duración de una llamada nativa ya iniciada ni el tiempo efectivo de restauración del firmware. Una consulta cancelada lógicamente conserva el slot hasta retornar; el drenaje, los marcadores de finalización desconocida y el lease siguen aplicándose. El cierre térmico no bloquea las solicitudes de recuperación `FF/FF → LegacyDefault`.

`thermal-admission.json` guarda parámetros; `thermal-admission.ndjson` conserva decisiones crudas y efectivas, racha, plazo y razón de cierre. `decisions.ndjson` incluye esos campos junto a la demanda/actuación. Los fallos durante adquisición o envío añaden `thermal-admission-fault.json`. El CSV mantiene los sensores originales. La GUI, Automatic y las autorizaciones productivas permanecen como estaban: esta combinación necesita captura física antes de considerar su integración final.

Las pruebas sin hardware ejercitan cinco épocas dentro del plazo, expiración antes de cinco, muestras frías oportunas/tardías, vistas repetidas, inicio caliente, datos inválidos, retroceso de reloj, vencimiento durante adquisición, contención real del mutex y comprobación inmediatamente antes del transporte. Una consulta nativa bloqueada demuestra que el vencimiento lógico no libera prematuramente su slot ni publica una respuesta tardía. La recuperación se comprueba tras cerrar la admisión térmica.

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

No ejecutar ambas a la vez. Revisar primero el ZIP de simulación de esta versión: captura completa, telemetría admitida, plazos/rachas coherentes y sin nuevos ACPI 13/15. Sólo después de esa revisión pasar a Control. Si no hubo picos, la sesión comprueba el funcionamiento normal, pero no valida físicamente la confirmación térmica. Ante cualquier fallo conservar y enviar el ZIP antes de repetir. Q en el recolector solicita cierre normal. No cerrar la consola a la fuerza. El watchdog M4 previamente activo se pausa para evitar sus lecturas EC y se reinicia después del ZIP si no queda lease ni proceso experimental activo; su reinicio queda fuera de la ventana de comparación.

Adjuntar ambos ZIP y SHA256. Cada sesión conserva CSV de sensores originales, decisiones con motivo, demanda original, demanda suavizada, demanda de actuación y excepción térmica, configuración (`policy.json`, `inertia.json`, `final-demand-filter.json`), intención de escritura, heartbeat, resultados del worker, resumen del supervisor, dos cronologías nativas y los EVTX/ETL habituales. Los eventos en la recuperación cuentan también. Se informa explícitamente `FirmwareRestorationVerified=false`, incluso con solicitudes aceptadas.

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
- Subida normal: al menos dos segundos continuos de nivel solicitado superior al nivel actual, tras redondear la demanda suavizada como se describe abajo. Se sube un nivel, sin superar el menor nivel solicitado durante esa ventana. Cada paso necesita otra confirmación. Ni una variación de temperatura moderada ni una demanda grande de potencia/carga omiten esta regla.
- Excepción térmica: CPU original desde 85 °C o GPU original desde 78 °C actualizan inmediatamente el filtro a la demanda original. La subida omite la confirmación temporal, conservando el paso máximo existente de cuatro niveles. Se identifica explícitamente en las decisiones.
- Bajada: el nivel solicitado debe permanecer por debajo del nivel actual durante al menos doce segundos. Se baja un nivel y la siguiente bajada necesita otra ventana continua. Solicitar el nivel actual o uno superior reinicia la espera. El redondeo a décimas sustituye la banda adicional de un nivel. Se usan timestamps de muestras consecutivas válidas, no una cantidad fija de muestras.
- Entradas inválidas, duplicadas, fuera de orden o con huecos mayores de tres segundos producen rechazo y borran el filtro y las confirmaciones. No se devuelve un mantenimiento aceptado ante datos inválidos. El worker conserva su parada y recuperación ante estos rechazos.

`policy.json` conserva la curva candidata y sus parámetros originales; `inertia.json` documenta las confirmaciones temporales, el paso normal de subida y `NormalDemandQuantization`. La banda `DecreaseDeadbandLevels` de la configuración compartida no se aplica en este arnés. `decisions.ndjson` añade `Detail` para explicar cada mantenimiento o cambio. No se amplía la edad permitida de ninguna lectura. SafetyGate continúa evaluando los sensores originales antes de la política y antes de despachar; CPU a 95 °C o GPU a 87 °C cierran la admisión normal sin esperar la inercia. La coordinación WMI, las comprobaciones bajo el mutex y la recuperación permanecen iguales.

La versión inicial de inercia de niveles permitía subir inmediatamente por cualquier demanda térmica o por diferencias grandes; la versión actual restringe esa excepción al calor alto. Las capturas siguientes corresponden a aquella versión inicial. Su reproducción offline de `41d69e` mantenía tres intenciones `30 → 32 → 31`, con la bajada después de 13,28 segundos de demanda baja sostenida.

### Capturas f56cad / e43bab con inercia de niveles

La simulación `f56cad` completó cinco minutos: 191 consultas RPM exitosas, 190 decisiones, edad RPM máxima 891 ms y ninguna escritura. La curva simulada recorrió niveles 30–38; sus bajadas confirmaron demanda baja durante 12,02–13,68 segundos. No hubo acceso EC directo, nuevos ACPI 13/15 registrados, finalización nativa desconocida ni lease retenido. SHA-256: `b901c11d1bfe8dd89bf864f85a166e8782c4e3edf5a1df441978633ccb7fe83f`.

El control `e43bab` se detuvo por umbral térmico a unos 156 segundos: 98 consultas RPM y nueve órdenes normales, todas con retorno cero; edad RPM máxima 890 ms. La última muestra pasó de CPU efectiva 54 °C, potencia 14,794 W y carga 6,686 % a CPU efectiva 97 °C (package 96 °C / hottest-core 97 °C), 55,166 W y 61,203 %, con GPU a 43 °C. La admisión se cerró por el umbral CPU de 95 °C, sin fallo de frescura ni eventos ACPI nuevos registrados. `FF/FF → LegacyDefault` retornó cero y no quedaron llamadas desconocidas ni lease. La captura no completó cinco minutos y no verifica propiedad del firmware ni establece qué provocó el salto de carga. SHA-256: `6411d5634f40bdeb8ba77f085d1f427f77773715252bfb693d7bdb0509d651a1`.

## Filtro únicamente de demanda final

Se elimina `WmiCpuTemperatureFilter`, introducido en `bbf8654`. Ya no hay una temperatura CPU de curva diferente de la original ni filtros individuales de sensores. Primero se calcula la demanda máxima de las seis curvas originales: temperatura, potencia y carga de CPU y GPU. `WmiFinalDemandFilter` aplica una única EMA a ese resultado, antes de redondear a una décima con empates hacia abajo y luego hacia arriba al nivel entero solicitado, y aplicar las confirmaciones de actuación. La excepción térmica conserva el techo entero de la demanda original sin redondeo a décimas.

Para cada muestra válida se calcula `alpha = 1 - exp(-dt/tau)` y `Dsuavizada = Danterior + alpha * (Doriginal - Danterior)`. Las constantes de tiempo experimentales son **seis segundos al subir y veinte segundos al bajar**. Son constantes de respuesta, no esperas fijas; `dt` procede de los timestamps reales. Un escalón sostenido alcanza aproximadamente el 63 % de su variación tras una constante de tiempo. La primera muestra usa la demanda original. La excepción CPU 85 °C / GPU 78 °C también actualiza el estado a la demanda original para no conservar una historia fría ante calor alto.

Todas las rutas normales pasan por ese mismo filtro, aunque cambie la curva que domina la demanda. Los sensores y sus timestamps no se modifican. La validación de inputs y continuidad ocurre antes de filtrar. NaN, entradas fuera de rango, timestamps repetidos/fuera de orden y huecos mayores de tres segundos se rechazan; no se reutiliza una demanda suavizada para sustituir una muestra inválida. SafetyGate y la admisión bajo el mutex siguen recibiendo el snapshot original. El detector compartido mantiene CPU 95 °C / GPU 87 °C; la confirmación acotada añadida posteriormente a este experimento se describe arriba. La frescura, recuperación, GUI y motor compartido no cambian.

El CSV conserva los sensores originales. Las decisiones incluyen `RawDemandLevel`, `SmoothedDemandLevel`, `ActuationDemandLevel` y `ThermalOverride`. La demanda de actuación contiene el redondeo normal o la demanda original bajo excepción térmica. `final-demand-filter.json` guarda los parámetros efectivos; la consola muestra `demand`, `smooth` y `actuationDemand` junto a CPU/GPU originales. Los fallos de admisión guardan las temperaturas originales. Así puede revisarse la respuesta lenta sin ocultar picos térmicos. El filtro no limita potencia ni elimina por sí mismo el pico de 97 °C.

Las pruebas sin hardware cubren escalones de subida y bajada, equivalencia con cadencias de 0,5 y dos segundos, rechazo de discontinuidad/inputs inválidos y cada una de las seis curvas dominando por separado. Todas las rutas normales requieren confirmación y avanzan un nivel por subida; CPU 85 °C y GPU 78 °C activan la excepción térmica. La muestra real de 97 °C de `e43bab` sigue activando el detector compartido; la admisión experimental actual confirma sólo después de un inicio sano y conserva las dos solicitudes de recuperación.

## Evidencia previa a la confirmación: `e818c22`, capturas `3d84c5`, `12c8a0` y `9c9616`

La simulación `3d84c5` terminó mediante Q después de unos tres minutos. Se verificaron 205 archivos del manifiesto: 117 consultas RPM, 115 decisiones, dos regresos a nivel 30 con descenso confirmado en 12,44 y 12,70 segundos. CPU máxima 76 °C, GPU 50 °C y edad RPM máxima 891 ms. No valida picos ≥95 °C. SHA-256: `b83cdf417cbf4ad6bbcf5c4622d8d28f047d3d6296e64f1290c4fec0617c100b`.

El control `12c8a0` completó cinco minutos con salida cero: 248 archivos verificados, 191 consultas RPM, 190 decisiones todas en nivel 30, una orden normal 30/30 y liberación, todas con retorno cero. CPU máxima 67 °C, GPU 48 °C, edad RPM máxima 907 ms (906 en decisiones). SHA-256: `2f7f0d18e7d21b5d16457f73fe36781f3130b2d4181a04c4612d5de4ecc647dd`.

El control `9c9616` se detuvo a unos 22 segundos por CPU efectiva 98 °C: package 97 °C, núcleo C3 98 °C, promedio de núcleos 55,786 °C, potencia 35,284 W, carga 16,547 %, GPU 47 °C y edad RPM 375 ms. La adquisición anterior tenía CPU efectiva 63 °C. Se verificaron 154 archivos: 15 consultas RPM, 13 decisiones, dos órdenes normales 30/30 y 31/31, y liberación con retorno cero. La captura termina en el primer valor alto; **no establece cuánto duró el calor**. SHA-256: `701c05adce9db3ba2b670c463e545170bc52fb0b51a664099a980e4a49b1cae1`.

En estas tres capturas no se registraron EC directo, nuevos ACPI 13/15, finalización nativa desconocida ni lease retenido; la aceptación de liberación no verifica propiedad del firmware. Una prueba contrafactual con la nueva admisión comprueba que la primera muestra de 98 °C, después de un inicio sano y con nivel previo 31, abre confirmación y propone nivel 35 mediante excepción térmica. No predice la siguiente temperatura ni el resultado físico de la nueva ventilación.

### Capturas 2e9836 / f937a9 y reproducción de demanda final

Estas capturas usaban el filtro CPU anterior de `bbf8654`. La simulación `2e9836` completó cinco minutos: 193 consultas RPM exitosas, 192 decisiones, edad RPM capturada máxima 922 ms y ninguna orden. CPU original máxima 94 °C. No se registraron EC directo, nuevos ACPI 13/15, finalización nativa desconocida ni lease retenido. Se verificaron los 246 archivos del manifiesto. SHA-256: `8224a7be8d9ba36c5c082e7c981f6f87bd4b43d3f39ebc8496ab120063e574bd`.

El control `f937a9` se detuvo por umbral térmico tras unos 186 segundos: 119 consultas RPM exitosas, 117 decisiones y doce órdenes normales con retorno cero; edad RPM capturada máxima 906 ms. La muestra final tenía CPU efectiva 97 °C (package 93 °C / hottest-core 97 °C), GPU 48 °C y RPM frescas de 375 ms. La interrupción fue térmica, no por muestras vencidas. `FF/FF → LegacyDefault` retornó cero, sin lease ni llamada desconocida; esto no verifica independientemente propiedad del firmware. No se registraron EC directo ni nuevos ACPI 13/15. Se verificaron los 209 archivos del manifiesto. SHA-256: `d74b18dd480902ef342f0fe265694e7072a0bc44a8dd473dcb29a908f6838687`.

Al reproducir los sensores originales con el nuevo filtro final, `f937a9` pasa de doce a seis intenciones de nivel y conserva la parada por CPU 97 °C. `2e9836` pasa de 25 a 27 intenciones: la subida por pasos de un nivel puede producir más cambios pequeños. El suavizado no garantiza menos órdenes en toda secuencia. Esta reproducción no envía llamadas de hardware y no predice las temperaturas ni las RPM que tendrá el equipo con una ventilación distinta.

La versión actual necesita otra simulación física de cinco minutos; revisar el ZIP antes de pasar a `-Control`. El suavizado puede reducir cambios bruscos y mantener ventilación más tiempo al enfriar, pero no soluciona por sí mismo el vencimiento de telemetría. Niveles CPU/GPU iguales tampoco garantizan RPM iguales.

## Capturas 2caa18 / 3fdb9c y acceso concurrente al heartbeat

La simulación `2caa18` completó cinco minutos, pero su HEAD era `bbf8654`: todavía usaba el filtro CPU, por lo que no valida la nueva demanda final de `4990d77`. Se verificaron el ZIP y los 247 archivos del manifiesto. Hubo 192 consultas RPM exitosas, 191 decisiones, edad máxima de 953 ms incluyendo el warmup (906 ms en decisiones), CPU efectiva máxima 84 °C en decisiones y ninguna orden. Sin accesos EC directos ni nuevos ACPI 13/15 registrados, finalización nativa desconocida o lease retenido. SHA-256: `9fd99975d1e44c668a2c5892c95826bcf90aa31cfd457c99a3e8b96573b83b7e`.

La simulación `3fdb9c` sí ejecutó `4990d77`, pero el supervisor falló al leer `heartbeat.json` con `File.ReadAllText`: Windows informó que otro proceso tenía el archivo en uso. Sólo hubo dos consultas RPM y una decisión (`demand=30`, `smooth=30`), ninguna orden ni acceso EC directo, y ningún nuevo ACPI 13/15 registrado. El worker terminó con `stop-signal`; el resumen no retuvo lease ni finalización nativa desconocida. Se verificaron el ZIP y los 144 archivos del manifiesto. SHA-256: `a4df44e1dea3c43ea51149c2e63bd87cc53cbb6d60b732a288838245c73d9bcf`.

El lector anterior no permitía el reemplazo de un archivo abierto y un conflicto transitorio escapaba como excepción fatal. `WmiFanHeartbeatMonitor` usa la compartición de eliminación necesaria para el protocolo atómico y tolera únicamente ausencia de publicación o errores Windows 32/33 sin modificar el último progreso observado. No se toleran errores de permisos/disco, contenido malformado ni identidad incorrecta. Las pruebas Windows reproducen el bloqueo exclusivo de `3fdb9c`, verifican la recuperación de lectura tras soltarlo y exigen timeout si persiste, incluso antes del primer heartbeat. Otra prueba conserva un handle a la publicación anterior durante veinte reemplazos y comprueba que conserva su JSON original mientras el supervisor observa los nuevos contadores. Las pruebas de contador repetido/regresivo, desaparición, datos inválidos y límites de tiempo no acceden al hardware. La configuración de demanda final, admisión, recuperación y lease permanece igual.

La primera validación Windows de esta corrección detectó además que `File.Move(..., overwrite: true)` no podía sobrescribir un destino abierto aunque el lector compartiera eliminación. El publicador experimental ahora usa `File.Replace` al actualizar un archivo existente; ambas operaciones mantienen un único publicador por pathname, sin borrar el destino ni degradar a escritura parcial si fallan. Un error de publicación sigue deteniendo la operación dependiente, incluyendo el despacho tras una intención durable. Referencia del contrato Windows: [ReplaceFileW](https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-replacefilew).

Esta corrección requiere repetir la simulación física completa y revisar el ZIP antes de probar el control activo.

## Redondeo a décimas y retorno al mínimo

La captura activa `328e09`, ejecutada con `07a5e0b`, completó cinco minutos con 190 consultas RPM, 189 decisiones y tres órdenes normales `34 → 33 → 32`, todas con retorno cero. La recuperación `FF/FF → LegacyDefault` también retornó cero. No se registraron EC directo, nuevos ACPI 13/15, llamadas nativas desconocidas ni lease retenido; la edad RPM máxima fue 891 ms. Esto no verifica propiedad del firmware.

En esa captura la demanda original volvió a 30 y la EMA alcanzó `30,00000651003845`, pero la combinación del techo entero y la banda adicional de un nivel mantuvo 32. Una simulación de veinte minutos con demanda constante 30 retenía 32 por el residuo de coma flotante; con demanda 33,2 retenía 35. Incluso una demanda filtrada exactamente 30 retenía 31 por la banda.

La actuación normal ahora redondea a la décima más cercana, con empates hacia abajo: `3,05 → 3,0`, `3,06 → 3,1`, `3,15 → 3,1`. La aritmética decimal evita que la representación binaria altere los empates. Sólo se redondea la salida para actuar; la EMA conserva toda su precisión. Después se aplica el techo entero que requieren las órdenes HP: `30,05 → 30,0 → nivel 30` y `30,06 → 30,1 → nivel 31`. Se elimina la banda adicional de un nivel en este experimento, conservando la confirmación de doce segundos y el descenso de un nivel por cambio.

Las pruebas cubren empates pares e impares, valores a ambos lados del umbral, el residuo observado en `328e09`, retorno a 30 tras veinte minutos de demanda baja, demanda estable 33,2 que termina en nivel 34 y límites temporales de descenso. La excepción de calor alto conserva la demanda original y su techo entero, incluso si su fracción se perdería con el redondeo normal. La protección térmica y las RPM siguen usando las lecturas originales.

La reproducción offline de las 189 entradas de `328e09` con esta corrección produce seis objetivos `34 → 33 → 32 → 31 → 30 → 31`; alcanza el mínimo antes de la última ráfaga de carga. No envía órdenes de hardware ni predice las temperaturas/RPM con esa nueva ventilación. Se requiere una nueva simulación física completa antes de probar el control activo.

## Interpretación y límites

Una sesión de control sin ACPI 13/15 muestra que el episodio no se reprodujo en esa ventana; no demuestra que se haya eliminado. La variante cambia tanto el transporte de comprobación como el modo/ritmo de control y pausa M4, por lo que es una comparación diagnóstica, no causalidad aislada ni validación para uso diario. Si aparece un evento se detiene el control normal y se intenta la recuperación descrita. Si la finalización nativa es desconocida, no se envían llamadas potencialmente superpuestas: se conserva la evidencia y no se anuncia éxito.

Las RPM supervisan movimiento, no son ACK de consigna ni prueba de restauración. El mínimo 30 es provisional para la primera sesión, no una nueva curva definitiva. No se modifican los gates productivos ni se confiere a este arnés la robustez ya comprobada del watchdog productivo. Primero se revisa esta prueba y luego se decide la integración diaria.
