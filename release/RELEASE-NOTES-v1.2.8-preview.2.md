# VictusFanControl v1.2.8-preview.2

- Entrada explícita de prueba Manual: CPU hasta comando 54, GPU hasta 52; pares 51/51, 52/52, 53/52 y 54/52. Guarda ambos objetivos antes del envío y los expone en diagnóstico.
- Inicio de prueba en Firmware, sin activación automática ni reanudación en segundo plano. Apertura normal y perfiles existentes conservan 10..50. El rango nuevo espera la prueba física del target.
- Añadir punto a la derecha del seleccionado; creación deshabilitada sin selección, sin intervalo disponible o con 64 puntos.
- Dos superficies opacas pequeñas para los rotores, evitando repintar el lienzo 4K en cada cuadro. Conserva RPM independientes, inercia, ritmo editable y pausas.

Para probar, extrae el ZIP y ejecuta `Start-ProductGui.ps1 -Mode FanRangeTest` como administrador. [Secuencia y alcance](https://github.com/Esteban-3055/VictusFanControl/blob/v1.2.8-preview.2/docs/FAN_UPPER_RANGE_PREVIEW.md). La aceptación WMI y las comprobaciones de software no sustituyen la observación física; esta versión no es la última estable.

- Corrige el fallo de arranque de FanRangeTest: la política experimental solo se adjunta cuando Automático está autorizado. Prueba el mismo constructor y configuración usados por el arranque real, con preferencias de retención activadas/desactivadas y backend disponible/cerrado. No debilita la validación del controlador.
