# HyperX Battery y LCD1602

Corrección local del 29 de octubre de 2026: el informe de voltaje no confirma que haya un cargador conectado. El estado de carga se presenta como «No confirmado». Las respuestas válidas por encima de 4107 mV conservan el techo estimado de 100 % de la curva existente, hasta el límite de plausibilidad del decodificador (4500 mV); esto no calibra una capacidad nueva ni confirma carga física completa. Recuperar un micrófono activo después de un estado desconocido no anuncia que se quitó el silencio: ese aviso requiere un estado silenciado anterior confirmado.

Preliminar 1.3.8: la estimación Windows usa una curva continua con resolución de 1 %. Se mantiene como estimación por voltaje, sin afirmar una exactitud de ±1 %. Si la placa ya tiene firmware 1.3.7, este cambio no requiere volver a flashearla.

Se mantienen las funciones de 1.3.7: selector lateral centrado, batería crítica configurable entre 0 y 100 % y avisos LCD repetidos sin PC. Las placas con firmware anterior a 1.3.7 necesitan el sketch compatible incluido para estas funciones LCD. La 1.3.6 sigue como estable. El instalador Windows no flashea el ESP32 automáticamente.

## Lecturas y frecuencia

El lector C# usa las API HID de Windows, sin Python ni NGENUITY como dependencias del producto. Selecciona exclusivamente Cloud Flight `0951:1723` / `0951:16c4`, sus colecciones de estado `FF90` / `FF53`, uso `0303`, y los eventos `FFC0`, uso `0001`. No graba audio, cambia volumen, micrófono, iluminación ni firmware. No comparte autoridad con CPU/GPU, HP WMI o EC. El cierre USB opcional tiene un presupuesto de tres segundos; un driver retenido o fallido se registra y no bloquea el cierre del control ni el de la base externa.

Consulta al arrancar y, tras cada intento acotado a dos segundos, espera 60 segundos. Una respuesta válida aporta voltaje, porcentaje aproximado y hora real de adquisición. Se invalida en Windows a los 180 segundos, ante fallo de consulta o apagado observado. La UI y el exportador solo consumen snapshots; enviar la misma muestra al ESP32 no renueva su edad. La presencia del receptor no prueba que el audífono esté encendido. El estado inicial del micrófono es desconocido hasta recibir un evento.

Los eventos de silencio se escuchan con una lectura asíncrona bloqueada y cancelable: no hay consulta periódica del micrófono. Se conservan hasta 32 eventos para evitar que un refresco de UI pierda dos cambios próximos; los eventos de más de cinco segundos no producen avisos tardíos. Los informes desconocidos no se interpretan como quitar silencio.

La conversión de voltaje a porcentaje es aproximada. El cálculo interpola linealmente entre los límites inferiores de los tramos de la referencia srn (MIT), conservando 3584 mV → 10 % y 3970–4107 mV → 100 %. Rellena los huecos antiguos (por ejemplo 3763 o 4096 mV) y permite cualquier entero de 10 a 100, en lugar de saltos obligatorios de cinco puntos. Por ejemplo, 3795 mV produce una referencia de 56.25 %, mostrada inicialmente como 56 %; el 55 % anterior pertenecía a un tramo grueso, no a una medición exacta de carga.

Se aplica una EMA con constante de tiempo de 60 segundos, calculada con tiempo monótono entre adquisiciones reales, y una pequeña histéresis de presentación de 0.75 puntos para reducir rebotes de un entero. La primera lectura se muestra inmediatamente. El tramo que redondea a 10 % no espera al filtro para evitar retrasar el aviso crítico predeterminado. Cambios grandes válidos pueden saltar varios enteros: no se simula una cuenta atrás de 1 % por minuto. Una resolución de 1 % no garantiza una exactitud de ±1 %.

El filtro se reinicia tras un fallo de lectura, receptor ausente o distinto, apagado observado o hueco de adquisición de al menos 180 segundos. Un voltaje alto no reinicia el estimador ni establece carga activa. La hora UTC del snapshot sigue siendo la de la respuesta USB; refrescar la UI o reenviar el snapshot al ESP32 no actualiza esa hora ni avanza el filtro. La frecuencia de consulta sigue siendo aproximadamente una por minuto.

