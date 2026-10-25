# VictusFanControl 1.3.4 · Preliminar · GPU hasta 3105 MHz

Amplía el máximo configurable de **Graphics clock** de 2535 a **3105 MHz** en los perfiles AC y batería. Incluye el deslizador, entrada numérica, perfiles guardados y configuración enviada a NVML.

Los valores existentes se conservan. Los predeterminados continúan en 1850/1200 MHz; actualizar no selecciona 3105 ni fuerza a la GPU a alcanzar esa frecuencia. El controlador puede rechazar el valor o ejecutar frecuencias inferiores según sus condiciones.

Incluye las correcciones de arranque, suspensión y recuperación de 1.3.3. Se conservan identidad del equipo, frescura, confirmación de liberación y journals. El firmware ESP32 y la curva externa no requieren cambios por esta ampliación.

Las pruebas son de software, sin aplicar 3105 MHz al equipo ni afirmar estabilidad física a esa frecuencia. Es una versión preliminar; requiere activar «Incluir versiones preliminares» en Actualizaciones. La versión estable permanece en 1.3.2.

[Alcance y compatibilidad de perfiles](../docs/GPU-CLOCK-3105.md).
