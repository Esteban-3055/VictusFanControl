# Evidencia de software y validaciones físicas pendientes

## Base y consolidación

- Paquete Work 0.4: manifiesto SHA256 de sus ocho archivos verificado; seis
  comprobaciones estáticas originales aprobadas. No se trataron como compilación.
- Base 6d86c92: Release Windows sin errores ni advertencias y 33 casos del motor
  C++ de referencia antes de modificar. Auditoría en docs/ESP32-WORK04-AUDIT.md.
- Commits por etapa: auditoría, controlador/protocolo, adaptación de placa,
  interfaz Windows/simulador, documentación/CI y endurecimiento final.

## Ejecutado, con datos simulados

| Comprobación | Evidencia |
|---|---|
| Release Windows .NET 8 | Sin errores ni advertencias, warnaserror |
| Motor C++ consolidado | 34/34 casos: HMAC/JSON/fechas/lease y controlador dinámico |
| Curvas por protocolo C++ | 17 escenarios: válido, límites, tipos, revisión inmutable y ausencia de autoridad en monitorización |
| Controlador externo | Interpolación, inversión 8 bits, límites, arranque, rampas, EMA/histéresis/descenso, piso ambiental, NTC inválida y rollover |
| Cliente Windows real ↔ motor C++ | TCP local, autenticación, respuestas fragmentadas, curva confirmada y Firmware→Local |
| Transporte Windows/simulador | Fragmentos, inválidos, retrasos, duplicados, silencio, autenticación, reconexiones, sesiones nuevas, cancelación y concurrencia |
| Interfaz WinForms | Puntos/ajustes, guardar/descartar, persistencia, escalado 1040/1672/3344, orden lateral y aislamiento de perfiles internos |
| Ciclo de vida Windows | DPAPI, bandeja/transmisión oculta, suspensión/reanudación y cierre; cero órdenes internas desde el editor externo |
| Regresiones internas | SafetyGate, control, política automática, WMI/BIOS/HP, watchdog, telemetría, recuperación y GUI de producto |
| ESP32-C3 PlatformIO | Dos imágenes reales compiladas: monitorización y PWM invertido; sin cargar placa |
| Arduino 3.x | Sketch generado de las fuentes canónicas compilado por CI oficial con core 3.3.2 y ArduinoJson 6.21.5 |

La prueba Performance GUI local encontró una carpeta de recuperación de la
instalación activa sin permiso de lectura (`ProgramData/.../WatchdogM2`). Se
registró como **no aprobada localmente**, sin borrar ni cambiar su estado.
La misma regresión pasó en el CI Windows limpio de la consolidación. La descarga
local duplicada del core Arduino 3.x se detuvo una vez compilado en CI; no se
presenta como una compilación local completada.

La prueba GUI general necesitó actualizar su lista esperada de navegación para
incluir Curva Externa; después pasó. No se cambiaron las implementaciones de
Control, Safety, Performance ni Runtime del notebook respecto a la base.

Otra ejecución CI encontró un timeout al cerrar el fixture de configuración
corrupta después del ensayo gráfico de Curva Externa. El ensayo nuevo se ejecuta
ahora en un proceso WinForms propio, separado del de red/ciclo de vida. Ambos
pasaron por separado sin ampliar los plazos ni cambiar el cierre del programa.
La validación de curvas en C# exige los mismos campos que C++; una configuración
inválida o una revisión reutilizada termina solo su conexión y el simulador
continúa aceptando sesiones. Esto también se verifica por TCP real.
Las capturas del editor y de los ajustes se revisan visualmente; la tabla usa
columnas explícitas para conservar la alineación al escalar la ventana.
También se ejercitan clics reales en +/− y la edición numérica exacta de ajustes;
no basta con invocar directamente el modelo de configuración.

En el ensayo local de recursos TCP de 10 s no hubo acumulación de mensajes;
las cifras medidas están en el informe de pruebas de ExternalCooling. La RAM
estática/flash del firmware se informan al compilar; no son una medición de
heap, CPU, consumo ni temperatura de una placa en funcionamiento.

El informe de entrega identifica el SHA final y los enlaces del CI de ese SHA.
Consultar también Actions/esp32-firmware, external-cooling, build, cpu-rapl,
wmi-fan-experiment y oem-shadow. Un resultado de otro SHA no sustituye ese control.

## Regresión LEDC: 18 Hz y 14 bits (2026-10-16)

El usuario capturó el error físico `requested frequency 18 and duty resolution 8 can not be achieved`
en el ESP32-C3 con PWM habilitado: el `abort()` de inicio impedía abrir
Wi-Fi/TCP. Corrección en fuentes canónicas: temporizador LEDC 18 Hz /
14 bits, y conversión del duty eléctrico invertido 0–255 → 0–16383
en caminos Arduino 2.x y Arduino 3.x. El controlador, las curvas y el
protocolo conservan 30–255. La prueba portátil comprueba extremos,
punto medio, monotonicidad e inversión. Las compilaciones del nuevo SHA
se deben verificar mediante GitHub Actions; todavía **no hay PASS físico**
de señal de 18 Hz, arranque ni temperatura.

## Diagnóstico adicional NTC / ADC (2026-10-16)

Se incorporó el comando USB `ntc`, que imprime lecturas RAW de GPIO0,
milivoltios calibrados, temperatura calculada, antigüedad, temperatura
filtrada y la causa exacta de rechazo. Se mantiene la misma validación y
fallback seguro; **no se modificó la conversión eléctrica ni se eliminó
ningún límite del ADC**. El usuario informa tensión aparentemente correcta,
pero la causa de `NTC Fault` no podrá conocerse hasta observar RAW/mV
del dispositivo. Falta comprobar en hardware y validar Arduino/PlatformIO
en el SHA posterior a este cambio.

## Estado físico

Solo se conserva como antecedente **informado por el usuario** que Wi-Fi a
8,5 dBm conectó en su Work 0.4. No es una prueba de esta consolidación completa.
No se ha programado el ESP32 ni enviado órdenes al hardware real en este trabajo.

Pendiente de medir en banco:

1. GPIO3 y etapa invertida a 18 Hz / 14 bits, salidas 30/64/128/192/255 y estado durante reset.
2. Arranque mecánico y corriente, particularmente el mínimo 30; no hay tacómetro.
3. Disipación/saturación/conmutación BC337 y 2N7000, resistencias y transitorios.
4. LCD: tensión de pull-ups, adaptación I²C, contraste, dirección y caracteres visibles.
5. NTC contra termómetro, tolerancias, ambiente frío y fallos abierto/corto/desconexión.
6. Corte Wi-Fi/router, PC apagado, reinicios, lease físico de 2000 ms más periodo
   de tarea y conservación de la curva/clave tras corte de alimentación.
7. Heap/stack/CPU y comportamiento prolongado en el ESP32-C3 real.

Los perfiles son de desarrollo/banco; **ningún PASS físico del ESP32 se deriva
del simulador, de los fixtures C++ o del CI**.
