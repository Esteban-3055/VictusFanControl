# VictusFanControl 1.3.0

Versión estable para HP 8C40 / 9D0R1LA / BIOS F.18. Consolida la recuperación de sesión y las correcciones de arranque investigadas después de 1.2.17.

- Recupera una liberación GPU que devolvió error, incluido NVML 999, sin quedar atrapado en la tarea fallida anterior. Primero espera la salida real del propietario y el cierre de los demás recursos; después exige la sesión exacta, respaldo y resultado validado del recuperador exclusivo. Nunca borra un registro para desbloquear el control.
- Si se pierde la última respuesta del supervisor, valida un recibo persistente con nonce, equipo, identidad del padre, proceso hijo, hora de creación y sesión GPU. Un código de salida por sí solo no habilita la recuperación ni demuestra que Reset haya funcionado.
- Un fallo previo de reintento, recuperación, actualización, inicio o cancelación ya no omite la limpieza final. Un error de la base externa tampoco deja el cierre esperando indefinidamente una señal que nunca llega.
- Al cerrar Windows, la aplicación espera y atiende las continuaciones de su cierre durante un plazo acotado. Cancelar el cierre de Windows conserva la sesión. Una operación nativa incierta conserva sus registros.
- Un diagnóstico que no puede guardarse ya no impide cerrar normalmente la sesión. La recuperación que realiza escrituras sigue exigiendo sus respaldos y comprobaciones.
- La consulta opcional del inicio con Windows y la lectura de la caché de actualizaciones no bloquean el arranque ni la recuperación. Se comprueban también los argumentos y las opciones de batería de la tarea. El inventario temporalmente ilegible se vuelve a consultar con esperas acotadas.
- La sustitución del controlador confirma su frontera de propiedad al terminar la preparación. La recuperación vuelve a comprobar las notificaciones de energía antes de iniciar el nuevo controlador.
- Se limita el archivo de perfiles a 1 MiB, conservando el original si es inválido. La instalación se serializa, publica la carpeta con un cambio de nombre y restaura perfiles mediante reemplazo atómico si falla el registro de inicio.
- Un lector NVML cuya terminación no se haya confirmado conserva su identidad e impide generar más lectores hasta que salga. Esto solo corresponde a lectura; no se termina por la fuerza ningún supervisor que pueda escribir en el hardware.

Descarga `VictusFanControl-1.3.0-Setup-win-x64.exe` o busca actualizaciones desde el programa. Conserva perfiles, curvas y preferencias de inicio. El actualizador original 1.2.9 reconoce la versión numérica 1.3.0 sin activar preliminares.

El firmware incluido conserva el código de 1.2.17; si ya lo cargaste, estas correcciones Windows no requieren volver a programar la placa. Para una placa anterior, usa `VictusBase-ESP32C3-v1.3.0-Arduino-sketch.zip`: core Espressif **3.3.2**, ArduinoJson **6.21.5**, ESP32C3 Dev Module, flash 4 MB, USB CDC habilitado y borrado completo desactivado para conservar NVS. PWM activado, GPIO3 invertido 18 Hz/14 bits, NTC GPIO0, I2C GPIO6/7, Wi-Fi 8,5 dBm, TCP HMAC 8765. No contiene configuración privada.

La base externa mantiene Local suave hacia 130/255 y su LCD 30/5/5/5 s: CPU/GPU + ambiente, uso CPU/GPU/RAM, demanda + aplicado y PC/Wi-Fi. Windows configura la curva; ESP32 ejecuta su EMA, histéresis, confirmación y rampas. Los ventiladores internos HP conservan su controlador y protecciones independientes, incluida la devolución a Firmware con GPU válida por encima de 95 °C.

Las pruebas de software no equivalen a otra validación física del equipo o del ESP32. Un proveedor nativo que nunca retorna sigue requiriendo conservar la exclusión y los registros; esta versión no promete poder recuperar cualquier avería del driver. No reinicia Windows automáticamente.

[Correcciones y alcance de las pruebas](../docs/RELEASE-1.3.0-VALIDATION.md) · [Investigación previa](../docs/EXHAUSTIVE-STARTUP-INVESTIGATION-2026-10-20.md)
