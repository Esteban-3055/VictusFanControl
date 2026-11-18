# VictusFanControl 1.3.5 · Preliminar · HyperX y LCD

Añade **HyperX Battery** debajo de Curva Externa: batería aproximada, voltaje, carga inferida, estado de micrófono y antigüedad de la lectura de los Cloud Flight compatibles. Consulta la batería al arrancar y cada 60 segundos; escucha los cambios de silencio mediante eventos USB, sin sondeo continuo del micrófono. No requiere Python ni NGENUITY.

Incluye avisos Windows configurables de batería baja, crítica, nivel completo y silenciar/quitar silencio. Los porcentajes son estimados: «completa» avisa al alcanzar el umbral elegido, no confirma la terminación eléctrica de carga. No molestar y los ajustes de Windows pueden ocultar las notificaciones.

El LCD1602 muestra **CPU / GPU / Amb / Hyp** en su pantalla principal de 30 segundos. Las otras tres pantallas conservan cinco segundos. El ambiente usa una media móvil de **40 temperaturas NTC**, adquiridas cada 100 ms; una muestra inválida o un hueco de adquisición borra la ventana y se muestra `--` hasta completarla.

Si se pierde el PC, el LCD conserva el último porcentaje HyperX en RAM como `Hyp:55*`: el asterisco indica lectura anterior. Sin PC no puede detectar nuevas bajadas. Los avisos LCD de batería baja y crítica interrumpen cualquier pantalla una vez al entrar en cada umbral; crítica tiene prioridad. Se configuran duración total y ciclos de parpadeo por separado. Valores iniciales: baja 20 %, 6 s / 3 ciclos; crítica 10 %, 10 s / 5 ciclos.

**Para disponer del nuevo LCD hay que actualizar el programa y cargar el firmware ESP32 de esta versión.** El instalador Windows no flashea la placa. El sketch Arduino se incluye entre los archivos de la release. Se conservan NVS/emparejamiento, GPIO3 PWM invertido 18 Hz/14 bits, GPIO0 NTC, GPIO6/7 I2C, Wi-Fi 8.5 dBm y HMAC TCP 8765; no se requiere borrar toda la memoria flash.

La telemetría HyperX es independiente de ventiladores y límites CPU/GPU. Incluye cierre USB opcional acotado, lectura en segundo plano, caducidad y rechazo de datos inválidos. Conserva las funciones y correcciones de 1.3.4.

Validación de software: interfaz aislada, persistencia, alertas, 19 grupos generales y 5 de estabilidad, parser y cliente autenticados, media NTC, retención, prioridades y tiempos de parpadeo. El lector se probó físicamente con los audífonos y recibió batería y ambos estados de silencio. No se afirma validación física de la nueva imagen ESP32, del final de carga USB ni de la aparición de notificaciones Windows. La documentación y procedencia publicadas delimitan cada prueba.

Es una **preliminar**: activa «Incluir versiones preliminares» para recibirla mediante el actualizador. La estable 1.3.2 permanece disponible.

[Funcionamiento, parámetros y límites](../docs/HYPERX-BATTERY-LCD.md).
