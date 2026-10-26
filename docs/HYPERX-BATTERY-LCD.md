# HyperX Battery y LCD1602

Preliminar 1.3.5; requiere actualizar Windows y el firmware para disponer de todas las funciones. No cambia la instalación activa ni flashea el ESP32 automáticamente.

## Lecturas y frecuencia

El lector C# usa las API HID de Windows, sin Python ni NGENUITY como dependencias del producto. Selecciona exclusivamente Cloud Flight `0951:1723` / `0951:16c4`, sus colecciones de estado `FF90` / `FF53`, uso `0303`, y los eventos `FFC0`, uso `0001`. No graba audio, cambia volumen, micrófono, iluminación ni firmware. No comparte autoridad con CPU/GPU, HP WMI o EC. El cierre USB opcional tiene un presupuesto de tres segundos; un driver retenido o fallido se registra y no bloquea el cierre del control ni el de la base externa.

Consulta al arrancar y, tras cada intento acotado a dos segundos, espera 60 segundos. Una respuesta válida aporta voltaje, porcentaje aproximado y hora real de adquisición. Se invalida en Windows a los 180 segundos, ante fallo de consulta o apagado observado. La UI y el exportador solo consumen snapshots; enviar la misma muestra al ESP32 no renueva su edad. La presencia del receptor no prueba que el audífono esté encendido. El estado inicial del micrófono es desconocido hasta recibir un evento.

Los eventos de silencio se escuchan con una lectura asíncrona bloqueada y cancelable: no hay consulta periódica del micrófono. Se conservan hasta 32 eventos para evitar que un refresco de UI pierda dos cambios próximos; los eventos de más de cinco segundos no producen avisos tardíos. Los informes desconocidos no se interpretan como quitar silencio.

La conversión de voltaje a porcentaje es aproximada y escalonada, con extremo inferior de 10 %. Por eso el umbral crítico admite desde 10 %. Durante la indicación inferida de carga puede no haber porcentaje; nunca se inventa 100 %. Alcanzar el umbral «completa» no demuestra terminación eléctrica de carga.

## Pestaña y notificaciones

«HyperX Battery» está debajo de «Curva Externa». Muestra receptor, estado de encendido observado, voltaje, batería, carga inferida, micrófono y antigüedad de la lectura. Los ajustes se guardan aparte en `%LOCALAPPDATA%/VictusFanControl/hyperx-battery.json`; no modifican perfiles de ventiladores ni claves de emparejamiento.

Los avisos bajos/críticos se habilitan y configuran para Windows y LCD. Los avisos de nivel completo y los de silenciar/quitar silencio son de Windows y tienen interruptores propios. La implementación usa las notificaciones nativas de NotifyIcon; No molestar y los permisos de notificación de Windows pueden ocultarlas.

Predeterminados: baja 20 %, crítica 10 %, completa 100 %. Se exige crítica < baja < completa. Un aviso no se repite por cada refresco, ni por una desconexión/reconexión. Para rearmar baja/crítica hace falta una recuperación de cinco puntos porcentuales; completa se rearma por debajo del umbral menos cinco puntos. Configurar nuevos umbrales vuelve a evaluar los avisos Windows. LCD evalúa nuevas adquisiciones, no una reproducción de la caché.

## LCD y avisos prioritarios

La pantalla principal conserva 30 segundos y muestra, por ejemplo:

```text
CPU:55C GPU:42C
Amb:21C Hyp:55%
```

Las otras pantallas conservan cinco segundos cada una: uso CPU/GPU/RAM; demanda/PWM; conexión PC/Wi-Fi. Las cuatro etiquetas y sus valores caben en 16 columnas por dos filas, incluidos extremos y nulos `--`.

Si se pierde el PC, solo HyperX conserva el último porcentaje válido en RAM: `Hyp:55*`. El asterisco indica lectura anterior, no telemetría fresca. CPU/GPU caducan normalmente; el NTC sigue siendo local. No se calcula descarga sin PC ni se inventa un porcentaje nuevo. La caché no sobrevive a un reinicio del ESP32 y no se escribe en NVS.

Una nueva adquisición autenticada al entrar en baja o crítica activa una pantalla por encima de cualquiera de las cuatro normales. Crítica interrumpe baja. Baja usa inicialmente **6 s / 3 ciclos**; crítica **10 s / 5 ciclos**. La duración es total, incluidos los intervalos en blanco. Cada ciclo alterna texto visible y ambas filas en blanco, sin bloquear el bucle ni cambiar la luz de fondo. Al terminar se retoma la posición actual del ciclo normal. Un dato fresco recuperado cancela un aviso obsoleto.

Duraciones configurables: 1–120 segundos; ciclos: 1–20. Debe existir al menos un segundo por ciclo para que el refresco físico de 250 ms pueda representar las dos fases. Desactivar una categoría cancela su aviso activo. La pérdida de PC no reinicia un aviso ni vuelve a generarlo por el último valor almacenado.

## Media ambiente de 40 muestras

Cada 100 ms se convierte una adquisición NTC validada a grados Celsius, conservando el prepromedio ADC existente de ocho lecturas raw/mV. Se calcula la media móvil aritmética de las últimas **40 temperaturas**, unos cuatro segundos. Se publica `--` hasta llenar la ventana. Un dato inválido o una interrupción de adquisición de tres segundos borra la ventana; se requieren nuevamente 40 muestras válidas. La caducidad local de tres segundos sigue vigente. No se mezclan temperaturas CPU/GPU ni se usa HyperX para controlar ventiladores.

## Validación y límites

Pruebas aisladas: `--hyperx-self-test`, `--hyperx-ui-self-test` (también dentro de `--product-gui-self-test`); scripts generales de ExternalCooling y estabilidad. El harness C++ prueba layout, media NTC, caducidad, retención sin PC, parpadeo exacto, prioridades, no repetición y rollover. FirmwareInterop verifica porcentaje y todos los parámetros entre el cliente TCP de producción y el parser C++ real con HMAC.

Prueba física acotada del lector C#: dos consultas separadas aproximadamente 60 s y eventos reales de silenciar/quitar silencio recibidos correctamente. Esto no valida físicamente carga USB completa, presentación de notificaciones Windows ni la nueva imagen en un ESP32. No se flasheó, alteró NVS o aplicaron cambios a ventiladores/CPU/GPU.

Referencias de protocolo: [srn (MIT)](https://github.com/srn/hyperx-cloud-flight-wireless), [HeadsetControl](https://github.com/Sapd/HeadsetControl/blob/master/lib/devices/hyperx_cloud_flight.hpp), [colecciones de la revisión 1723](https://github.com/vguasta/hyperx-cloud-flight-battery-tray/blob/main/index.js). La tabla srn se adapta con su licencia en THIRD_PARTY_NOTICES; no se incorpora código ni la curva polinómica GPL de HeadsetControl.
