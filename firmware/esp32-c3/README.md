# Victus Base — ESP32-C3 SuperMini, primera implementación

Servidor TCP local compatible con el cliente Windows y el
[protocolo v1](../../docs/EXTERNAL-COOLING-PROTOCOL.md). No utiliza nube, HTTP,
OTA ni cambios en el controlador del notebook. **Código compilado y probado
en PC; ninguna prueba física del ESP32, sensor, pantalla o ventilador realizada.**

## Pines propuestos

| Función | GPIO | Conexión |
|---|---:|---|
| PWM | 3 | Entrada de tu etapa NOT; salida lógica de 3,3 V |
| LCD SDA | 6 | SDA a través de adaptación de nivel si el backpack usa 5 V |
| LCD SCL | 7 | SCL a través de adaptación de nivel si el backpack usa 5 V |
| NTC ambiente | 0 | Centro del divisor descrito abajo; ADC1 |
| Referencia | GND | Masa común de ESP32, control y alimentación |

El perfil utiliza un ESP32-C3 genérico con flash de 4 MB. La inscripción
SuperMini V1601 no identifica por sí sola un esquema oficial verificado:
comprueba que la serigrafía de tu placa expone esos GPIO antes de cablear.
Se evitan GPIO2/8/9 de arranque y GPIO18/19 de USB. GPIO6/7 dejan de estar
disponibles para JTAG externo al usarlos como I²C. La selección se basa en
la [tabla de GPIO del fabricante](https://docs.espressif.com/projects/esp-idf/en/v4.4.2/esp32c3/api-reference/peripherals/gpio.html)
y el [datasheet ESP32-C3](https://documentation.espressif.com/esp32-c3_datasheet_en.html).

El GPIO no alimenta el motor. Tu etapa NOT debe recibir niveles de 3,3 V y
accionar un circuito adecuado al ventilador. `PwmActiveLow=true` compensa la
inversión: 25 % solicitado produce aproximadamente 75 % HIGH en GPIO3 y
25 % HIGH después de la NOT; 100 % produce GPIO bajo. Usa un pull-down externo
en esa entrada para fijar el estado durante reset; con esta polaridad el
estado de fallo previsto después de NOT es ventilación máxima. Verifica que
eso corresponde a tu circuito real. Frecuencia inicial configurable: 25 kHz;
su idoneidad para el motor/etapa y el arranque a bajo duty quedan pendientes.

Los GPIO del ESP32-C3 no aceptan 5 V. Para un LCD1602 con PCF8574 alimentado
a 5 V y pull-ups a 5 V, utiliza adaptación bidireccional 3,3↔5 V en SDA/SCL.
Dirección inicial `0x27`, modificable a `0x3F`. Mapeo soportado del backpack:
P0=RS, P1=RW, P2=E, P3=luz, P4..7=datos. No se da por probado otro mapeo.

NTC propuesta: **10 kΩ a 25 °C, B3950**, con resistencia fija 10 kΩ/1 %:
`3V3 → resistencia fija → GPIO0 → NTC → GND`. Coloca 100 nF de GPIO0 a GND
cerca de la placa. Si tu NTC tiene otros valores, cambia `BoardConfig.h`.
El ADC usa milivoltios calibrados, atenuación de 11 dB y promedio de 8 lecturas;
detecta cortocircuito, saturación y valores no plausibles. La alimentación real,
tolerancias y autocalentamiento requieren calibración con termómetro.

## Compilación y carga

Instala Python y PlatformIO Core 6.1.18 en un entorno de desarrollo:

```powershell
python -m pip install platformio==6.1.18
cd firmware/esp32-c3
python -m platformio run -e supermini
```

La variante normal deja **GPIO3 sin accionar**, devuelve PWM aplicado `null`
y nunca informa Remote. No equivale a aislar eléctricamente una etapa externa:
su pull-down puede llevarla al estado máximo. Comprueba primero la electrónica.
La variante de banco habilita salida y control local/remoto:

```powershell
python -m platformio run -e supermini-bench
```

Ambas generan `.pio/build/NOMBRE/firmware.bin` y los archivos necesarios para
carga. No se ha programado ninguna placa en este desarrollo. Una vez revisado
el cableado y seleccionada su conexión USB, la carga es explícita:

```powershell
python -m platformio run -e supermini -t upload --upload-port COM7
python -m platformio device monitor --port COM7 --baud 115200
```

Sustituye COM7 por el puerto real; confirma USB/BOOT de tu revisión. En Windows
usa una ruta de proyecto/toolchain corta: GCC de esta plataforma puede fallar
con `CreateProcess: No such file or directory` en rutas excesivamente largas.

## Aprovisionamiento, sin credenciales en el código

Conecta por USB y abre un terminal **sin eco local ni registro de entradas**.
Envía `setup` y Enter. El firmware solicita, por líneas:

1. SSID Wi-Fi de 2,4 GHz.
2. Contraseña Wi-Fi, sin eco.
3. Clave compartida Base64 de 32 bytes, generada en Base externa de Windows.

Guarda la misma clave en Windows. No uses una contraseña humana como clave
compartida; no se admite la clave pública de los vectores de pruebas. Nunca
se imprime ni se transmite por TCP la clave. `status` muestra estado sin
secretos; al conectar se informa la IP para usarla si `.local` no resuelve.
`erase CONFIRM` borra las preferencias del dispositivo y desconecta.

En Windows: desmarca Simulador, host `victus-base.local` o la IP mostrada,
puerto 8765, clave compartida y modo deseado. Guarda y conecta.

**Limitación de almacenamiento:** Preferences/NVS de esta configuración Arduino
no cifra la flash. No ofrece la protección DPAPI de Windows. Protege físicamente
la placa; cifrado de flash/NVS y secure boot requieren un proceso de producción
aparte. No se guardan credenciales en archivos del repositorio o binarios de CI.

## Comportamiento

Autenticación mutua HMAC-SHA256, nonces aleatorios, claves por dirección, sesión
nueva por conexión y secuencias crecientes. Se verifica el MAC contra los bytes
originales del payload, sin reserializarlos. Mensajes de 4096 bytes, profundidad
8, máximo 256 valores, rechazo de claves duplicadas —incluidas escapadas—,
UTF-8 inválido, tipos, rangos, fechas antiguas y campos obligatorios ausentes.
Los campos desconocidos son aditivos, sin efecto en control.

Un único cliente activo; sockets no bloqueantes, entrada acotada y espera de
5 s por mensaje/autenticación, 3 s para completar una respuesta. Reconexión Wi-Fi
progresiva. La tarea de ventilación comprueba su lease cada 20 ms, separada
del socket, LCD y aprovisionamiento. Una Solicitud válida necesita una respuesta
del dispositivo enviada hace menos de 2000 ms entre las últimas 16. EOF, mensaje
rechazado, solicitud nula, sesión nueva, sensor inválido o lease de 2000 ms agotado
devuelven Local. Los duplicados no renuevan la vigencia.

La conversión Solicitud→PWM pertenece al ESP32. Escala inicial explícita en
`BoardConfig.h`: **0–54**, propia del controlador actual, modificable. Un valor
fuera de la calibración vuelve a Local; no se recorta silenciosamente.
Duty positivo mínimo inicial 25 %. Demanda, Solicitud y PWM son distintos.
No se usa Demanda como orden. En Local: 35 % hasta 25 °C, rampa hasta 100 % a
45 °C; NTC inválida o sin renovación durante 3 s fuerza 100 %. Son ajustes
iniciales que requieren banco.

Respuestas `deviceStatus`: ambiente, duty lógico que el firmware ordenó después
de compensar NOT, modo y comunicación, `extensions.slot1/slot2` siempre nulos.
**No existe tacómetro: aplicado no demuestra giro ni un PWM físico medido.**
El estado enviado refleja la última ejecución de la tarea de control y puede
confirmar un cambio en la siguiente respuesta, no su finalización física.
LCD alterna ambiente/salida y CPU/GPU/RAM/Demanda/Solicitud. Sin datos muestra `--`.

## Pruebas y límites

El harness `test/host.cpp` compila el mismo parser, autenticación, validación,
calibración y lease usados por la placa. Utiliza exclusivamente vectores públicos
y valores simulados. `test/test_protocol.py` verifica las firmas, entradas
inválidas, políticas, inversión y rollover del reloj. `tools/FirmwareInterop.Tests`
conecta el **cliente TCP de producción de Windows** con ese motor C++ mediante
un puente local de pruebas; comprueba respuestas fragmentadas y cambio a Firmware.
El CI compila además las dos imágenes reales ESP32-C3. El harness no es firmware
de producción y su clave/nonce públicos nunca deben aprovisionarse.

Antes de uso físico: medir GPIO y salida NOT a 0/25/50/100 %, verificar frecuencia,
arranque del ventilador, LCD/niveles I²C, NTC con termómetro y fallo abierto/corto,
Wi-Fi ausente, PC apagado, conexión parcialmente abierta, silencio y reinicio de
ambos extremos. Medir el retorno a Local en la salida real tras 2000 ms más el
periodo de tarea. No se afirma que esas pruebas hayan pasado.
