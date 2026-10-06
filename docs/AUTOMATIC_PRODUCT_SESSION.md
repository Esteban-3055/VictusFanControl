# Automatic: activación coordinada de curva y límites

**Contrato vigente de la candidata:** ver [PRODUCT_FINAL_CANDIDATE.md](PRODUCT_FINAL_CANDIDATE.md).
El inicio conserva CPU ≤90 °C; la confirmación activa comienza a 95 °C, con
2000 ms y retorno inmediato a 99 °C. Se conserva salida limpia/nueva apertura
tras interrupción. Las secciones de diagnóstico de abajo describen versiones
anteriores y sus pruebas; sus procedimientos y umbrales históricos no sustituyen
el contrato actual ni representan pendientes adicionales.

## Estado consolidado y revisión prolongada

Ver [PRODUCT_VALIDATION_STATUS.md](PRODUCT_VALIDATION_STATUS.md): conserva los
PASS físicos históricos y la transición de GUI f669831 AC → Batería → AC del
6 de octubre (203 muestras completas, misma activación, sin interrupción).
Los apartados históricos que dicen «pendiente» describen el estado de esa entrega;
no revierten pruebas aprobadas después. Guardian 6I/6J y P15D1/P15D2 ya están aprobados.

Se añade `-Mode AutomaticExtendedReview`: 45 min por activación, mismos límites
y protecciones que la revisión de 5 min. No promueve Automatic normal. Estado/ZIP
identifican la duración y el historial de carga; la entrada larga conserva/exporta
hasta 16 MiB por flujo para observar veinte minutos de carga y enfriamiento.

## Contrato de producto

Seleccionar Automatic es la autorización explícita para habilitar CPU y GPU con
los valores en edición de ambos perfiles. Se toma una copia congelada; se
habilitan ambos dominios para esa solicitud aunque sus interruptores estén
apagados. Los interruptores/preferencias guardados no se modifican: siguen
controlando el botón Aplicar independiente. La GUI muestra la configuración
aplicada por separado y anuncia «Curva + límites CPU y GPU».

Primero se comprueba destino, Firmware, telemetría completa Healthy/vigente y
fuente real conocida. Performance Guardian aplica CPU/GPU; se exige respuesta
vigente, sesión habilitada, CPU Active, GPU ActiveUnverified, configuración
idéntica y fuente coincidente. Solo entonces se configura la curva de la fuente
real y se inicia la revisión de 300 s. El inicio exige tres adquisiciones Healthy
con CPU ≤90 °C/60 W y GPU ≤82 °C/75 W. Una vez establecida, la CPU dispone
de la confirmación de picos acotada descrita abajo; GPU y potencia conservan
sus rechazos inmediatos.

Preparar Performance no retiene la cola de ventiladores ni detiene las lecturas.
Firmware invalida el intento desde el clic, antes de esperar una cola. Una
operación nativa ya iniciada puede terminar; una preparación cancelada no
inicia después la curva. Los límites aceptados conservan su sesión independiente
y su estado visible. No hay rollback atómico ni borrado de journals.

Si hay un Guardian vivo con la misma configuración y ambos dominios habilitados,
se consulta Status y se reutiliza sin Enable repetido. Con valores diferentes,
solo CPU/GPU parcial, estado no confirmado u owner no resuelto, no se reemplaza
ni se mata el proceso: se informa que debe liberarse la sesión anterior.

## Casos de aplicación y reaplicación

