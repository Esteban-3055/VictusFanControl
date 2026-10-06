# Automatic: activación coordinada de curva y límites

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
real y se inicia la revisión de 300 s. Sus tres adquisiciones Healthy y márgenes
CPU ≤90 °C/60 W, GPU ≤82 °C/75 W no se amplían.

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
| Actualización Healthy durante Automatic | Evalúa las seis curvas e inercia; escribe solo un cambio de nivel | Comprueba estado vigente; no aplica límites por cada muestra |
| Seleccionar pestaña AC/Batería | Cambia solo el perfil que se edita | No cambia la fuente aplicada |
| Editar, guardar, descartar, importar curvas o límites | No modifica la curva congelada ya aplicada | No modifica una sesión viva |
| Firmware limpio → editar → Automatic explícito | Admite otra curva; nuevo plazo de revisión | Reutiliza si idénticos; si se editaron límites exige liberar antes |
| Firmware durante la preparación | Cancela el intento; no inicia después Automatic | Una petición en vuelo puede completar; límites quedan visibles y liberables |
| Volver voluntariamente a Firmware o Manual | Cambia solo el control de fans | Conserva los límites activos |
| Liberar CPU/GPU desde Automatic | Vuelve primero a Firmware; si la liberación WMI falla no retira los límites | Se libera después, mediante su Guardian |
| Fuente cambia durante preparación | No inicia con una curva de la fuente anterior; requiere otro clic | Guardian mantiene su seguimiento de la fuente real |
| Fuente cambia con Automatic activo | Solicita Firmware y bloquea la revisión; no aplica automáticamente la otra curva | Guardian conserva sus transiciones AC/Batería independientes |
| Guardian falla, pierde vigencia o entra en recuperación durante Automatic | Interrumpe, solicita Firmware y bloquea reentrada | Conserva la recuperación propia; no reinicia ciegamente al Guardian |
| Telemetría perdida, margen excedido, suspensión/lifecycle, vencimiento | Interrumpe; Healthy posterior no rearma; reiniciar después de liberar limpio | No hay reaplicación por el estado Healthy de la GUI; Guardian conserva su ciclo propio |
| Minimizar u ocultar al tray con una sesión ya activa | Continúa con su supervisión y plazo existentes | Continúa con su Guardian existente |
| Cerrar explícitamente la aplicación | Intenta liberar fans | Intenta liberar Performance incluso si otro dominio falla |

El cambio automático de curva por AC/Batería, la reentrada tras lifecycle/fallo
y el arranque Automatic persistente siguen pendientes de cualificación física.
Esta integración no abre el gate Automatic normal; usa exclusivamente
`Start-ProductGui.ps1 -Mode AutomaticReview` en 8C40/F.18 y niveles 10–50.

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
transiciones de curva AC/Batería/lifecycle. Se conserva el margen CPU 90 °C,
GPU 82 °C, CPU 60 W y GPU 75 W de esta revisión, y su ventana de 300 s.
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
