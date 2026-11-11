# VictusFanControl 1.3.2

Mejora la continuidad de Automático cuando otras aplicaciones cargan la CPU y corrige bloqueos de recuperación de sesiones. Incluye una mejora de reconexión Wi-Fi para el firmware ESP32-C3, distribuida por separado.

- **Prioridad Alta de Windows** predeterminada para el programa y sus Guardian. Cada proceso comprueba y registra su prioridad efectiva, tanto al abrir manualmente como al iniciar Windows o crear una sesión nueva.
- La publicación de sensores deja de recorrer archivos de sesiones anteriores: un único lector en segundo plano mantiene la vista de recuperación. El inicio automático espera las comprobaciones en curso sin cancelar su solicitud y sigue exigiendo tres lecturas frescas antes de activar.
- Una liberación WMI que terminó después de una espera fallida puede confirmarse con el proceso y el informe originales. Evita quedarse atrapado en el error anterior sin repetir comandos nativos ni dar por resuelta una liberación incierta.
- Los registros distinguen reanudaciones automáticas y reintentos manuales. La conexión externa diferencia errores de resolución de nombre y conserva diagnósticos sin secretos.
- **Firmware ESP32:** reintento Wi-Fi cuando AutoReconnect queda estancado tras una interrupción de red y reintento del anuncio mDNS. Conserva el emparejamiento/NVS, la autenticación HMAC, los pines y las curvas. No reinicia la placa para intentar reconectar.

Actualiza desde el programa o descarga **VictusFanControl-1.3.2-Setup-win-x64.exe**. Conserva perfiles, curvas y preferencias. La actualización espera el cierre y la liberación de los propietarios; no fuerza su terminación ni borra journals pendientes.

La mejora Wi-Fi del ESP32 requiere cargar aparte **VictusBase-ESP32C3-v1.3.2-Arduino-sketch.zip**, con core Espressif **3.3.2**, ArduinoJson **6.21.5** y borrado completo de flash desactivado para conservar NVS. Actualizar Windows no actualiza automáticamente la placa. Las correcciones de prioridad y publicación funcionan con el firmware anterior.

La prioridad Alta reduce la competencia de CPU, pero no garantiza el retorno de un driver bloqueado. Se mantienen las comprobaciones de frescura, propiedad, recuperación exclusiva y protecciones térmicas internas. No se declaran nuevas pruebas físicas de arranque Windows, GPU, ventiladores, router o ESP32.

[Validación y límites](../docs/RELEASE-1.3.2-VALIDATION.md) · [Publicación y prioridad](../docs/TELEMETRY-SCHEDULING-RECOVERY.md) · [Reconexión ESP32](../docs/ESP32-WIFI-RECOVERY.md).
