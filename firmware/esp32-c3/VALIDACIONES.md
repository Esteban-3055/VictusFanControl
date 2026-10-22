# Evidencia de software y validaciones físicas pendientes

## Política actual: NTC informativa y Local 130 (2026-10-18)

Por petición del usuario, se retiran las actuaciones automáticas por ambiente:
NTC inválida/vencida/caliente ya no fuerza máximo, ni retira Remote, ni impone
un piso. Al perder la orden válida, Local converge con filtros y rampas a 130
exacto. El arranque de software parte de cero sin pulso Local; el pulso explícito
solo se admite con una orden remota positiva. Máxima suavidad lo desactiva.
Las secciones inferiores registran revisiones anteriores y sus políticas históricas.

Pruebas actuales: C++ con `-Werror`, controlador con NTC desde inválida a caliente,
invariancia de autoridad/salida respecto al ambiente, rampa Local desde cero y
255 a 130 exacto, caducidad a 2000 ms, rechazo de calibración/órdenes inválidas,
35/35 vectores de protocolo (incluye Remote autenticado sin NTC), exportación
canónica, layout LCD y cinco sesiones Windows↔C++ con campos/ACK comprobados.
Build Windows sin errores/advertencias y prueba UI externa: PASS.
HMAC, frescura, GPIO/frecuencia/inversión, NVS y control HP no se cambian.
No son pruebas de señal física, giro, temperatura ni placa programada.

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

## LCD1602: ciclo solicitado y recuperación (2026-10-16)

El ciclo vigente reemplaza el anterior: **temperaturas CPU/GPU + DEMANDA 30 s**,
**uso CPU/GPU/RAM 5 s**, **ambiente + PWM aplicado 5 s**, **PC + Wi-Fi 5 s**.
Las duraciones 3 y 4 no fueron especificadas; se adoptan 5 s. Se conserva la
fase del ciclo al recuperar LCD y al desbordar `millis()`, con refresco 250 ms.

Pruebas host en `test/presentation.h`, invocadas por `--controller` y la suite
`test_protocol.py`, contra las mismas cabeceras compiladas para la placa:

- Cada milisegundo de dos ciclos completos: orden y límites 30000/35000/40000/45000.
- Rollover del reloj de páginas y de vencimientos a 2000/3000/5000 ms.
- Filas de 16 caracteres, máximos/mínimos, cero, NaN/inf, campos ausentes,
  PWM deshabilitado, desconexión PC, Wi-Fi independiente, separación CPU/GPU/NTC.
- Inyección de fallo en cada una de las **81 transacciones** de inicialización
  y escritura de dos filas: se detiene en el primer error, no escribe más,
  reinicia EN bajo, resincroniza por completo y reescribe las dos filas actuales.
- Conversión del reporte `2439 / 1792 mV` a 21,2 °C; divisor nominal a 25 °C;
  límites RAW/mV intactos; NTC válida → inválida → recuperada sin conservar EMA
  vieja; salto caliente sin suavizar. NTC inválida sigue forzando 255 en control.

`test_presentation.py` usa el parser C++ real con autenticación/HMAC y telemetría
firmada de monitorización: verifica por campo la edad de origen más residencia
local, fechas ausentes, cero válido y nulos. También compara byte por byte todas
las cabeceras/fuentes exportadas a Arduino y comprueba ausencia de config privada.
No se modifica el esquema de red ni el formato persistido `ExternalConfig`/NVS.

Ejecución local: **34/34 casos de protocolo**, **17/17 escenarios de configuración**
y **presentación/frescura/exportación PASS**. Compilador host Zig/Clang 0.13,
C++17, `-Wall -Wextra -Werror`, ArduinoJson 6.21.5; se utiliza BCrypt de Windows
para HMAC. El runtime de Zig emite avisos propios de atributos de libunwind;
no hay errores ni avisos de las fuentes del proyecto.

Reproducir después de compilar el host como indica el workflow:

```text
python firmware/esp32-c3/test/test_protocol.py firmware-host --report protocol-tests.json
python firmware/esp32-c3/test/test_external_config.py firmware-host
python firmware/esp32-c3/test/test_presentation.py firmware-host
```

PlatformIO local: **ambas variantes PASS**, usando una unidad temporal de ruta
corta al mismo proyecto para resolver la limitación de rutas largas GCC en Windows.

El workflow `esp32-firmware.yml` compila PlatformIO `supermini` y
`supermini-bench` (Arduino 2.0.17, espressif32 6.12.0) y el sketch exportado
con **core Arduino-ESP32 3.3.2 + ArduinoJson 6.21.5**, PWM habilitado.
3.3.2 es la versión del paquete de placas ESP32, no del programa Arduino IDE.
Los resultados del commit actual se enlazan en el PR #44; la compilación del
sketch verifica la misma implementación que se abre desde Arduino IDE 2.x.
No se cargó ninguna placa ni se usaron credenciales privadas.

La instantánea NTC ahora publica valor y fecha juntos bajo sección crítica,
con reloj obtenido después de copiar la muestra. Evita mezclar publicaciones
y una edad negativa por preempción. Los diagnósticos USB `ntc` y `lcd` se
conservan. Las pruebas inyectadas verifican el comportamiento del software;
no establecen la causa física del reporte de pantalla congelada.

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
