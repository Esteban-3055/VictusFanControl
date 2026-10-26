# Victus Base ESP32-C3 SuperMini — consolidación Work 0.4

Implementación única compatible con VictusFanControl y su pestaña **Curva Externa**.
Conserva la corrección Wi-Fi de **8,5 dBm** informada como funcional por el usuario.
Servidor TCP local 8765, mDNS `victus-base.local`, autenticación mutua y sin nube.
El código se compila y prueba en PC; **no se ha cargado esta consolidación en la
placa ni validado físicamente su LCD, NTC, PWM o etapa de potencia**.

La curva validada conserva su identidad entre conexiones TCP y al restaurarla
desde NVS: reutilizar una revisión con contenido diferente se rechaza. Una conexión
nueva exige autenticación y solicitud nuevas; conservar la curva no concede Remote.
Véase la [revisión de coordinación](../../docs/AUTOMATIC-COORDINATION-REVIEW.md).

## Conexiones que se conservan

| Función | GPIO | Configuración |
|---|---:|---|
| PWM invertido | 3 | **18 Hz**, temporizador LEDC **14 bits**, salida lógica 0–255 |
| LCD SDA | 6 | PCF8574: 0x20–0x27 o 0x38–0x3F, detección automática |
| LCD SCL | 7 | LCD1602, P0=RS/P1=RW/P2=EN/P3=luz/P4..7=datos |
| NTC ambiente | 0 | 10 kΩ, B3950; resistencia fija 10 kΩ |
| Alimentación motor | externa 5 V | Masa común con ESP32 y etapa |

Divisor NTC: `3V3 → fija 10k → GPIO0 → NTC → GND`, 100 nF opcional a GND.
Promedio de 8 lecturas ADC en mV calibrados, EMA ambiental y validación de rango.
Cambiar valores físicos en `include/BoardConfig.h` si la NTC real difiere.

Se mantiene la etapa del paquete: GPIO3 → 220 Ω → gate 2N7000; gate con pull-down
100 kΩ; source a GND; drain a base BC337; resistencia de base a +5 V; emisor a GND,
colector al negativo del ventilador, positivo del ventilador a +5 V.
HIGH en GPIO3 apaga BC337: **duty eléctrico de 8 bits = 255 − salida lógica**.
Se escala ese duty eléctrico al rango LEDC de 14 bits mediante
`round(duty8 * 16383 / 255)`. Así, una salida lógica de 30 produce
225 en escala eléctrica de 8 bits y **14456 en LEDC**; una salida lógica
de 255 produce **0 en LEDC**. La Curva Externa de Windows y el protocolo
siguen trabajando con salidas **0–255**. No se reinterpretan como 14 bits.
No conectar el motor a un GPIO. GPIO flotante/reset con ese pull-down puede
significar **ventilación máxima**, también con firmware de monitorización.

El usuario informa ~500 mA de marcha y ~700 mA de arranque. Quedan sin medir el
margen del BC337, saturación, temperatura, conmutación a 18 Hz y transitorios.
La resistencia de base del circuito inicial también necesita revisar disipación;
el paquete propone dos de 47 Ω en serie o resistencia con potencia suficiente.
Estas conexiones conservadas no constituyen aprobación eléctrica del circuito.
Para LCD a 5 V con pull-ups a 5 V, usar adaptación bidireccional 3,3↔5 V en I²C;
el ESP32 no admite 5 V en sus GPIO. Un ACK no demuestra caracteres visibles.

## Compilar con PlatformIO (entorno reproducible)

Python + PlatformIO Core 6.1.18, plataforma espressif32 6.12.0, Arduino 2.0.17 y
ArduinoJson 6.21.5 fijados. Dos imágenes, ninguna incluye credenciales:

```powershell
python -m pip install platformio==6.1.18
cd firmware/esp32-c3
python -m platformio run -e supermini
python -m platformio run -e supermini-bench
```

`supermini`: monitorización, GPIO3 sin accionar, PWM aplicado nulo y modo Local.
**No aísla el motor**: desconectar alimentación del motor para revisar el circuito.
`supermini-bench`: PWM y control local/remoto habilitados, pendiente de banco.
Imágenes en `.pio/build/NOMBRE/firmware.bin`, con bootloader y particiones.
En Windows conviene una ruta corta para proyecto y herramientas (limitación GCC).

Carga explícita, solo después de revisar el circuito y seleccionar el USB real:

```powershell
python -m platformio run -e supermini-bench -t upload --upload-port COM7
python -m platformio device monitor --port COM7 --baud 115200
```