**El tramo de 0–9 % sigue sin calibración.** Una respuesta plausible inferior a 3584 mV conserva el voltaje recibido pero muestra porcentaje desconocido; no extrapola 0 % ni lo confunde con falta de datos. El umbral crítico configurable admite 0–100 %, pero esto no añade una medición de 0–9 %. El voltaje alto conserva una estimación de porcentaje; no se usa como prueba de carga activa. Alcanzar el umbral «completa» no demuestra terminación eléctrica de carga.

Cada respuesta válida puede dejar un registro opcional acotado `HYPERX BATTERY ESTIMATE` con voltaje, fecha de adquisición, referencia continua y valor publicado. Sirve para estudiar la zona baja durante el uso normal, sin nuevas consultas ni cambios al dispositivo. No registra rutas HID, identificadores del receptor, audio ni credenciales. Estos datos por sí solos no constituyen una calibración de capacidad restante. No se incorpora la curva polinómica de HeadsetControl.

## Pestaña y notificaciones

«HyperX Battery» está debajo de «Curva Externa». Muestra receptor, estado de encendido observado, voltaje, batería, carga no confirmada, micrófono y antigüedad de la lectura. Los ajustes se guardan aparte en `%LOCALAPPDATA%/VictusFanControl/hyperx-battery.json`; no modifican perfiles de ventiladores ni claves de emparejamiento.

La presentación se divide en **Información**, **Notificaciones** y **Pantalla LCD**, con las mismas tarjetas, colores, botones y deslizadores del programa. Guardar, Descartar y Restaurar quedan visibles en las tres secciones sin desplazamiento. Restaurar modifica el borrador; Guardar valida y persiste los ajustes. Un fallo de guardado conserva el borrador para corregirlo o reintentar. Los valores admiten ratón, teclado, controles accesibles y entrada numérica exacta al pulsar el número; las relaciones entre umbrales y entre duración/ciclos se validan al guardar.

Los avisos bajos/críticos se habilitan y configuran para Windows y LCD. Los avisos de nivel completo y los de silenciar/quitar silencio son de Windows y tienen interruptores propios. La implementación usa las notificaciones nativas de NotifyIcon; No molestar y los permisos de notificación de Windows pueden ocultarlas.

Predeterminados: baja 20 %, crítica 10 %, completa 100 %. Crítica admite 0–100 % independientemente de baja; si se superponen, crítica tiene prioridad. Se conserva baja < completa. Un aviso no se repite por cada refresco, ni por una desconexión/reconexión. Para rearmar baja/crítica hace falta una recuperación de cinco puntos porcentuales; completa se rearma por debajo del umbral menos cinco puntos. Configurar nuevos umbrales vuelve a evaluar los avisos Windows. LCD evalúa nuevas adquisiciones, no una reproducción de la caché.

## LCD y avisos prioritarios

La pantalla principal conserva 30 segundos y muestra, por ejemplo:

```text
CPU:55C GPU:42C
Amb:21C Hyp:55%
```

Las otras pantallas conservan cinco segundos cada una: uso CPU/GPU/RAM; demanda/PWM; conexión PC/Wi-Fi. Las cuatro etiquetas y sus valores caben en 16 columnas por dos filas, incluidos extremos y nulos `--`.

Si se pierde el PC, solo HyperX conserva el último porcentaje válido en RAM: `Hyp:55*`. El asterisco indica lectura anterior, no telemetría fresca. CPU/GPU caducan normalmente; el NTC sigue siendo local. No se calcula descarga sin PC ni se inventa un porcentaje nuevo. La caché no sobrevive a un reinicio del ESP32 y no se escribe en NVS.

Una nueva adquisición autenticada al entrar en baja o crítica activa una pantalla por encima de cualquiera de las cuatro normales. Crítica interrumpe baja. Baja usa inicialmente **6 s / 3 ciclos**; crítica **10 s / 5 ciclos**. La duración es total, incluidos los intervalos en blanco. Cada ciclo alterna texto visible y ambas filas en blanco, sin bloquear el bucle ni cambiar la luz de fondo. Con el PC conectado, al terminar se retoma la posición actual del ciclo normal. Un dato fresco recuperado cancela un aviso obsoleto.

