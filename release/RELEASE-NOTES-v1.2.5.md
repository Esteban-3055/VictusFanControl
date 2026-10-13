# VictusFanControl v1.2.5 · estable

- **Giro por RPM medidas**: cada ventilador de Inicio sigue sus propias últimas RPM válidas. La referencia visual es una vuelta por segundo a **3000 RPM**, una vez estabilizada la inercia.
- **Inercia continua**: los retrasos o lecturas inválidas no reinician el movimiento. Un cero medido desacelera y detiene el ventilador correspondiente. Funciona en Automático, Manual y Firmware.
- **Aspas iguales y cifras estables**: sin aspa blanca, punto marcador, texto «última lectura» ni cambio de color durante retrasos. Antes de la primera lectura se muestra Esperando.
- **Pausa al dejar Inicio**: cambiar de pestaña, ocultar o minimizar detiene el temporizador conservando posición y velocidad. Al volver se reanuda sin saltos ni reinicios de inercia.

Conserva perfiles, curvas, preferencias, control físico sincronizado, seguridad y recuperación automática. La retención es exclusiva de presentación; los gráficos históricos conservan los huecos de adquisición. Si la telemetría se pierde por completo, se conserva la última observación hasta recibir una nueva.

La publicación exige comprobaciones Windows de interfaz, inercia, retención independiente, cero medido, pausa/restauración, recursos, regresiones y paquete/instalador; se adjuntan SHA-256 y procedencia del mismo commit. No se afirma una nueva validación física.

[Detalles técnicos](https://github.com/Esteban-3055/VictusFanControl/blob/v1.2.5/docs/HOME-MEASURED-RPM-ANIMATION.md).