COM7 es un ejemplo. No se ha ejecutado ninguna carga en este desarrollo.
No borrar toda la flash al actualizar si se quiere conservar el emparejamiento.

## Arduino IDE 2.x y core ESP32 3.x

No hay un segundo controlador: se genera el sketch a partir de las mismas fuentes.
Desde la raíz del repositorio:

```powershell
python firmware/esp32-c3/tools/export_arduino.py artifacts/arduino --enable-pwm
```

Abrir `artifacts/arduino/VictusBaseESP32C3/VictusBaseESP32C3.ino`. Instalar placa
**esp32 by Espressif Systems 3.3.2**, biblioteca **ArduinoJson 6.21.5**, seleccionar
ESP32C3 Dev Module, flash 4 MB, USB CDC On Boot habilitado y puerto real.
`--enable-pwm` genera `BoardConfig.h` con la salida invertida GPIO3 activada.
Sin esa opción se conserva la variante de monitorización, sin control PWM.
El ZIP Arduino de la release 1.2.17 usa `--enable-pwm`, igual que el CI Arduino.
Los paquetes Windows incluyen los informes de coordinación, LCD y política externa.
Antes de regenerar, conservar fuera del repositorio cualquier configuración
privada; el exportador nunca la copia. Al cargar, dejar desactivado el borrado
completo de flash para conservar el emparejamiento y los ajustes NVS.

El exportador copia también `include/LcdPresentation.h`, que contiene
el formato y la secuencia de pantallas compartidos entre Arduino IDE,
PlatformIO y los tests C++.

