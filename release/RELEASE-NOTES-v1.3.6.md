# VictusFanControl 1.3.6 · Estable · HyperX Battery

HyperX Battery adopta el mismo diseño que las otras secciones: tarjetas, colores, botones y deslizadores comunes. Se organiza en **Información**, **Notificaciones** y **Pantalla LCD**, con Guardar, Descartar y Restaurar siempre visibles. Permite editar con ratón, teclado, controles accesibles o introduciendo el valor exacto. Conserva el borrador si no se puede guardar y evita cambios durante un guardado en curso.

Mantiene las funciones HyperX de 1.3.5: consulta de batería al arrancar y cada 60 segundos, voltaje y porcentaje aproximado, eventos de silencio y avisos configurables en Windows. El LCD muestra CPU / GPU / Amb / Hyp, conserva el último porcentaje al perder el PC y dispone de alertas prioritarias de batería baja/crítica con duración y parpadeos configurables. El ambiente conserva la media móvil de 40 temperaturas NTC.

**Si ya cargaste el firmware de 1.3.5, no necesitas volver a flashear el ESP32.** Su código funcional no cambia en 1.3.6. Para placas con versiones anteriores se incluye el sketch compatible. El instalador Windows no flashea la placa; se conservan NVS y emparejamiento, GPIO3 PWM invertido 18 Hz/14 bits, GPIO0 NTC, GPIO6/7 I2C, Wi-Fi 8.5 dBm y HMAC TCP 8765.

Se publica como **estable**, por solicitud del usuario. El actualizador puede ofrecerla sin activar «Incluir versiones preliminares». No cambia las curvas, la autoridad de ventiladores internos, las protecciones CPU/GPU ni las credenciales.

Validación de software: nueve presentaciones de interfaz en tres tamaños, navegación y edición, accesibilidad, persistencia y fallos de guardado, además de las suites generales y de estabilidad. La publicación requiere los seis grupos de comprobaciones de la misma revisión, integridad de los archivos y descarga/verificación con el actualizador original 1.2.9, sin ejecutar el instalador. Los registros públicos detallan los resultados efectivos.

El canal estable no constituye una nueva validación física. Se conserva la evidencia anterior del lector HyperX; quedan pendientes la imagen en un ESP32 físico, la terminación de carga y la presentación real de avisos Windows, además de las comprobaciones del portátil detalladas en la procedencia. Las pruebas de esta actualización usan simulación y no modifican la instalación activa.

[Funcionamiento, parámetros y límites](../docs/HYPERX-BATTERY-LCD.md).