| Caso | Ventiladores | CPU/GPU |
|---|---|---|
| Inicio, reinicio, inicio minimizado, importación | Firmware; no entrada Automatic automática | No se aplican por estas acciones |
| Clic Automatic admitido, sin sesión Performance | Inicia después de confirmar límites para la fuente real | Ambos habilitados con la copia de AC/Batería en edición |
| Clic Automatic con una sesión idéntica y vigente | Inicia con la curva actual de la fuente real | Reutiliza la sesión; consulta Status, no reescribe Enable |
| Sesión existente distinta, parcial o no confirmada | Permanece Firmware | Rechaza el reemplazo; conserva owner y evidencia |
| Clic Automatic cuando ya está seleccionado | Continúa la sesión; no reinicia el reloj ni sustituye la curva | No reescribe ni cambia límites |
| Actualización Healthy durante Automatic | Evalúa demanda unificada e inercia; escribe solo un cambio de nivel | Comprueba estado vigente; no aplica límites por cada muestra |
| Seleccionar pestaña AC/Batería | Cambia solo el perfil que se edita | No cambia la fuente aplicada |
| Editar, guardar, descartar, importar curvas o límites | No modifica la curva congelada ya aplicada | No modifica una sesión viva |
| Aplicar en Curvas durante Automatic activo | Sustituye solo curva e influencias de la fuente activa en la siguiente adquisición; conserva nivel, EMA, carga y plazo de sesión; reinicia confirmaciones normales pendientes | No modifica límites ni escribe preferencias; exige CPU/GPU vigentes |
| Firmware limpio → editar → Automatic explícito | Admite otra curva; nuevo plazo de revisión | Reutiliza si idénticos; si se editaron límites exige liberar antes |
| Firmware durante la preparación | Cancela el intento; no inicia después Automatic | Una petición en vuelo puede completar; límites quedan visibles y liberables |
| Volver voluntariamente a Firmware o Manual | Cambia solo el control de fans | Conserva los límites activos |
| Liberar CPU/GPU desde Automatic | Vuelve primero a Firmware; si la liberación WMI falla no retira los límites | Se libera después, mediante su Guardian |
| Fuente cambia durante preparación | No inicia con una curva de la fuente anterior; requiere otro clic | Guardian mantiene su seguimiento de la fuente real |
| Fuente cambia con AutomaticReview activo | Conserva el nivel sin writes hasta confirmar fuente estable y respuesta CPU/GPU posterior; cambia curva e influencias sin reiniciar inercia ni plazo. Espera máxima 4 s; fallo o calor urgente pendiente vuelve a Firmware | Guardian conserva sus transiciones AC/Batería independientes; la GUI no emite Enable ni reescribe límites |
| Guardian falla, pierde vigencia o entra en recuperación durante Automatic | Interrumpe, solicita Firmware y bloquea reentrada | Conserva la recuperación propia; no reinicia ciegamente al Guardian |
| Telemetría perdida, margen excedido, suspensión/lifecycle, vencimiento | Interrumpe; Healthy posterior no rearma; reiniciar después de liberar limpio | No hay reaplicación por el estado Healthy de la GUI; Guardian conserva su ciclo propio |
| Minimizar u ocultar al tray con una sesión ya activa | Continúa con su supervisión y plazo existentes | Continúa con su Guardian existente |
| Cerrar explícitamente la aplicación | Intenta liberar fans | Intenta liberar Performance incluso si otro dominio falla |

El cambio de curva AC/Batería pasó el ensayo ligero de f669831. La integración
actual tras lifecycle y la respuesta prolongada siguen pendientes; la reentrada
automática y el arranque Automatic persistente no están habilitados.
Esta integración no abre el gate Automatic normal; usa exclusivamente
`Start-ProductGui.ps1 -Mode AutomaticReview` en 8C40/F.18 y niveles 10–50.

## Revisión continua AC/Batería — 2026-10-06

El diagnóstico `20261006T163422778Z-245aaeab54b8429e8c365f5840c75551`
valida detección de cambio de fuente y retorno protector a Firmware de la versión
anterior. El usuario confirma desconexión/reconexión. El estado final AC y los
statuses de presets CPU/GPU respaldan su seguimiento independiente, pero no hay
un estado Battery intermedio con evidencia suficiente de los límites efectivos.
El diagnóstico `20261006T163959090Z-28cde682f9524671b1511d131eebce2d`
registra cinco aplicaciones en vivo en una única activación AC, sin renovación
del plazo, y recuperación de CPU control 92→48 °C en 1.041 s sin interrupción.
No contiene cambio de fuente. Son resultados parciales, no cualificación general.

