# VictusFanControl 1.2.17

Actualización estable para HP 8C40 / 9D0R1LA / BIOS F.18.

- Corrige el arranque, reintento y reanudación de Automático: comprobar otra vez una lectura ya caducada no borra las adquisiciones buenas anteriores. Antes de conceder control se obtienen lecturas vigentes mediante el lector coordinado, sin ampliar su frescura.
- Corrige estados de interfaz fuera de orden y la pausa prematura al actualizar límites CPU/GPU mientras el canal del supervisor está ocupado. Conserva los perfiles y los registros de recuperación.
- Incluye la reconciliación de fuente AC/batería desde STATUS, también al arrancar con batería y cambiar después a AC. Una fuente informada por Windows no prueba desconexión física.
- Curva Externa: confirma correctamente la revisión entre reconexiones, y texto/punto comparten frescura. Windows configura; ESP32 ejecuta EMA, histéresis, confirmación de descenso y rampas.
- Base externa sin aumentos forzados por temperatura ambiente. Sin conexión, converge suavemente a PWM **130/255**. Corrige el asentamiento del máximo: una petición de 255 termina en 255 por la rampa; 254 no se convierte en máximo.
- **Máxima suavidad** prepara un borrador con subida/bajada 1 %/s, EMA 10000 ms, histéresis 3, confirmación 30000 ms y pulso desactivado. Requiere Guardar; no modifica automáticamente preferencias existentes.
- LCD1602: **30 s CPU/GPU + ambiente**, **5 s uso CPU/GPU/RAM**, **5 s demanda + PWM aplicado**, **5 s conexión PC/Wi-Fi**. Lecturas independientes y `--` cuando faltan.

Instala `VictusFanControl-1.2.17-Setup-win-x64.exe`, o busca actualizaciones desde el programa. El actualizador original 1.2.9 y las versiones hasta 1.2.16 deben descubrir esta versión sin activar preliminares. Se verifica la descarga íntegra sin ejecutar el instalador en la prueba de publicación.

Para aplicar las correcciones de la base, carga también `VictusBase-ESP32C3-v1.2.17-Arduino-sketch.zip`. Extrae el ZIP y abre `VictusBaseESP32C3/VictusBaseESP32C3.ino`. El PWM ya está activado. Usa core Espressif **3.3.2**, ArduinoJson **6.21.5**, placa **ESP32C3 Dev Module**, flash **4 MB**, **USB CDC On Boot habilitado** y **borrado completo de flash desactivado**. Mantiene GPIO3 invertido 18 Hz/14 bits, NTC GPIO0, I2C GPIO6/7, Wi-Fi 8,5 dBm, TCP HMAC 8765 y la misma NVS. No contiene SSID, contraseña ni clave privada; conserva los datos existentes si no borras la flash.

Se mantienen las protecciones internas HP y la GPU devuelve control a Firmware por encima de 95 °C. La base externa es independiente. Las llamadas nativas sin retorno o liberaciones inciertas conservan sus registros y bloqueo; no se promete recuperación universal sin reiniciar.

Los archivos incluyen hashes y procedencia del commit exacto. La validación es de software; siguen pendientes las comprobaciones físicas del arranque, AC/batería, LCD, NTC, PWM y circuito de potencia. No se declara un nuevo PASS físico.

Detalles: [revisión de coordinación](../docs/AUTOMATIC-COORDINATION-REVIEW.md), [política externa y desconexión](../docs/EXTERNAL-BASE-RAMPED-LOCAL.md), [LCD y ajustes](../docs/ESP32-LCD-RESPONSE-REVIEW.md), [recuperación](../docs/FORCED-SESSION-RECOVERY.md).