Duraciones configurables: 1–120 segundos; ciclos: 1–20. Debe existir al menos un segundo por ciclo para que el refresco físico de 250 ms pueda representar las dos fases. Desactivar una categoría cancela su aviso activo. Sin PC (sin sesión autenticada, sin red o enlace de datos vencido), si el último porcentaje conocido está en baja/crítica habilitada, su aviso se repite indefinidamente por encima de las pantallas normales. Se reutilizan duración y ciclos configurados como patrón repetido y se muestra `Ult: 20% *` para identificar un dato anterior. También se inicia esta repetición si el aviso conectado ya había terminado antes de perderse el PC. Al reconectar cesa la repetición; no se inventa una nueva adquisición ni se rearma por un mensaje duplicado. Sin porcentaje conocido o con las categorías desactivadas, no hay aviso repetido.

## Media ambiente de 40 muestras

Cada 100 ms se convierte una adquisición NTC validada a grados Celsius, conservando el prepromedio ADC existente de ocho lecturas raw/mV. Se calcula la media móvil aritmética de las últimas **40 temperaturas**, unos cuatro segundos. Se publica `--` hasta llenar la ventana. Un dato inválido o una interrupción de adquisición de tres segundos borra la ventana; se requieren nuevamente 40 muestras válidas. La caducidad local de tres segundos sigue vigente. No se mezclan temperaturas CPU/GPU ni se usa HyperX para controlar ventiladores.

## Validación y límites

Pruebas aisladas: `--hyperx-self-test`, `--hyperx-ui-self-test` (también dentro de `--product-gui-self-test`); scripts generales de ExternalCooling y estabilidad. El harness C++ prueba layout, media NTC, caducidad, retención sin PC, parpadeo exacto, prioridades, no repetición y rollover. FirmwareInterop verifica porcentaje y todos los parámetros entre el cliente TCP de producción y el parser C++ real con HMAC.

La estimación continua se prueba en todo el intervalo de milivoltios soportado: monotonía, todos los enteros de 10–100, antiguos huecos, ausencia de extrapolación, intervalos irregulares, oscilaciones, extremo crítico, llegada a 100 y reinicios de estado. También se comprueba la ruta de producción reporte HID → snapshot → exportador/protocolo, con respuestas sintéticas y sin abrir USB; los reenvíos no renuevan la antigüedad y la carga inferida no inventa un porcentaje. Las mediciones físicas de exactitud y del tramo bajo siguen pendientes.

La prueba de interfaz de 1.3.6 genera nueve capturas (las tres secciones a anchuras 1040, 1344 y 1672), verifica límites y controles visibles, teclado/accesibilidad, entrada exacta, guardado, descarte, errores de persistencia y cambio de sección durante un arrastre. Usa un lector simulado y comprueba cero órdenes al hardware y ningún cambio en los ajustes de ventiladores del portátil. Estas capturas verifican la interfaz, no el comportamiento físico de los audífonos o del LCD.

Prueba física acotada del lector C#: dos consultas separadas aproximadamente 60 s y eventos reales de silenciar/quitar silencio recibidos correctamente. Esto no valida físicamente carga USB completa, presentación de notificaciones Windows ni la nueva imagen en un ESP32. No se flasheó, alteró NVS o aplicaron cambios a ventiladores/CPU/GPU.

Referencias de protocolo: [srn (MIT)](https://github.com/srn/hyperx-cloud-flight-wireless), [HeadsetControl](https://github.com/Sapd/HeadsetControl/blob/master/lib/devices/hyperx_cloud_flight.hpp), [colecciones de la revisión 1723](https://github.com/vguasta/hyperx-cloud-flight-battery-tray/blob/main/index.js). La tabla srn se adapta con su licencia en THIRD_PARTY_NOTICES; no se incorpora código ni la curva polinómica GPL de HeadsetControl.
