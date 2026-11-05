# VictusFanControl 1.3.3 · Preliminar de estabilidad

Mejora el arranque, la continuidad de Automático y la recuperación tras errores temporales. La ventana se abre mientras prepara preferencias e inventarios; una lectura lenta de disco, la presentación o un ZIP de diagnóstico ya no retienen el camino de control.

- Publicación del último estado con colas acotadas, curvas aplicadas inmutables e historial de fases para investigar bloqueos.
- Una política compartida de recuperación, esperas de 10/30/60 s y reentrada desatendida tras 30 s de adquisiciones nuevas y completas. El presupuesto solo se renueva tras diez minutos de Automático fresco; un registro inválido o una liberación incierta no se reintentan a ciegas.
- Inicialización NVIDIA con fases, plazo absoluto y prioridad Alta explícita; fallos parciales no reconstruyen los lectores sanos. Limpieza de recursos adquiridos cuando falla un constructor.
- Suspensión sin esperar al driver en el mensaje de Windows. La reanudación espera liberación confirmada y descarta publicaciones anteriores a la frontera.
- Estado de inicio con Windows honesto, respaldo y verificación de cambios; hasta tres relanzamientos separados un minuto. Instalador y aplicación comparten exclusión.

Es una preliminar. Las pruebas son de software y no certifican recuperación de un driver que nunca retorna. Conserva exclusión, journals, frescura, identidad del equipo y confirmación nativa. No reinicia Windows ni elimina evidencia para desbloquear control.

El prototipo de separación entre motor y visor es una herramienta de investigación sin hardware y queda fuera del paquete normal. No se ha promovido a producción.

Firmware ESP32, protocolo, NVS y parámetros eléctricos conservados; estas reparaciones Windows no requieren volver a programar la placa. Sin claves, SSID ni contraseñas en el paquete.

Validación y observaciones pendientes: [informe de estabilidad](../docs/STABILITY-RECOVERY-HARDENING.md). Antes de promover a estable faltan uso habitual, AC/batería, recuperación real y próximo arranque normal de Windows sobre esta candidata. La instalación actual no se sustituye durante las pruebas.
