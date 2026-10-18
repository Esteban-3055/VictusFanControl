# Curva Externa — extensión compatible con protocolo v1

El sobre, HMAC SHA256 direccional sobre JSON crudo, autenticación mutua,
sesión, secuencias, límite 4096 bytes y lease de 2000 ms siguen el
[contrato v1](EXTERNAL-COOLING-PROTOCOL.md). Las claves no viajan por la red.
No hay cifrado del contenido: HMAC autentica y detecta modificaciones.
Los dos slots de `extensions` siguen nulos e inertes.

El dispositivo anuncia `externalCurveVersion:1` en `deviceStatus` firmado.
Windows añade **solo después de ese anuncio** `externalConfiguration` a la
telemetría firmada (sin crear nuevos tipos de mensaje). Firmware anterior
sigue aceptando la telemetría anterior; cliente anterior ignora campos nuevos.
La interfaz indica firmware incompatible si falta la capacidad. No presenta
una curva como aplicada por el simple hecho de haber guardado preferencias.

Ejemplo de configuración (todos sus campos son obligatorios si se incluye):

```json
{
  "schemaVersion":1,
  "revision":"0123456789abcdef0123456789abcdef",
  "points":[{"input":0,"output":30},{"input":50,"output":130},{"input":100,"output":255}],
  "input":0,
  "requestMinimum":0,
  "requestMaximum":54,
  "manualOutput":130,
  "rise":12,
  "fall":6,
  "filterMs":1500,
  "hysteresis":3,
  "downHoldMs":3000,
  "startMs":800,
  "startOutput":255
}
```

| Campo | Regla/unidad |
|---|---|
| revision | 32 caracteres hexadecimales minúsculos; nueva revisión por guardado |
| points | 2–16 puntos, extremos X=0/100, X enteros estrictamente crecientes, Y enteros 0–255 no decrecientes |
| input | 0 = Demanda del notebook; 1 = Solicitud aceptada normalizada por calibración explícita |
| requestMinimum / Maximum | 0≤mínimo<máximo≤65535; 0/54 es calibración inicial, no límite permanente del protocolo |
| manualOutput | 0–255, salida externa independiente en modo Manual cuando input=0 |
| rise / fall | 1–100 % de escala completa por segundo; 12/6 iniciales |
| filterMs | 0–10000 ms; constante del filtro EMA (0 desactiva) |
| hysteresis | 0–30 niveles externos |
| downHoldMs | 0–30000 ms, confirmación sostenida de descenso |
| startMs / Output | 0–3000 ms; 0–255; arranque al encender y al salir de parada real, no por cada reconexión |

El receptor valida la configuración completa antes de modificar el estado.
Dentro de una sesión, reutilizar una revisión con contenido distinto se rechaza.
Aplicar repetidamente la misma revisión no reinicia filtros ni arranque.
La curva se interpola linealmente **en el ESP32**. Windows no convierte la
Solicitud del notebook en PWM. Los Y son la Solicitud **externa**, antes de
suavizado y de la inversión física; no son niveles WMI ni RPM.

Requisitos para Remote: autenticación, Solicitud aceptada vigente, referencia a
respuesta enviada hace menos de 2000 ms, evidencia térmica y modos válidos.
Guardar curva en monitorización no concede autoridad. Firmware, transición,
interrupción y falta de Solicitud regresan a Local. Con entrada Demanda,
Automatic usa demanda 0–100; Manual usa manualOutput. Con entrada Solicitud,
un valor fuera de la calibración invalida Remote, sin saturarlo silenciosamente.

El dispositivo responde además:

```json
{"externalCurveVersion":1,"appliedConfigRevision":"0123456789abcdef0123456789abcdef","externalRequest":130,"appliedDuty":142}
```

`appliedConfigRevision` es nulo hasta que la tarea de control ha aceptado la
configuración, y no significa que esté habilitada la salida ni que exista giro.
`externalRequest`/`appliedDuty` son 0–255 o nulos (salida deshabilitada).
`appliedPwmPct` se conserva como porcentaje 0–100 o nulo. La revisión se confirma
en una respuesta posterior, nunca antes de entregar la configuración al control.
Windows verifica ACK y antigüedad, y muestra datos recientes. Al abrir una sesión
nueva se negocia capacidad de nuevo y se reenvía la configuración guardada.