La nueva revisión permite transiciones continuas únicamente tras establecer
Custom. Exige dos lecturas de fuente iguales separadas al menos un segundo y una
respuesta Guardian distinta de la observada al detectar el cambio. CPU Active,
GPU ActiveUnverified, ambas habilitaciones, configuración exacta, fuente coincidente
y vigencia son obligatorias. Rebotes no renuevan el límite total de cuatro segundos.
Durante la espera no hay nuevas escrituras fans: se conserva el nivel y se observan
adquisiciones para conservar continuidad, EMA e historial de carga. No se pausa el
plazo de cinco minutos ni la confirmación térmica. CPU control ≥85 °C o GPU ≥78 °C
durante una espera no resuelta devuelve a Firmware; una transición confirmada puede
continuar con el override térmico habitual. Unknown, pérdida de vigencia, fallo
Performance, telemetría perdida o vencimiento mantienen su retorno protector.

Las curvas AC/Batería se congelan al clic inicial. Aplicar modifica también la copia
de esa fuente dentro de la sesión, por lo que se recupera al volver a ella. Guardar
por sí solo no cambia estas copias. El cambio exige Tuning idéntico entre fuentes;
no sustituye el motor ni la sesión. Aplicar se deshabilita durante la reconciliación.
Los eventos STARTED/WAIT/CONFIRMED incluyen ID de activación, fuente, snapshot y
respuesta Guardian; CONFIRMED añade configuraciones y segundos restantes. Esta
evidencia identifica solicitudes aceptadas, no demuestra rango NVML exacto ni
ownership físico del setpoint WMI. Firmware/lifecycle cancelan la transición.

Ensayo ligero, dentro de la misma revisión: mantener los límites usados en la última
prueba (AC 30/50 W, GPU 1800 MHz; Battery 9/12 W, GPU 1200 MHz), guardar ambas curvas
antes de activar, permanecer AC 30 s, desconectar 30 s, reconectar 30 s, exportar.
Si aparece Firmware/bloqueo, exportar inmediatamente sin rearmar. Cerrar con Salir
de la bandeja al terminar; no matar procesos ni borrar journals. Batería suficiente,
sin benchmark ni carga nueva durante este primer ensayo.

## Diagnóstico de interrupciones

El diagnóstico GUI (6), 2026-10-06 00:55:02 UTC, del paquete 4fc8ee3 demuestra
la secuencia Automatic 12/12 → Firmware limpio → Automatic 12/12. La lectura
aplazada a 21:52:41 Santiago no produjo Degraded ni bloqueó la reentrada.
Hubo 99 decisiones a nivel 12/13 y solo cinco solicitudes de nivel, incluidas
las dos entradas. A 21:54:20 la segunda activación se interrumpió por el margen
de revisión. CPU/GPU estaban Disabled al exportar. No hay RPM/temperatura
completos durante las dos sesiones ni la muestra del disparo, por lo que no se
declara estabilidad térmica, cualificación de batería o causa física concreta.

Ahora el mensaje identifica temperatura/potencia inválida o excedida, con valor
observado y umbral, o telemetría incompleta. `AutomaticInterruptionSnapshot`
conserva la muestra usada al interrumpir; no se reemplaza por la muestra Healthy
posterior. El export añade preparación, configuración Performance aplicada,
presencia del owner y estado activo. Esto es evidencia de software/solicitud,
no prueba independiente del rango NVML ni ownership WMI.
Los números no finitos se conservan como cadenas JSON `NaN`/`Infinity`;
no se convierten en muestras válidas ni impiden exportar la evidencia.

## Verificación y prueba física siguiente

Fixtures sin IO verifican orden de activación, configuración congelada con ambos
dominios, reutilización sin reescritura, rechazo de owner distinto/parcial/no
confirmado, fuente/vigencia, fallo parcial, cancelación antes y durante Apply,
compleción vieja que no borra un intento nuevo, y liberación de fans antes de
retirar límites. Los tests GUI conservan inicio Firmware y edición sin writes;
el ZIP debe preservar la muestra del disparo junto a telemetría posterior.