El código selecciona la API LEDC de Arduino 2.x o 3.x por versión. La documentación
de referencia de Espressif explica la [migración de LEDC](https://docs.espressif.com/projects/arduino-esp32/en/latest/migration_guides/2.x_to_3.0.html).
El CI compila también el sketch generado con core 3.3.2, además de PlatformIO.

## Emparejamiento y conexión

Preferido: USB 115200, terminal sin eco local ni registro de entradas. Enviar
`setup`, luego por líneas SSID de 2,4 GHz, contraseña Wi-Fi y **la misma clave
Base64 de 32 bytes guardada en Windows**. No usar una contraseña humana ni el
vector público de pruebas. `status` informa estado sin secretos.
`erase CONFIRM` borra las preferencias del dispositivo y desconecta.

SSID, contraseña y clave persisten en NVS: no se necesita cambiarlos al reiniciar.
La clave permanece fija mientras no se reprovisione o borre la flash. Alternativa
compatible con Work 0.4: copiar `include/config.local.h.example` como
`include/config.local.h` (en Arduino, junto al sketch), con WIFI_SSID,
WIFI_PASSWORD y PAIRING_KEY_B64. NVS ya aprovisionada tiene prioridad.
El archivo privado está ignorado por Git. **Una imagen compilada con él contiene
secretos**; no compartirla. NVS tampoco está cifrada en este perfil.

Se llama WiFi.begin una vez por inicio/provisión, se conserva AutoReconnect y
se fija 8,5 dBm, con verificación al asociar. Si la estación permanece sin conexión
y sin eventos durante al menos 60 s, un watchdog solicita WiFi.reconnect sin
borrar credenciales. Las esperas entre solicitudes son 60/120/240/300 s; un evento
reciente pospone la intervención para no interrumpir una asociación en progreso.
No se reconfigura la estación cada 10 s. Al perder conexión se cierran sockets y
mDNS; si falla el inicio de mDNS se reintenta cada 5 s sin cerrar TCP.
`wifi` o `status` muestran estado numérico, último motivo de desconexión,
contador y recuperaciones de este arranque, sin SSID, contraseña ni clave.
No se fija 192.168.100.133: una reserva DHCP depende de la MAC y del router.

En Windows: Base externa → desmarcar Simulador → host `victus-base.local` o IP
mostrada por USB → puerto 8765 → guardar la misma clave → guardar y conectar.
Monitorización no habilita control remoto. Para usar la curva, seleccionar
Monitorización + Solicitud y tener una Solicitud interna aceptada y vigente.

## Configurar la curva

**Curva Externa** aparece después de Base externa y antes de Actualizaciones.
Editar puntos arrastrando, con flechas o entrada numérica; añadir/quitar puntos,
restablecer y descartar. Guardar no modifica los perfiles internos del notebook.
Se conservan los extremos 0 y 100, 2–16 puntos y salidas crecientes de 0–255.

Entrada **Demanda**: X demanda 0–100 del notebook, Y solicitud externa. En Manual
usa la salida externa fija configurable en Respuesta y arranque.
Entrada **Solicitud aceptada**: normaliza según los extremos de calibración
visibles y editables. 0–54 es el valor inicial, no una regla permanente.
Un valor fuera de la calibración deja Remote y lleva suavemente a PWM 130.

Respuesta configurable: EMA, histéresis, confirmación de descenso, subida/bajada
independientes, salida/duración de arranque. Iniciales: 1500 ms, 3 niveles, 3000 ms,
12 %/s arriba, 6 %/s abajo y pulso opcional 255 durante 800 ms con orden remota positiva.
El pulso se repite al pasar de parada real a demanda positiva; no se repite por reconectar. La configuración se confirma por revisión aplicada
desde la tarea de control; el estado de la pestaña distingue guardado de confirmado.
Se conserva en NVS, con escrituras espaciadas al menos 30 s y comprobación al cargar.
La [revisión de LCD y Respuesta y arranque](../../docs/ESP32-LCD-RESPONSE-REVIEW.md)
documenta el recorrido de cada ajuste y las pruebas con valores no predeterminados.

Para la respuesta más lenta admitida, **Respuesta y arranque → Máxima suavidad**
prepara un borrador con subida/bajada 1 %/s, EMA 10000 ms, histéresis 3,
confirmación de descenso 30000 ms y duración/salida de arranque 0. Conserva
los puntos, entrada, calibración y salida Manual. Pulsar **Guardar y enviar curva**
para aplicarlo; no cambia las configuraciones existentes automáticamente.
El recorrido completo de salida necesita al menos 100 s, además de filtros y
esperas; confirmar descenso añade espera, no cambia la velocidad de la rampa.
El objetivo gráfico puede moverse rápidamente con la demanda aunque el aplicado
avance despacio. La NTC es solo informativa; Local tiene objetivo fijo 130 y
respeta filtros y rampas. Sin pulso no se ha verificado el arranque mecánico a
baja salida; el firmware no tiene tacómetro. No hace falta otro formato de
protocolo/NVS ni modificar el firmware para recibir estos valores.

**La NTC es exclusivamente informativa.** Su temperatura, fallo o caducidad no
modifican la salida ni retiran Remote. Se eliminan el piso ambiental y el máximo
automático por NTC. Sin una orden remota válida, Local tiene objetivo **130/255**
(aproximadamente 51 %), alcanzado con EMA, histéresis, confirmación y rampas.
Se asienta en 130 exacto, tanto desde arriba como desde abajo. El arranque de
software comienza en salida lógica cero y se dirige a ese objetivo, sin pulso.
Los pulsos explícitamente configurados solo se admiten al arrancar con una orden
remota positiva o al salir de una parada real; Máxima suavidad los desactiva.
La tarea de control corre cada 20 ms separada de TCP, LCD y provisión. La orden
remota caduca a los 2000 ms. Volver a Firmware, perder autoridad o desconectar
Windows devuelve Local con rampa a 130, sin saltos de salida. No se restaura
ninguna autoridad del notebook desde ESP32. Esta política requiere cargar el
firmware actualizado; no cambia las protecciones internas HP ni HMAC/TCP/NVS.

Contratos: [v1](../../docs/EXTERNAL-COOLING-PROTOCOL.md) y
[extensión Curva Externa](../../docs/EXTERNAL-CURVE-PROTOCOL.md).
[Auditoría y comparación con Work 0.4](../../docs/ESP32-WORK04-AUDIT.md).

## Corrección LEDC 18 Hz / 14 bits (2026-10-16)

Tras seleccionar 18 Hz y mantener por error 8 bits, el usuario observó al
arrancar: `ledc: requested frequency 18 and duty resolution 8 can not be achieved`.
La inicialización fallaba y el `abort()` impedía que arrancaran Wi-Fi y TCP;
el fallo no era de clave Base64 ni del router. Se corrigió
`include/BoardConfig.h` a **18 Hz / 14 bits** y la salida de
`src/main.cpp` en ambos caminos Arduino 2.x y 3.x: `ledcWrite()`
recibe el duty eléctrico invertido convertido de 0–255 a 0–16383.
El sketch exportado se genera desde esas mismas fuentes. El perfil
no modifica las curvas ni la lógica de seguridad del notebook.

La documentación de Arduino-ESP32 para ESP32-C3 admite 1–14 bits de
resolución LEDC. La combinación 18 Hz / 14 bits y la señal física aún
requieren comprobación en la placa: el test de conversión y la compilación
no sustituyen medición con osciloscopio. Con PWM deshabilitado no debe
asumirse que el motor se detiene, debido a la inversión 2N7000 → BC337.

## Diagnóstico ADC / NTC ambiental

El ESP32-C3 usa **GPIO0** con ADC 12 bits, atenuación 11 dB y lectura
`analogReadMilliVolts()` promediada ocho veces. El divisor físico es
`3V3 → 10 kΩ fija → GPIO0 → NTC 10 kΩ → GND`: alrededor de
**1650 mV a 25 °C**, si el NTC es B3950 y las resistencias son nominales.

Ante el mensaje `NTC Fault`, abrir el monitor serie a **115200 baudios**,
activar **Nueva línea** y enviar `ntc`. El firmware muestra sin secretos:

```text
NTC GPIO0 raw=... adc=... mV motivo=... antiguedad=... ms temperatura=... C filtrada=... C
```

`motivo=NO_SAMPLE` significa que todavía no se adquirió la primera muestra;
`RAW_LOW/RAW_HIGH` indica lectura sin calibrar cerca de los extremos;
`MV_LOW/MV_HIGH` indica milivoltios fuera del intervalo de protección
20–2450 mV; `BAD_RESISTANCE` y `TEMP_RANGE` señalan error numérico
o temperatura implausible; `OK` indica una lectura válida.
`filtrada=INVALIDA` pese a `motivo=OK` sugiere muestra vencida (>3 s)
o lectura todavía no propagada. Comparar `adc=... mV` con un multímetro
**midiendo GPIO0 respecto a GND**, no con la caída sobre el NTC.

El comando `ntc` también imprime `lecturas_ok`, `lecturas_fallo`
y `ultima_falla` para detectar un fallo intermitente aunque la última
muestra sea correcta. Ejemplo reportado por el usuario:
`raw=2439 adc=1792 mV motivo=OK temperatura=21.2 C filtrada=21.2 C`.
La lectura es válida; por sí sola no explica un `NTC Fault` persistente en
el LCD.

### LCD 1602 — ciclo de pantallas

| Vista (en este orden) | Duración | Fila 1 | Fila 2 |
|---|---:|---|---|
| Temperaturas | **30 s** | CPU °C y GPU °C | Ambiente: °C (NTC) |
| Uso | **5 s** | CPU y GPU: porcentajes | RAM: porcentaje |
| Demanda/ventilador | **5 s** | DEMANDA: porcentaje | PWM: porcentaje (salida lógica 0–255) |
| Conexión | **5 s** | PC: conectado/sin datos/desconectado | WiFi: conectado/sin red |

Se eligieron **5 s para las vistas 3 y 4**, cuyas duraciones no estaban
especificadas. El ciclo de **45 s empieza por temperaturas** y se repite aunque
no haya PC, Wi-Fi o NTC válida. Las filas tienen exactamente 16 caracteres más
terminador; no hay desplazamiento. La NTC ocupa su propia fila, sin sustituir
las temperaturas CPU/GPU. Por ejemplo, `CPU:55C GPU:42C` / `Ambiente:21.2C`,
y la página de uso muestra
`CPU:100%GPU:  0%` / `RAM: 65%`. Temperaturas CPU/GPU redondeadas a °C enteros;
ambiente con una decimal. El refresco es cada 250 ms; los cambios de página
ocurren en el primer refresco posterior al límite. El reloj conserva fase al
desbordar `millis()` y al recuperarse el LCD.

CPU/GPU/RAM/DEMANDA/temperaturas CPU/GPU proceden de la telemetría **firmada de
Windows**. La presentación exige sesión autenticada y telemetría recibida hace
menos de 2000 ms. Además suma la edad de la fuente al capturar con el tiempo
monotónico transcurrido en el ESP32: cada valor vence a los **5000 ms**.
CPU/GPU uso emplean `sampledAtUtc`; RAM, `ramSampledAtUtc`; temperaturas,
`cpuTempSampledAtUtc`/`gpuTempSampledAtUtc`; DEMANDA, `decisionAtUtc`.
Campo o fecha nulos, valor fuera de rango o vencido se muestran `--`; cero válido
se conserva. Una trama nueva no rejuvenece una muestra de origen vieja.
El ESP32 no tiene reloj UTC sincronizado: compara fechas dentro del payload
firmado y mide su residencia local; no puede medir por sí solo un retraso
unidireccional anterior a recibir la trama. La autoridad remota conserva los
controles originales y su referencia a respuesta/lease de 2000 ms.

La NTC es exclusivamente ambiente y caduca a los 3 s, aunque el PC siga enviando.
Si falla, la primera vista muestra `Ambiente:--C`; la tercera conserva DEMANDA
si está fresca y muestra el PWM aplicado, independiente del estado de la NTC.
CPU/GPU conservan sus datos propios si están frescos. Sin PC, el ambiente válido
sigue visible y las temperaturas CPU/GPU y DEMANDA muestran `--`. Con PWM
inhabilitado se muestra `PWM:-- (OFF)`. El porcentaje corresponde a la salida
lógica aplicada: 96/255 = 38%; no es duty invertido de GPIO3 ni medición de RPM.
PC conectado significa sesión autenticada con telemetría reciente, incluso en
monitorización; `sin datos` indica sesión autenticada sin telemetría reciente.
Esto no afirma autoridad Remote. Wi-Fi se muestra de forma independiente.

Si la pantalla sigue mostrando `NTC Fault` mientras `ntc` marca `OK`,
podría tratarse de un **mensaje antiguo congelado por un error I²C** o un
fallo transitorio de lectura previo al comando. No es posible determinar la
causa sin medir la pantalla y consultar ambos diagnósticos. Para distinguirlos,
en el monitor serie a 115200 baudios enviar `lcd` y `ntc`:

- `lcd` muestra dirección I²C, si está activa, errores de escritura,
  código de error y tiempo transcurrido desde la última actualización
- `ntc` muestra la lectura actual y los contadores de muestras correctas y
  fallidas desde el arranque

Si falla la comunicación I²C, el firmware reintenta detectar el LCD cada
5 s. Al recuperar ACK se fuerza EN bajo y se ejecuta la inicialización completa
HD44780, con esperas explícitas entre instrucciones, antes de reescribir ambas
filas con datos actuales. Una transacción fallida detiene el resto de nibbles y
caracteres hasta ese reinicio. I²C se fija a 100 kHz y timeout 20 ms. Los
caracteres físicos pueden quedar congelados mientras el bus no responda. El diagnóstico no altera el PWM ni
deshabilita protecciones. Alimentar el backpack a 5 V exige conversor
de niveles en SDA/SCL para proteger al ESP32-C3.

Un fallo NTC solo invalida la lectura ambiental y sus diagnósticos: no modifica
el PWM. No se afirma que el hardware
esté defectuoso o que la lectura del ADC sea fiable hasta observar datos reales.
La lectura filtrada del NTC y su fecha se publican juntas bajo una sección
crítica corta. El consumidor obtiene la hora después de la instantánea para
evitar una falsa caducidad si fue interrumpido durante una nueva muestra.
No se retiene la última lectura buena ante un fallo: siguen vigentes RAW
8–4087, 20–2450 mV, −40..100 °C y caducidad a 3000 ms. No se atribuye el
reporte físico a esta carrera ni al bus sin contrastar `ntc` y `lcd` en placa.
El comando no muestra SSID, contraseña Wi-Fi ni clave de emparejamiento.

## Validación pendiente de banco

La prueba C++ ejecuta el mismo parser, autenticación y controlador que la placa,
con valores simulados y claves públicas exclusivas de pruebas. La integración
conecta el cliente Windows real por TCP local, sin acceso a hardware.
Los resultados de software y las limitaciones constan en
[VALIDACIONES](VALIDACIONES.md). El código anterior de tres NTC no estaba en el
ZIP: se adaptó un perfil autorizado, no se afirma recuperar sus constantes exactas.

Antes de activar motor: medir 18 Hz e inversión para salidas 30/64/128/192/255,
arranque, corriente, temperatura del BC337/2N7000/resistencias, LCD con niveles
correctos, NTC con termómetro/abierto/corto, Wi-Fi ausente y reinicios, retirada de
Windows, silencio y retorno Local tras 2000 ms + periodo de tarea. Sin tacómetro,
appliedDuty y appliedPwmPct son órdenes eléctricas calculadas, no prueba de giro.


### LCD HyperX / promedio ambiente (desarrollo posterior a 1.3.4)

La pantalla principal pasa a CPU/GPU y Amb/Hyp. NTC: media móvil de 40 temperaturas cada 100 ms; ventana vacía o inválida aparece como `--`. HyperX llega firmado desde Windows, una adquisición por minuto. Sin PC, `Hyp:55*` indica último porcentaje guardado en RAM. Los avisos bajos/críticos interrumpen cualquier página y sus tiempos/ciclos se configuran desde HyperX Battery. No hay consulta USB de audífonos en el ESP32. Véase [detalle de la integración](../../docs/HYPERX-BATTERY-LCD.md). Se conservan pines, PWM invertido, Wi-Fi, HMAC y NVS.