Local tiene objetivo fijo **130/255**, por petición del usuario, y lo alcanza
mediante EMA, histéresis, confirmación y rampas configuradas, asentándose en 130
exacto desde arriba o abajo. No hay piso ni máximo automático por NTC. Ambiente
es solo una lectura: alto, inválido o vencido no cambia PWM ni retira Remote.
El software arranca en salida lógica cero, sin pulso Local. Un pulso configurado
solo puede activarse con una orden remota positiva; Máxima suavidad lo desactiva.
Esta política requiere firmware actualizado; las versiones anteriores tenían
fallback ambiental. El formato de configuración, autenticación y NVS no cambian.
La salida lógica siempre permanece en **0–255**. GPIO3 utiliza una etapa
NOT (2N7000 → BC337): el valor eléctrico conceptual es **255 − salida**,
en escala de 8 bits. El hardware LEDC del ESP32-C3 funciona a **18 Hz con
resolución de 14 bits**, por lo que el firmware convierte el duty eléctrico
mediante `round((255 − salida) × 16383 / 255)`. Esa conversión no altera
el contrato del protocolo, la curva editable, los estados ni la realimentación
lógica de 0–255. La combinación anterior de 18 Hz / 8 bits falló al
configurar el temporizador y provocó un reinicio antes de Wi-Fi.
Caducidad por reloj monotónico con rollover de millis soportado.

La configuración validada se conserva en RAM y en una entrada NVS versionada
con comprobación CRC de NVS y rangos al cargar. Las escrituras se limitan a una
cada 30 s; un corte antes del siguiente guardado puede recuperar la revisión
anterior hasta reconectar Windows. Se escribe fuera de la tarea de control.
La clave de emparejamiento persiste de forma independiente. NVS y firmware con
config.local.h no equivalen a almacenamiento cifrado: para un producto final
debe revisarse flash encryption y el proceso de provisión.

Esta especificación está probada por software. Arranque, estabilidad mecánica,
ruido, frecuencia real y disipación de transistores necesitan pruebas físicas.

## Dónde se ejecutan histéresis, filtros y rampas

El controlador real es `firmware/esp32-c3/lib/VictusProtocol/ExternalController.cpp`.
`controlTask()` en `src/main.cpp` lo llama cada 20 ms, independiente de TCP/LCD:

1. `receive()` interpola la curva externa con Demanda, o normaliza la Solicitud
   interna aceptada según calibración; con entrada Demanda y modo Manual usa
   `manualOutput`. La NTC no interviene en el actuador.
2. `tick()` aplica EMA **a la salida externa deseada**, no directamente a las
   temperaturas CPU/GPU: `filtered += (desired-filtered) * dt/(filterMs/1000+dt)`.
   `filterMs=0` equivale a aplicación directa. `dt` se limita a 0,1 s.
3. La histéresis compara esa salida filtrada con `target`, en **niveles lógicos
   externos 0–255**. Se actualiza al alcanzar ±`hysteresis`; los descensos exigen
   permanecer debajo del umbral durante `downHoldMs`.
4. El duty aplicado avanza hacia `target` como máximo `rise*2,55*dt` al subir y
   `fall*2,55*dt` al bajar (porcentajes de escala completa por segundo). El pulso
   `startOutput`/`startMs` solo ocurre con orden remota positiva al partir de cero;
   no por renovar el lease, arrancar en Local o reconectar en marcha.
5. Un lease vencido, retirada de autoridad, Firmware, calibración inválida o
   desconexión lleva a Local, objetivo 130, a través de los mismos filtros y
   rampas. La NTC no modifica el objetivo ni la autoridad. El lease sigue en
   2000 ms; no se aceptan órdenes antiguas o no autenticadas.
6. Finalmente se invierte el duty y se escala a LEDC 14 bits / 18 Hz en GPIO3.

Hay **otro EMA ambiental** (`AmbientFilter`), aplicado a la lectura NTC: factor
0,25 por muestra (lectura cada segundo). Una muestra inválida lo borra de inmediato;
una lectura ≥45 °C lo atraviesa sin suavizado. Es distinto de `filterMs` de la curva.

Windows presenta los controles en Curva Externa → Respuesta y arranque
(`ExternalCurvePage.cs` / `ProductCanvas.ExternalCurve.cs`). Valida y guarda
`ExternalCurveConfiguration`, con nueva revisión, en sus preferencias.
`Esp32TcpClient.CommunicateAsync()` captura telemetría cada segundo y, después del
anuncio de capacidad `externalCurveVersion=1`, añade `externalConfiguration` al
payload autenticado con HMAC. El ESP32 vuelve a validar y lo entrega por cola a
la tarea de control; `appliedConfigRevision` confirma después su aplicación.
Guardar en Windows y recibir esa confirmación son estados diferentes. El ESP32
conserva `curve-v1` en el mismo namespace NVS `victus-base`; el emparejamiento
`ssid`/`password`/`pairing`/`valid` mantiene su esquema.