En el Victus, mantener AC y Firmware, seleccionar Automatic una vez y exportar
cuando se observen CPU Active, GPU ActiveUnverified y Automatic controlando la
curva. Volver a Firmware y reactivar para comprobar reutilización del Guardian.
Finalmente Liberar CPU/GPU debe dejar fans en Firmware y dominios Disabled.
El rechazo/timeout debe conservar sus informes y no fabricar una restauración.
Estas nuevas pruebas combinadas aún están pendientes de ejecución en el equipo.


## Sesiones, muestras y gráficos (6 de octubre de 2026)

Cada proceso GUI tiene `sessionId`, inicio UTC, PID y MVID de App/Core.
Los registros se guardan en `%LOCALAPPDATA%\VictusFanControl\logs\sessions\<sessionId>`:
`session.json`, `events.log`, `telemetry.jsonl`. Cada flujo rota a 5 MiB y retiene
un segmento anterior; el ZIP reúne hasta 2 MiB recientes por flujo, sin filas
parciales. El nombre sugerido del ZIP contiene el ID de sesión. No se recorren
logs de otras aperturas. No se eliminan diagnósticos anteriores ni journals.
Solo los harnesses explícitos de MainForm conservan además una copia diaria
para sus recolectores históricos. ProductForm no activa esa compatibilidad y
los ZIP siguen leyendo exclusivamente las rutas de sesión.
`telemetry-tail.jsonl` conserva las muestras originales, incluidos valores no
finitos representados como strings, sin lecturas adicionales de hardware.
Cada activación Automatic registra otro `automaticSessionId`, la configuración
congelada y los timestamps de decisión. `AutomaticDecisionSnapshot` enlaza la
solicitud con su muestra exacta; `AutomaticInterruptionSnapshot` conserva el disparo.

En los gráficos del control de ventiladores y del editor, el punto verde indica
el nivel común solicitado por Automatic, con el eje X correspondiente a la
muestra de esa decisión y su configuración aplicada. No representa RPM, lectura
independiente del setpoint ni ownership. El anillo amarillo indica la interpolación
de la curva en edición usando la entrada actual. Es demanda de ese eje, no el
MAX de seis curvas ni una orden de hardware. Mover nodos actualiza el anillo,
sin modificar la curva aplicada. Cambiar a otro perfil, Firmware, bloqueo o
caducidad elimina el punto de solicitud; datos caducos/ausentes/NaN eliminan la
vista previa. El selector térmico CPU sigue la configuración de cada curva.

El selector GPU permite 210–2500 MHz AC/Batería; el mínimo solicitado a NVML es
210 y el máximo es el valor elegido. No se fija el reloj a 2500 ni se cambian
preferencias existentes/defaults. Un rechazo NVML sigue cerrando la operación.

## Evidencia física reciente y validación pendiente

Diagnóstico (7), paquete a9001de: Automatic activa CPU/GPU y admite retorno
voluntario Firmware→Automatic sin bloqueo. Diagnóstico (8): CPU 91 °C dispara
el margen; al exportar fans en Firmware y Performance Disabled/sin owner.
Diagnóstico (9): una activación expira a 00:27:55 Santiago por los cinco minutos;
otra activa CPU 35/50 W y GPU 210–1800 MHz, pero a 00:32:21 CPU Package 97 °C
(core máximo 94 °C, potencia 37.51 W) produce retorno Firmware aceptado a
00:32:22. GPU 73 °C/60.90 W y fans 3500 RPM al disparar. Esto refuta que los
límites de potencia/reloj garanticen por sí mismos ausencia de picos térmicos.
Los límites CPU/GPU seguían activos al exportar (9).

La validación completa NO se declara PASS: faltan estabilidad bajo carga y
transiciones de curva AC/Batería/lifecycle. El inicio conserva CPU ≤90 °C; la revisión activa incorpora la confirmación
temporal descrita abajo. GPU 82 °C, CPU 60 W, GPU 75 W y la ventana de 300 s
conservan sus límites.
El gate normal Automatic permanece cerrado. Las nuevas muestras por sesión
permiten reconstruir el historial previo al disparo sin mezclar aperturas.

