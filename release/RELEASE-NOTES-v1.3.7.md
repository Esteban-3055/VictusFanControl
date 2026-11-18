# VictusFanControl 1.3.7 · Preliminar · Refrigeración al apagar y avisos HyperX

Con corriente AC confirmada, el cierre normal de Windows añade una fase de **hasta 20 segundos de refrigeración**, solicitando nominalmente **5000 RPM en ambos ventiladores**. Después conserva el presupuesto separado de **hasta otros 20 segundos** para la liberación habitual. Con batería o fuente desconocida se omite la refrigeración adicional. Se utiliza la alimentación real consultada a Windows, no la pestaña seleccionada en el editor.

La fase conserva los controles de seguridad y el guardián WMI existentes. Se interrumpe ante desconexión del cargador, suspensión, fallo o cierre forzado. Si se cancela el apagado después de intentar la refrigeración, se retira el nivel temporal a Firmware; Automático requiere un nuevo inicio explícito. Las RPM son una solicitud nominal, no una garantía de velocidad mecánica exacta. Windows comparte la señal de apagado con el **reinicio normal**, por lo que también se aplica al reiniciar con AC. [Secuencia y límites](../docs/SHUTDOWN-AC-COOLING.md).

El selector azul de la barra lateral centra ahora icono y texto en todas las pestañas. **Batería crítica admite 0–100 %**, tanto en el editor como al guardar y enviar la configuración al ESP32. El umbral crítico es independiente de batería baja y tiene prioridad si se superponen. No cambia la estimación por voltaje ni se transforma una lectura desconocida en 0 %.

**Sin conexión al PC**, el LCD repite indefinidamente los avisos habilitados de batería baja/crítica según el último porcentaje conocido. Usa la duración y los parpadeos configurados como patrón repetido, marcando el valor con `*` porque es anterior. Al reconectar termina la repetición; con PC conectado los avisos conservan su duración limitada y no se repiten por cada refresco. No se inventa batería al arrancar sin una lectura previa.

**Para disponer del rango ampliado y de los avisos LCD sin PC hay que actualizar Windows y cargar el firmware ESP32 1.3.7.** El instalador no flashea la placa. Se incluye el sketch Arduino compatible con core ESP32 **3.3.2** y ArduinoJson **6.21.5**. Se conservan NVS/emparejamiento, GPIO3 PWM invertido 18 Hz/14 bits, GPIO0 NTC, GPIO6/7 I2C, Wi-Fi 8.5 dBm y HMAC TCP 8765.

Se refuerza la comprobación del plazo justo después de guardar el registro previo a una orden nativa, manteniendo disponibles las operaciones de liberación. También se corrige una carrera al cerrar el simulador TCP ya cancelado; los errores inesperados siguen siendo visibles. Se conservan los registros de recuperación y no se modifica el control de la base externa para refrigerar el portátil.

Validación requerida: compilación Windows, 19 grupos generales, 5 de estabilidad, pruebas aisladas de apagado/cancelación/órdenes tardías, extremos 0/100 guardados y transmitidos por el cliente real al parser C++, repetición LCD y rollover, Arduino y PlatformIO. La procedencia publicada detalla los resultados efectivos. **No hay nuevo PASS físico** de apagado real, RPM, pantalla ESP32 ni notificaciones Windows; las pruebas usan dispositivos simulados y no instalan ni flashean automáticamente.

Es una **preliminar**: activa «Incluir versiones preliminares» para recibirla mediante el actualizador. La **1.3.6 permanece como última estable**. [HyperX, LCD y parámetros](../docs/HYPERX-BATTERY-LCD.md).
