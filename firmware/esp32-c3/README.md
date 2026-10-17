# Victus Base ESP32-C3 SuperMini — consolidación Work 0.4

Implementación única compatible con VictusFanControl y su pestaña **Curva Externa**.
Conserva la corrección Wi-Fi de **8,5 dBm** informada como funcional por el usuario.
Servidor TCP local 8765, mDNS `victus-base.local`, autenticación mutua y sin nube.
El código se compila y prueba en PC; **no se ha cargado esta consolidación en la
placa ni validado físicamente su LCD, NTC, PWM o etapa de potencia**.

## Conexiones que se conservan

| Función | GPIO | Configuración |
|---|---:|---|
| PWM invertido | 3 | 18 kHz, 8 bits, salida lógica 30–255 |
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
HIGH en GPIO3 apaga BC337: **duty eléctrico = 255 − salida lógica**.
Así, solicitud externa 30 produce 225 en LEDC, y 255 produce 0.
No conectar el motor a un GPIO. GPIO flotante/reset con ese pull-down puede
significar **ventilación máxima**, también con firmware de monitorización.

El usuario informa ~500 mA de marcha y ~700 mA de arranque. Quedan sin medir el
margen del BC337, saturación, temperatura, conmutación a 18 kHz y transitorios.
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
python firmware/esp32-c3/tools/export_arduino.py artifacts/arduino
```

Abrir `artifacts/arduino/VictusBaseESP32C3/VictusBaseESP32C3.ino`. Instalar placa
**esp32 by Espressif Systems 3.3.2**, biblioteca **ArduinoJson 6.21.5**, seleccionar
ESP32C3 Dev Module, flash 4 MB, USB CDC On Boot habilitado y puerto real.
En `BoardConfig.h` del sketch generado, cambiar `VICTUS_ENABLE_PWM` de 0 a 1
para la variante de banco. Antes de regenerar, conservar fuera del repositorio
cualquier configuración privada; el exportador nunca la copia.

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
se fija 8,5 dBm, con verificación al asociar. No se reconfigura la estación cada
10 s mientras intenta conectar. Al perder IP se cierran sockets y mDNS.
No se fija 192.168.100.133: una reserva DHCP depende de la MAC y del router.

En Windows: Base externa → desmarcar Simulador → host `victus-base.local` o IP
mostrada por USB → puerto 8765 → guardar la misma clave → guardar y conectar.
Monitorización no habilita control remoto. Para usar la curva, seleccionar
Monitorización + Solicitud y tener una Solicitud interna aceptada y vigente.

## Configurar la curva

**Curva Externa** aparece después de Base externa y antes de Actualizaciones.
Editar puntos arrastrando, con flechas o entrada numérica; añadir/quitar puntos,
restablecer y descartar. Guardar no modifica los perfiles internos del notebook.
Se conservan los extremos 0 y 100, 2–16 puntos y salidas crecientes de 30–255.

Entrada **Demanda**: X demanda 0–100 del notebook, Y solicitud externa. En Manual
usa la salida externa fija configurable en Respuesta y arranque.
Entrada **Solicitud aceptada**: normaliza según los extremos de calibración
visibles y editables. 0–54 es el valor inicial, no una regla permanente.
Un valor fuera de la calibración deja Remote y vuelve al control ambiental.

Respuesta configurable: EMA, histéresis, confirmación de descenso, subida/bajada
independientes, salida/duración de arranque. Iniciales: 1500 ms, 3 niveles, 3000 ms,
12 %/s arriba, 6 %/s abajo y 255 durante 800 ms al encender.
No hay pulso por reconectar. La configuración se confirma por revisión aplicada
desde la tarea de control; el estado de la pestaña distingue guardado de confirmado.
Se conserva en NVS, con escrituras espaciadas al menos 30 s y comprobación al cargar.

La NTC es ambiental: fallback local 89→255 entre 25 y 45 °C. En Remote hay piso
ambiental desde 35 °C. NTC inválida, sin renovar durante 3 s o ≥45 °C fuerza 255;
no espera a EMA/rampas. Al arrancar sin NTC válida también fuerza máximo.
La tarea de control corre cada 20 ms separada de TCP, LCD y provisión. La orden
remota caduca a los 2000 ms. Volver a Firmware, perder autoridad o desconectar
Windows devuelve Local. No se restaura ninguna autoridad del notebook desde ESP32.

Contratos: [v1](../../docs/EXTERNAL-COOLING-PROTOCOL.md) y
[extensión Curva Externa](../../docs/EXTERNAL-CURVE-PROTOCOL.md).
[Auditoría y comparación con Work 0.4](../../docs/ESP32-WORK04-AUDIT.md).

## Validación pendiente de banco

La prueba C++ ejecuta el mismo parser, autenticación y controlador que la placa,
con valores simulados y claves públicas exclusivas de pruebas. La integración
conecta el cliente Windows real por TCP local, sin acceso a hardware.
Los resultados de software y las limitaciones constan en
[VALIDACIONES](VALIDACIONES.md). El código anterior de tres NTC no estaba en el
ZIP: se adaptó un perfil autorizado, no se afirma recuperar sus constantes exactas.

Antes de activar motor: medir 18 kHz e inversión para salidas 30/64/128/192/255,
arranque, corriente, temperatura del BC337/2N7000/resistencias, LCD con niveles
correctos, NTC con termómetro/abierto/corto, Wi-Fi ausente y reinicios, retirada de
Windows, silencio y retorno Local tras 2000 ms + periodo de tarea. Sin tacómetro,
appliedDuty y appliedPwmPct son órdenes eléctricas calculadas, no prueba de giro.
