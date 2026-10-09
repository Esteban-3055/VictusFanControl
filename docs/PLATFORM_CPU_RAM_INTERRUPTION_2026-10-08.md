# Interrupción al iniciar CPU + RAM: HP 8C40

## Conclusión

La captura instrumentada de 23:46 UTC identifica el primer disparo: el
controlador vio un salto de 3,1056265 s entre épocas de DTT3, aunque el dato
actual tenía solo 0,3213443 s de edad. La lectura principal estaba completa,
con CPU al 100 %, 64 °C y 29,049 W. La interrupción inicial no fue térmica
ni el watchdog principal. Este último apareció después, durante el procesamiento
que libera los controles tras el rechazo de DTT3.

El código conservaba ocho adquisiciones auxiliares, pero evaluaba solo la última
seleccionada por cada snapshot principal. Se corrige ese consumo para comprobar
las adquisiciones intermedias reales, conservar sus timestamps y registrarlas
junto al snapshot. La captura antigua no guarda esos valores intermedios; no
permite asegurar que hubiera un puente DTT3 válido en esa ejecución. El cambio
resuelve el caso de submuestreo demostrado en una reproducción sintética, sin
aceptar huecos reales ni ampliar frescura. Falta comprobarlo en el equipo.

Las capturas anteriores no tenían marcadores suficientes para identificar su
primera fase de bloqueo. No se les atribuye retrospectivamente la misma causa,
ni se concluye que OCCT, WMI o saturación de CPU fueran responsables.

## Captura instrumentada: 23:46 UTC

- ZIP: `Victus-Platform-physical-20261008T234633Z-bac746c3.zip`.
- SHA-256: `198f31abb3a32f034ab471d0492cf7fc2df57ccb821ca380b38bdafa6e8e9360`.
- Build: `d69331437fbbed2785e94edf7ef53534fba3f0a6`.
- CPU PL1/PL2 25/30 W; GPU máxima 1950 MHz.
- Auditoría independiente PASS: 214 snapshots, 212 cualificados, 93 decisiones
  y 93 resultados; cinco cambios aceptados. Solo A1/baseline.
- Máxima registrada CPU 79 °C; última CPU 64 °C, GPU 48 °C.
- El ZIP no acredita configuración, cantidad de hilos o prioridad de OCCT.

| UTC | Evidencia |
|---|---|
| 23:52:16.6505354 | Snapshot anterior; DTT3 seleccionado con época 23:52:14.9027984. |
| 23:52:18.3297692 | Snapshot completo; CPU 100 %, 64 °C, 29,0493348609 W. DTT3 51 °C, época 23:52:18.0084249; edad 0,3213443 s. |
| 23:52:18.334938 | Experimento cierra por fuente TZ01/DTT3 no disponible/requalificando. |
| 23:52:18.343391 | Comienza restauración de ventiladores. |
| 23:52:21.7634927 | Watchdog de 3422 ms; worker `SnapshotProcessor`, reader `SnapshotComplete`, ambos con edad 3422 ms. |
| 23:52:26.9126206 | Liberación Firmware aceptada. |
| 23:52:28.1709408 | Cleanup succeeded=true, failure=null. |

El hueco entre snapshots principales fue 1,6792338 s. TZ01 tenía 1,3481696 s
de edad y un salto entre épocas de 2,0776401 s; cumplía ambas barreras. El salto
DTT3 de 3,1056265 s excedía la continuidad de canal de 3 s. Los dos valores
actuales eran válidos y frescos. El watchdog posterior no demuestra que el
lector nativo estuviera bloqueado cuando se produjo el rechazo inicial.

Los registros de salud muestran tiempos de consultas/pending, pero no la época
y valor de cada DTT3 intermedio. Un pending recién encolado con queue=0 en
varios polls no prueba que sea la misma consulta ni una espera de varios segundos.
Las escrituras WMI registradas de 1344 y 1156 ms tampoco fueron el primer disparo.

## Corrección y comprobación de software

La admisión física recibe secuencias inmutables de adquisiciones por snapshot, con hasta ocho
adquisiciones TZ01/DTT3 cuya época no es posterior a ese snapshot. Exige que el
último registro coincida con el valor seleccionado; comprueba orden, validez,
mutación de época y continuidad de las nuevas adquisiciones. No ordena muestras,
no inventa puentes y no renueva la edad del caché. Sin estado previo, solo
adquisiciones actualmente frescas pueden establecer cualificación.

