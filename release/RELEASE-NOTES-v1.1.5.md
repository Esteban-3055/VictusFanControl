# VictusFanControl v1.1.5 · preliminar

- Actualizador con **Descargar** e **Instalar v…** separados. La descarga verificada se conserva entre aperturas y al fallar la instalación; no se descarga otra vez para reintentar.
- **Recuperar sesiones** accesible desde Actualizaciones. Se conserva el instalador mientras se resuelven registros anteriores. No se omiten las comprobaciones de liberación.
- Consulta breve de la release exacta en GitHub y nueva verificación de tamaño/SHA-256 antes de cada instalación, sin volver a descargar el EXE; parciales, archivos alterados, versiones iguales/anteriores y previews sin selección explícita permanecen bloqueados. Adopción de descargas completas de v1.1.4 tras comprobarlas con la metadata solicitada.
- El instalador preserva por defecto las preferencias anteriores de inicio con Windows y Automático. Un reintento exitoso limpia el código de error previo.
- La apertura del instalador drena la sesión incluso si falla el bloqueo inicial, y deja de presentar CPU/GPU como activos tras liberar o perder el controlador.
- Las notificaciones de pantalla se conservan también sin un runtime abierto. Una verificación interrumpida por energía conserva el archivo y requiere volver a pulsar Instalar.
- Aplicar cambios CPU/GPU requiere sensores actuales sin campos críticos conservados. Los marcadores de curvas usan la misma entrada conservadora de utilización que el motor, sin inventar la lectura observada.

Revisión funcional y regresiones: `docs/PRODUCT_1_1_5_REVIEW.md`. Se mantienen las tolerancias de v1.1.4, los guardianes y la serialización de llamadas nativas. Las pruebas de software y replays no acreditan nuevas mediciones térmicas, ruido ni cualificación física de Windows/HP.
