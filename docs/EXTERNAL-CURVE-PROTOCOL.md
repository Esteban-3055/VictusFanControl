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
| points | 2–16 puntos, extremos X=0/100, X enteros estrictamente crecientes, Y enteros 30–255 no decrecientes |
| input | 0 = Demanda del notebook; 1 = Solicitud aceptada normalizada por calibración explícita |
| requestMinimum / Maximum | 0≤mínimo<máximo≤65535; 0/54 es calibración inicial, no límite permanente del protocolo |
| manualOutput | 30–255, salida externa independiente en modo Manual cuando input=0 |
| rise / fall | 1–100 % de escala completa por segundo; 12/6 iniciales |
| filterMs | 0–10000 ms; constante del filtro EMA (0 desactiva) |
| hysteresis | 0–30 niveles externos |
| downHoldMs | 0–30000 ms, confirmación sostenida de descenso |
| startMs / Output | 0–3000 ms; 30–255; arranque al encender, no pulso por cada reconexión |

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
`externalRequest`/`appliedDuty` son 30–255 o nulos (salida deshabilitada).
`appliedPwmPct` se conserva como porcentaje 0–100 o nulo. La revisión se confirma
en una respuesta posterior, nunca antes de entregar la configuración al control.
Windows verifica ACK y antigüedad, y muestra datos recientes. Al abrir una sesión
nueva se negocia capacidad de nuevo y se reenvía la configuración guardada.

Fallback local: NTC válida, 89 a 255 entre 25 y 45 °C (mínimo 89 por debajo de
25 °C); piso ambiental creciente desde 35 °C incluso en Remote; ≥45 °C o fallo
de sensor da 255 inmediatamente. Promedio ADC y EMA ambiental; fallos y ambiente
alto no quedan ocultos por el filtro. No se usa el ambiente como carga CPU/GPU.
La salida lógica siempre permanece en **30–255**. GPIO3 utiliza una etapa
NOT (2N7000 → BC337): el valor eléctrico conceptual es **255 − salida**,
en escala de 8 bits. El hardware LEDC del ESP32-C3 funciona a **18 Hz con
resolución de 14 bits**, por lo que el firmware convierte el duty eléctrico
mediante `round((255 − salida) × 16383 / 255)`. Esa conversión no altera
el contrato del protocolo, la curva editable, los estados ni la realimentación
lógica de 30–255. La combinación anterior de 18 Hz / 8 bits falló al
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