La **curva de ventiladores internos HP** y sus propias histéresis/filtros/rampas
se ejecutan en Windows con su supervisor y backend HP/WMI. Producen la Demanda y
la Solicitud aceptada que puede usar la base; no son los ajustes de Curva Externa.
El ESP32 nunca escribe niveles HP ni cambia la seguridad del notebook. Tampoco
el mero guardado de la curva externa concede autoridad Remote.

### Máxima suavidad, sin cambiar el contrato

El botón optativo de Windows prepara `rise=1`, `fall=1`, `filterMs=10000`,
`hysteresis=3`, `downHoldMs=30000`, `startMs=0`, `startOutput=0`.
Conserva puntos, entrada, calibración y salida Manual; solo se envía tras guardar
con revisión nueva. Utiliza los límites ya admitidos por schemaVersion 1,
incluyendo la negociación existente de salida mínima cero. No cambia defaults
ni preferencias instaladas, el payload, la NVS ni el controlador de placa.

Ambas rampas normales se limitan a 2,55 unidades lógicas por segundo (1 % de
escala completa). Un recorrido completo tarda al menos 100 s; filtros y
confirmación pueden alargarlo. El objetivo sigue representando la curva antes
del suavizado. Desactivar el pulso evita el salto a 255 al abandonar una parada;
no filtra ni oculta ese salto en la gráfica, sino que cambia la configuración
usada por el controlador. No demuestra que el motor arranque con un duty bajo.
La NTC no puede imponer aumentos. Local converge suavemente a 130; se conserva
la frescura de las órdenes y no se modifican las protecciones internas HP.

`smoothing.h` prueba diez minutos simulados con el controlador C++ real:
parada, escalón a 100 %, subida/bajada acotadas, confirmación y retorno a cero.
Reproduce además el cruce 47→48 % de la curva del caso investigado. `external_only.h`
comprueba independencia de NTC válida/inválida/caliente y rampa a 130 desde ambos
lados, caducidad de lease y retirada de autoridad. El cliente Windows↔C++ comprueba
también el preset completo recibido.

## Rango completo y parada externa

El estado firmado añade `externalMinimumOutput:0` conservando
`externalCurveVersion:1` y schemaVersion 1. Su ausencia significa firmware
anterior con mínimo 30. Windows solo envía configuraciones con algún punto,
salida Manual o arranque <30 después del anuncio explícito de mínimo cero.
Si falta, mantiene monitorización, retira Solicitud y pide actualizar firmware.
Las curvas existentes con mínimo 30 siguen funcionando sin alterarse.
No cambia el formato ni namespace NVS; las curvas guardadas no se convierten
ni se sustituyen automáticamente por otras con mínimo cero.

En Remote autorizado, una salida cero puede apagar la base independientemente
de la NTC. El EMA y la confirmación de descenso siguen activos;
al asentarse dentro de la banda de histéresis, una petición explícita de cero
ancla el objetivo a cero exacto para no dejar un residuo indefinido. La rampa
de bajada llega entonces a cero. Al salir de parada, se reinicia el estado de
objetivo/filtro de esa transición y se aplica el pulso configurado si su salida
y duración son positivas. Sin pulso también se admite una salida positiva
menor que la histéresis. Esto evita quedar atrapado en cero o reiniciar el
arranque en cada iteración.

No hay umbrales ambientales que modifiquen la salida. La pérdida de autoridad o
lease vuelve a Local con rampa a 130. El circuito invertido recibe 16383/16383 para salida
lógica 0 y 0 para salida lógica 255. El LCD informa `PWM: 0% (0)` cuando se aplica
cero; un nulo sigue siendo `--`. El programa no puede confirmar giro o parada
mecánica sin tacómetro.

La gráfica usa escala Y 0–255. ○ amarillo indica el objetivo de la curva
editable a la entrada reciente; ● verde indica el duty aplicado informado por
el ESP32. Se emparejan el estado autenticado y la telemetría del ACK exacto,
no la última trama enviada si todavía no tiene respuesta. El marcador aplicado
exige Remote, revisión confirmada y el mismo tipo/calibración de entrada; puede
quedar fuera de la curva por los filtros y el pulso configurado. En edición, el objetivo
se rotula Borrador. Ambos marcadores desaparecen al vencer trama/respuesta a
2000 ms o fuente de demanda a 5000 ms; cero válido se muestra y nulos no se
inventan. Con entrada Solicitud se muestra su normalización, no Demanda.

La implementación y la validación remota se describen en
[análisis de parada y marcadores](EXTERNAL-CURVE-ZERO-OUTPUT.md).