Prueba siguiente agrupada: abrir el paquete nuevo en AutomaticReview, mantener
AC, conservar inicialmente GPU 1800 (o el valor previamente utilizado),
seleccionar Automatic y observar 60–90 s con el uso que se desea evaluar.
Comprobar los puntos en control/editor; editar un nodo y verificar que cambia
solo la vista previa; descartar la edición. Exportar antes de los cinco minutos
(o inmediatamente al interrumpirse), luego Liberar CPU/GPU y exportar otra vez.
Registrar la carga/programa usado. El mismo ID de proceso une ambos ZIP y los
IDs de activación separan los tramos Automatic. La ampliación GPU puede probarse
por separado con Apply explícito en Firmware, incluyendo liberar/reset, sin
combinarla con cambios de curva durante la prueba térmica.


## Corrección de cancelaciones por picos CPU (6 de octubre de 2026)

Sesión `20261006T040723439Z-31eef8824c4d4b94840eebeb8a7e4dad`, paquete
4045d88: CPU 63 °C a 04:08:47.359 UTC, 96 °C a 04:08:48.723, 62 °C a
04:08:50.821. La lectura de 96 °C (core máximo 93 °C, CPU 29.97 W,
GPU 58 °C/23.53 W, fans 1900/1800 RPM) anuló la revisión por su veto
instantáneo de 90 °C, antes de llegar al controlador. CPU 35/50 W y GPU
210–1800 MHz seguían activos. La siguiente muestra ocurrió ya en Firmware
2.098 s después: no prueba que una repetición dentro de 2 s habría recuperado,
ni permite asegurar que la nueva versión evitará todo retorno.

La entrada producto explícita ahora usa el resultado efectivo de la admisión
8C40 existente, conservando el detector raw SafetyGate sin cambios. Tras tres
muestras iniciales ≤90 °C, una adquisición CPU >90 y <99 °C abre una ventana
monotónica máxima de 2000 ms. No se reinicia con otra muestra caliente ni con
previews/repintados. Solo una adquisición nueva, completa, Healthy y vigente
≤90 °C, recibida antes del plazo, la cancela. Una muestra fresca a tiempo
mantiene Automatic; un plazo vencido rechaza incluso una muestra fría tardía.
CPU ≥99 °C (package o cualquier núcleo) solicita Firmware inmediatamente.
La confirmación compartida CPU ≥95 °C conserva además su veto tras cinco
adquisiciones únicas consecutivas y su plazo de 2000 ms; ninguna capa amplía
el presupuesto de la otra. GPU >82 °C, CPU >60 W, GPU >75 W, datos inválidos,
pérdida de límites/owner, fuente y lifecycle siguen interrumpiendo sin espera.

El worker no añade su pausa normal durante un pico: solicita lectura fresca
secuencial con el menor presupuesto restante entre ambas capas. Si la adquisición
no completa a tiempo, la ruta de fallo de telemetría cierra la sesión. El plazo
se comprueba antes de cada despacho; no cancela una llamada de firmware ya
iniciada ni garantiza latencia física de restauración.

La revisión producto usa MAX(package, núcleo más caliente) para activar la
respuesta térmica, separado del promedio P-Core configurable para demanda.
A CPU ≥85 °C o GPU ≥78 °C (o umbral de respuesta más bajo configurado), el
objetivo de respuesta es al menos nivel 44, limitado por el máximo configurado.
Omite EMA y confirmación normal, pero conserva el paso protegido de hasta
4 niveles por decisión y la bajada gradual. No salta directamente de 19 a 44.
La curva en edición y sus preferencias no se reescriben. Este comportamiento
se habilita solo para AutomaticReview; la ruta de cualificación histórica y
el gate Automatic normal permanecen reproducibles. El simulador representa
las seis señales de demanda, no la confirmación de seguridad de sensores raw.

