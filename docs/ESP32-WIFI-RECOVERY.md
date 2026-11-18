# ESP32 sin conexión después de reiniciar el router

## Evidencia y límites

El usuario confirmó «Wi-Fi desconectado» en la LCD, alimentación independiente del ESP32 y un reinicio previo del router. La instalación actual del PC ya era 1.3.1. La configuración externa seguía habilitada, con reconexión automática y emparejamiento persistente válido; la comprobación de descifrado no mostró ni guardó la clave. Windows no resolvía el nombre de la placa (`SocketError.HostNotFound`, 11001). No se observó conexión TCP activa al puerto configurado.

Esto sitúa el primer problema observable en la conexión Wi-Fi del ESP32. La falta de resolución en Windows es compatible con una placa fuera de la red. No demuestra el motivo exacto de la desconexión, ni permite atribuirlo al inicio del PC: la placa mantiene alimentación independiente. No había puerto serie disponible para leer el motivo real. El reinicio del router es un antecedente plausible, no una prueba del código de error recibido por esta placa.

El firmware llamaba a `WiFi.begin` una vez y dependía después de AutoReconnect. Tanto [Arduino ESP32 3.3.2](https://github.com/espressif/arduino-esp32/blob/3.3.2/libraries/WiFi/src/STA.cpp) como [2.0.17](https://github.com/espressif/arduino-esp32/blob/2.0.17/libraries/WiFi/src/WiFiGeneric.cpp) seleccionan los motivos que reintentan. Por ejemplo, AUTH_FAIL no pertenece a esa lista después del primer reintento general. No se afirma que ese fuera el motivo físico de esta incidencia. Si el driver deja de intentar, `wifiStarted` impedía otra intervención del firmware. Además, un fallo de `MDNS.begin` podía dejar TCP iniciado sin volver a anunciar el nombre.

## Cambios

- Watchdog independiente de las curvas y la LCD: solo solicita `WiFi.reconnect` cuando la estación permanece desconectada y han pasado al menos 60 s sin eventos. Esperas entre solicitudes: 60/120/240/300 s. Los eventos de asociación, desconexión o IP posponen la intervención. Una conexión válida o el aprovisionamiento reinician el contador.
- No repite `WiFi.begin`, no reinicia la placa, no alterna el modo de radio y no borra NVS ni cambia credenciales. Se mantiene AutoReconnect y la potencia de 8,5 dBm. Un error permanente de red/credenciales puede seguir impidiendo conectar; el watchdog no promete resolverlo.
- Reintento de mDNS cada 5 s si su inicio falla, sin reiniciar TCP. La pérdida de Wi-Fi sigue retirando el enlace remoto y sus solicitudes; reconectar exige una nueva autenticación HMAC y mensajes frescos.
- `status` y `wifi` por serie aportan motivo numérico, desconexiones y reintentos, sin secretos. No se publica SSID, contraseña o clave.
- Windows distingue ahora fallo de resolución de nombre, conexión rechazada, autenticación y tiempo agotado. Conserva el motivo mientras espera la reconexión. El registro de sesión contiene solo estados, categorías y contadores; no copia mensajes del peer, nombre de destino, clave o pruebas HMAC.

GPIO3 invertido a 18 Hz/14 bits, NTC GPIO0, I2C GPIO6/7, TCP 8765, HMAC, NVS, curva, rampas y comportamiento Local a 130 permanecen con su contrato anterior.

## Inicio con Windows

El programa configura la conexión externa al abrirse, sin esperar a Automático interno ni a entrar en su página. Si la base o la red no están disponibles y la reconexión está habilitada, sigue intentando con esperas de 0,5/1/2/4/8/16/30 s y jitter de hasta 249 ms. El nuevo watchdog corresponde al lado ESP32. Reiniciar Windows no es parte del procedimiento de recuperación.

Una prueba real de TCP en loopback inicia el gestor cuando la base no existe, comprueba que no inventa conexión o telemetría, inicia la base después y verifica conexión autenticada automática. También comprueba que desactivar la reconexión o desconectar explícitamente conserva la intención del usuario. Esto verifica la coordinación del programa, no un nuevo arranque físico de Windows.

## Validación

- Compilación .NET Release: cero errores y advertencias.
- Transporte y GUI aislados: PASS, incluyendo base inicialmente ausente, nueva autenticación, suspensión/reanudación, bandeja, emparejamiento DPAPI y registro sin campos opacos.
- Host C++ con el watchdog de producción: PASS para conexión inicial estancada, pérdida del router, eventos activos, backoff, aprovisionamiento y vuelta del reloj millis.
- Protocolo real C++: 35/35; curvas autenticadas, frescura LCD y exportador Arduino canónico: PASS. Sin cambios en formato de mensajes o pruebas HMAC.
- Compilaciones de placa Arduino 3.3.2/ArduinoJson 6.21.5 y PlatformIO: deben confirmarse sobre este commit antes de distribuir una imagen. El entorno local de PlatformIO no pudo lanzar sus auxiliares RISC-V; no se atribuye ese fallo de herramientas al firmware.

No se ha cargado firmware ni alterado la red del usuario. Falta probar físicamente un reinicio del router y confirmar la reconexión del ESP32. Estos cambios son posteriores a la release 1.3.1 y no se incorporan modificando sus assets publicados.