Se mantienen edad estrictamente menor que 3 s, continuidad real máxima de 3 s,
dos adquisiciones separadas al menos 1 s, continuidad principal y comprobación
justo antes de dispatch. También quedan intactos curva, MAX, retención, límites
térmicos, watchdog y Automatic normal. El replay archivado sin historial conserva
exactamente su admisión anterior. La traza nueva identifica el esquema
`bounded-acquisition-history-v1`; su auditor exige los historiales presentes.

La reproducción ABBA incluye un snapshot omitido con una adquisición auxiliar
real intermedia: el salto observado DTT3 es 3,1056265 s. Los controles negativos
eliminan ese puente y alteran épocas, valores, orden y presencia de historiales;
deben rechazarse. La validación de software no acredita que el equipo produzca
esas adquisiciones ni demuestra beneficio térmico de los sensores.

Comprobación local de la corrección: compilación Roslyn de observer/GUI;
9283 checks de política, 8657 de experimento físico sintético, auditoría de
2579 snapshots / 2159 decisiones y resultados, y 14 corrupciones rechazadas.
Replay reconciliado: 323876 filas / 84 ejecuciones; baseline original intacto.
La captura de usuario sin histories también conserva su resultado auditado.
La verificación Windows corresponde al CI del commit que contiene este cambio.

## Procedencia y comprobaciones

- ZIP: `Victus-Platform-physical-20261008T224423Z-ee3ad5ff.zip`.
- SHA-256 ZIP: `5eac5528aa87d33700eda24d9463554aa9886f10a4929c6de477ea56a591c0ad`.
- Build: `2de48a17d780b70693b017a2af9b9de94005aefc`.
- Selección efectiva: CPU PL1 30 W / PL2 45 W; GPU máxima 1950 MHz.
  No es la selección propuesta 25/30 W y 1900 MHz.
- Auditoría independiente: PASS; manifest, perfiles, fuentes y demanda MAX
  reconciliados; 212 snapshots, 211 cualificados, 94 decisiones, 93 resultados.
- Seis cambios aceptados; no request aceptado del último objetivo 22/22.
- Solo hubo control baseline/A1. No se alcanzaron los bloques B.
- Máximas registradas CPU 87 °C y GPU 48 °C. La máxima CPU fue durante
  el preflight Firmware; la última adquisición de control mostró 86 °C.
- No hay muestras para conocer la temperatura durante el intervalo sin lecturas.

## Cronología (UTC)

| Instante | Evidencia |
|---|---|
| 22:48:54.4062505 | Snapshot completo; CPU control 58 °C; decisión HoldCustom 18. |
| 22:48:55.7723421 | Último snapshot; CPU 86 °C; decisión objetivo 22, sin dispatch-result posterior. |
| 22:48:58.4973778 | Autoridad cambia de Custom a Restoring. |
| 22:48:59.2254697 | Interrupción: watchdog con 3453 ms sin lectura completada. |
| 22:49:00.0158676 | Recovery solicitado por watchdog de frescura. |
| 22:49:05.8028976 | Requests de liberación aceptados; autoridad Firmware. |
| 22:49:05.8121672 | Mensaje posterior: Experimental stage/source/dispatch epoch lost. |
| 22:49:07.3208307 | Cleanup succeeded=true; sin fallo reportado; protocolo incompleto. |

La última pareja auxiliar usada se consultó a 22:48:54.651 UTC. Luego los
registros de salud muestran TZ01 y DTT pendientes simultáneamente durante varios
segundos. No hay errores de consulta publicados. Ese patrón es compatible con
un retraso compartido de ejecución o de lecturas, pero no identifica su causa.

El error genérico de dispatch es posterior al disparo del watchdog y no debe
interpretarse como diagnóstico de origen. Las barreras impidieron continuar
con una adquisición que ya no cumplía las condiciones de frescura.

## Liberación y alcance de la evidencia

El diagnóstico GUI se exporta antes de `ProductRuntime.DisposeAsync`. Por eso
su CPU Active / GPU ActiveUnverified es una fotografía intermedia, no el estado
final. El resumen posterior indica cleanup sin excepción. La ruta Dispose
intenta liberar ventiladores, cerrar Performance Guardian y terminar el worker;
no se prueba de forma independiente un rango GPU exacto ni ownership físico.
La aceptación de release no equivale a una medición de finalización mecánica.

Esta ejecución sirve para diagnosticar continuidad bajo el inicio de la carga.
No permite comparar la curva actual con TZ01/DTT3 ni evaluar CPU sostenida.

## Siguiente prueba propuesta

