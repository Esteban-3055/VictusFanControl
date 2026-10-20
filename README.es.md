# VictusFanControl v1.3.1

[English](README.md) · [Release estable](https://github.com/Esteban-3055/VictusFanControl/releases/tag/v1.3.1) · [Cambios](release/RELEASE-NOTES-v1.3.1.md) · [Guía de uso](docs/PRODUCT_V1.md)

Control de ventiladores y preferencias CPU/GPU para **HP 8C40 / 9D0R1LA / BIOS F.18** (Victus 15-fa1xxx, revisión 63.43, i7-13700H y RTX 4060 Laptop GPU). Otros modelos y BIOS no están autorizados por esta release.

## Actualizar Windows

Descarga **VictusFanControl-1.3.1-Setup-win-x64.exe** o busca actualizaciones desde el programa. Necesita Windows 11 x64, .NET Desktop Runtime 8 x64, PawnIO y NVIDIA/NVML. Conserva perfiles y curvas. Una sesión sin liberar conserva sus registros y bloquea nuevas órdenes.

También puedes extraer **VictusFanControl-1.3.1-win-x64.zip**, salir de la instancia anterior desde la bandeja y ejecutar `Start-ProductGui.ps1 -Mode Install` como administrador con la misma cuenta. Cerrar la ventana minimiza; **Salir** desde la bandeja libera el control.

## Cambios principales

- Corrige la casilla «Iniciar con Windows» de 1.3.0: reconoce el nombre de cuenta que devuelve el Programador de tareas y confirma el estado real sin pedir guardar Automático. [Causa y pruebas](docs/RELEASE-1.3.1-VALIDATION.md).
- 1.3.1 corrige cierres que omitían la limpieza, la recuperación GPU con error devuelto y la pérdida de la última respuesta del supervisor. Comprueba la tarea de batería, separa la caché del arranque y serializa la instalación. [Auditoría y pruebas](docs/RELEASE-1.3.1-VALIDATION.md).
- Corrige la espera inicial de Automático, las lecturas caducadas durante la preparación, los estados fuera de orden y la pausa prematura al actualizar límites.
- Reconcilia perfiles AC/batería con confirmación nueva del supervisor. Una lectura batería de Windows no demuestra desconexión física.
- Conserva protecciones térmicas internas: GPU devuelve el control a Firmware por encima de 95 °C. El consumo CPU >60 W por sí solo no interrumpe Automático habitual; GPU configurable hasta 2535 MHz.
- Recuperación definitiva y reanudación acotada conservan evidencia y no admiten llamadas nativas inciertas. No garantizan recuperar cualquier driver bloqueado sin reiniciar.
- Uso normal sin TZ01/DTT3 ni acceso EC directo. Los ventiladores internos y la base externa tienen control independiente.

## Subir el firmware ESP32

El firmware conserva el código de 1.2.17. Si ya lo cargaste, no necesitas volver a programarlo para estas correcciones Windows.

Descarga **VictusBase-ESP32C3-v1.3.1-Arduino-sketch.zip**, extrae todo y abre `VictusBaseESP32C3/VictusBaseESP32C3.ino`. Ya tiene PWM activado. Instala core Espressif **3.3.2** y **ArduinoJson 6.21.5**; selecciona **ESP32C3 Dev Module**, flash **4 MB**, **USB CDC On Boot habilitado**, puerto USB correcto y **borrado completo de flash desactivado** para conservar el emparejamiento/NVS.

ESP32 ejecuta EMA, histéresis y rampas con los ajustes enviados desde Windows. La temperatura ambiente es informativa; al desconectar, converge suavemente a **130/255**. LCD: CPU/GPU + ambiente 30 s; uso CPU/GPU/RAM 5 s; demanda + PWM aplicado 5 s; conexión PC/Wi-Fi 5 s. Los datos ausentes o caducados muestran `--`.

Conserva GPIO3 invertido 18 Hz/14 bits, NTC GPIO0, I2C GPIO6/7, Wi-Fi 8,5 dBm y HMAC TCP 8765. Los archivos públicos no contienen claves, SSID ni contraseñas. [Detalles de firmware, circuito y emparejamiento](firmware/esp32-c3/README.md).

## Validación

Los perfiles históricos de calificación conservan `control.enabledByDefault=false`
y `automaticPolicyEnabled=false`. Esos hosts experimentales permanecen separados
del producto autorizado para este equipo y de sus preferencias explícitas de inicio.

Los paquetes incluyen hashes y procedencia del commit. Las pruebas Windows, C++ y comunicación local, y las compilaciones PlatformIO/Arduino, son validación de software. La comprobación física del arranque Windows, transiciones AC/batería, LCD, NTC, PWM y circuito sigue pendiente para esta versión.

[Informe por caso](docs/AUTOMATIC-COORDINATION-REVIEW.md) · [Recuperación](docs/FORCED-SESSION-RECOVERY.md) · [Política externa](docs/EXTERNAL-BASE-RAMPED-LOCAL.md) · [Avisos de terceros](THIRD_PARTY_NOTICES.md)
