# VictusFanControl 1.3.8 · Preliminar · HyperX con resolución de 1 %

La batería HyperX deja de tener saltos obligatorios de cinco puntos. Windows interpola una curva continua entre las referencias de voltaje existentes y publica porcentajes enteros como 54, 55 o 56 %. Corrige también los huecos de la tabla anterior que podían dejar ciertos voltajes sin porcentaje. Es una **estimación por voltaje**: una resolución de 1 % no garantiza una exactitud de ±1 %.

El suavizado usa únicamente adquisiciones USB nuevas y reduce los rebotes de un entero. Conserva la consulta aproximadamente cada **60 segundos**, la fecha real de adquisición y la caducidad de **180 segundos**. Refrescar la interfaz o reenviar datos al ESP32 no avanza el filtro ni renueva la muestra. La lectura crítica predeterminada del 10 % no espera al suavizado.

El historial se reinicia ante fallo de lectura, cambio o ausencia de receptor, apagado observado, carga inferida o una interrupción prolongada. Los receptores distintos tampoco comparten estados pendientes de micrófono. La interfaz Windows, las alertas y el LCD reciben el mismo porcentaje publicado.

**El tramo 0–9 % sigue pendiente de calibración.** Los voltajes sin referencia suficiente conservan el valor leído y muestran porcentaje desconocido; no se inventa 0 % ni 100 % durante la carga inferida. Los umbrales configurables no cambian esa limitación. Se registra voltaje/referencia/estimación por cada respuesta válida para estudiar la zona baja durante el uso normal, sin consultas adicionales, rutas HID o identificadores del receptor.

**Si ya cargaste el firmware 1.3.7, no necesitas volver a flashear el ESP32.** El cambio de cálculo está solo en Windows; el protocolo y el código funcional del firmware se conservan. Se incluye un sketch compatible para placas anteriores. Se mantienen NVS, pines, PWM, potencia Wi-Fi y autenticación HMAC.

Mantiene la refrigeración AC previa al apagado, el selector lateral centrado, el umbral crítico configurable 0–100 % y los avisos LCD repetidos sin PC de 1.3.7. No cambia curvas, límites CPU/GPU ni autoridad de los ventiladores del portátil o de la base externa.

Validación requerida: compilación sin advertencias, curva completa y todos los enteros 10–100, huecos antiguos, intervalos irregulares, oscilaciones, límite crítico, llegada a 100, pérdida/reemplazo de fuente, carga inferida y ruta reporte HID → snapshot → protocolo con datos sintéticos; además de los 19 grupos generales y 5 de estabilidad. La procedencia publicada documenta los resultados efectivos. La exactitud física del porcentaje y el tramo 0–9 % siguen pendientes; no se afirma un nuevo PASS físico ni se instala o flashea automáticamente.

Es una **preliminar**: activa «Incluir versiones preliminares» para recibirla mediante el actualizador. La **1.3.6 permanece como última estable**. [Funcionamiento y límites](../docs/HYPERX-BATTERY-LCD.md).