Conservar todas las protecciones y usar 25/30 W y 1900 MHz. Verificar la
configuración real de OCCT antes de repetir. Como aislamiento inicial, seleccionar
CPU + RAM con carga constante, Normal/SSE y 10 hilos en lugar de Auto, si la
versión ofrece esos campos; no alterar la afinidad del controlador. Esto reduce
la competencia de CPU como hipótesis de trabajo, sin demostrar que fuera la causa.
Primero basta observar un minuto efectivo de la primera fase de carga y detener
normalmente ambos programas, exportando evidencia. Si vuelve a fallar, no repetir
el ABBA completo: hace falta instrumentar tiempos de lectura/procesamiento y
verificar configuración/prioridad de OCCT antes de elegir una corrección.

No se cambia el límite de frescura ni se promueve Automatic normal. No se
publica un nuevo paquete ni se modifica código de control por esta auditoría.

## Repetición corta: 22:56 UTC

La comprobación corta volvió a interrumpirse por el mismo watchdog. No procede
repetir la misma carga con este paquete para obtener el mismo mensaje genérico.

- ZIP: `Victus-Platform-physical-20261008T225623Z-21f15b65.zip`.
- SHA-256 ZIP: `12d394b1d27a72f4dffb6a3d38d358fba911ef1841632e6e1afa37565751e559`.
- SHA-256 JSONL: `84c6b0adfc647104f481049ba0731c8a9b9314924143c28f2f82d8f3b911082d`.
- Mismo build 2de48a1; selección CPU 25/30 W, GPU 1950 MHz.
  El ZIP no confirma cantidad de hilos, instrucciones ni prioridad de OCCT.
- Auditoría independiente PASS: 204 snapshots, 203 auxiliares cualificados,
  85 decisiones y 85 resultados; siete cambios aceptados.
- Primer snapshot 22:56:59.1888571 UTC; último 23:01:00.6139835 UTC.
- Inicio A1 22:58:56.8078394 UTC; no llegó a los bloques B.
- Última lectura: CPU 85 °C, GPU 48 °C, CPU 19,265 W, carga CPU 16,019 %.
  Esa carga es diferencial y cubre el intervalo anterior; no representa
  necesariamente la carga después de arrancar OCCT.
- Request 23/23 aceptado a 23:01:00.9795911 UTC por override de CPU >=85 °C.
- Watchdog: 3282 ms sin lectura completada, disparo a 23:01:03.9072504 UTC.
- DTT quedó pendiente en varios registros de salud. No hubo error publicado
  ni lectura posterior con la que identificar la fase que se retrasó.
- Cleanup succeeded=true a 23:01:09.083465 UTC; fallo null; proceso 173 por
  interrupción del protocolo, no por un cleanup fallido.

La máxima de 98 °C ocurrió a 22:57:25.6393942 UTC, en preflight Firmware,
antes de activar A1 y sus límites. Package 96 °C; núcleo P de índice 3 a
98 °C; carga agregada 6,816 % y potencia media 12,954 W. No se interpreta
como temperatura de CPU bajo 25/30 W ni como una lectura falsa. Es un pico
registrado y requiere distinguir instante térmico de potencia/carga promediadas.
El disparo final fue el watchdog, con una última lectura de 85 °C; las
temperaturas posteriores durante el hueco no están disponibles.

## Instrumentación local tras la repetición

Se añadieron marcadores de fase monotónicos y visibles sin adquirir el lock
de lectura. Al dispararse el watchdog, su registro incluye fase/edad del worker
(espera, lectura, procesamiento o publicación) y fase/edad del reader (prueba
HP-WMI, package/power Intel, núcleos, carga Windows, NVML, fans o finalización).
La captura se toma antes del cambio a Degraded; el log adicional se emite después
de solicitar recovery para no interponerlo en esa ruta de seguridad.

Los auxiliares ahora distinguen tiempo en cola, inicio de la función de lectura
y duración de esa función. Se incluyen en source-health y terminal-sources.
Es progreso observado, no una prueba de que todo ese tiempo ocurra dentro de
una llamada nativa: el scheduler y el código de lectura también pueden influir.
No hay callbacks de logging dentro de las consultas nativas ni nuevas consultas.
Los marcadores no renuevan frescura, no cambian polling/admission, ni amplían
los límites térmicos o el watchdog. No se modifica la curva.

Validación local: compilación Roslyn de Core, GUI y OEM Capture; self-tests
OEM Capture (incluyendo lectura falsa bloqueada, duración visible, única admisión
e invalidación de epoch), 9236 checks de política y 8661 del experimento; suite
de telemetría/prueba HP-WMI PASS. Ambas evidencias reales pasaron la auditoría.
No se declara validación física del cambio ni un paquete Windows publicado.
El origen del retraso sigue pendiente; esto prepara un diagnóstico útil, no
una corrección demostrada. No se solicita otra prueba física igual ahora.