Cada inicio/recuperación de pico registra `PRODUCT AUTOMATIC CPU SPIKE` con
ID de activación, timestamp, CPU raw, estado y presupuesto. El estado exporta
`AutomaticCpuSpikeRemainingMilliseconds`; las muestras originales y el disparo
permanecen en los registros de sesión. Las pruebas sin hardware cubren inicio
caliente, recuperación, núcleo excluido del promedio, repetición/preview,
calor sostenido, frío tardío, 99 °C inmediato, presupuesto mínimo y respuesta
real del controlador con backend simulado. No constituyen validación física.

Ensayo siguiente: conservar AC y los límites 35/50 W y GPU 1800 MHz utilizados,
activar una vez, repetir el uso habitual durante 60–90 s y exportar dentro del
plazo de cinco minutos. Si vuelve a Firmware, exportar en ese momento; el motivo
distinguirá umbral crítico, confirmación vencida o pérdida de adquisición.
No combinar este ensayo con un cambio de curva o ampliación de reloj GPU.


## Ensayo de recuperación y protección crítica — sesión 04:30 UTC

El diagnóstico de sesión `20261006T043042114Z-e618184c52d74340b150aeb14ac66ce5`
corresponde al paquete 4945206. Automatic se activó a 01:30:53.649 Santiago;
registró 163 decisiones y se interrumpió a 01:34:56.116, tras 242.47 s.
Tres picos iniciaron confirmación y recuperaron sin abandonar Automatic:

| Hora Santiago | CPU control | Recuperación siguiente | Tiempo entre muestras |
|---|---:|---:|---:|
| 01:31:19.061 | 91 °C (package 83, core 91) | 87 °C | 0.998 s |
| 01:33:18.391 | 91 °C | 72 °C | 1.095 s |
| 01:33:25.401 | 93 °C | 79 °C | 1.088 s |

Hubo seis decisiones de respuesta térmica raw, incluidas subidas 13→17→21,
35→39→43 y 42→46. Los RPM observados durante el tramo llegaron a 4600/4600.
La igualdad de la solicitud no garantiza igualdad exacta de cada lectura RPM.

La muestra final a 01:34:56.113 reporta package 98 °C y un P-Core a 100 °C:
CPU control=100 °C, por encima del retorno inmediato de 99 °C. CPU 24.77 W,
GPU 74 °C/57.34 W, fans 3700/3700 RPM. Performance seguía en CPU Active/GPU
ActiveUnverified con CPU 35/50 W y GPU 210–1800 MHz. Firmware fue aceptado
a 01:34:57.170 (~1.05 s después del registro de interrupción). La siguiente
muestra, ya en Firmware, CPU 72 °C, llegó 2.066 s después del disparo.
No se atribuye causalidad física al consumo de esa muestra ni se concluye que
los límites eviten picos críticos. No hubo vencimiento de los cinco minutos.

El comportamiento de interrupción crítica es el previsto. El mensaje
`Prueba Automatic requiere SafetyGate vigente y Healthy` ocultaba el motivo:
la admisión efectiva ya había cerrado por temperatura, aunque Runtime siguiera
Healthy. Se corrige para identificar CPU control, package, núcleo máximo,
umbral y retorno inmediato. Los rechazos por epoch/orden se identifican por
separado; los demás rechazos conservan las Reasons originales de SafetyGate.
Un pico CPU admitido no se etiqueta como causa de un rechazo independiente
GPU/potencia. No se cambian temperaturas, plazos, curvas, límites ni gates.

Resultado físico parcial: recuperaciones CPU 91–93 °C observadas y retorno
crítico por core 100 °C observado. No es una cualificación completa: el tramo
no contiene recuperaciones CPU 95–98 °C con SafetyGate raw térmico, ni pruebas
Battery/transición/lifecycle. El estado al exportar es Healthy/Firmware con
bloqueo de revisión y límites aún activos. No se cambia ese bloqueo a reentrada
automática ni se declara estabilidad térmica general.
