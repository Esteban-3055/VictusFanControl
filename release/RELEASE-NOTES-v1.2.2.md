# VictusFanControl v1.2.2 · estable

- **Giro por demanda en Inicio**: ambas animaciones siguen la demanda automática aceptada del controlador, de 0 a 100 %, conservando la rotación lenta y la inercia de v1.2.1.
- **Corrección de los arranques repetidos**: una lectura de RPM retrasada o temporalmente ausente ya no interrumpe el giro ni reinicia su aceleración. Se mantiene la última decisión aceptada mientras su propia muestra siga vigente.
- **RPM informadas sin cambios**: los números centrales conservan las lecturas de los ventiladores. Inicio indica que el giro visual representa la demanda.
- Cero demanda desacelera hasta reposo. Firmware, Manual, demanda caducada, interrupción o transición de fuente pausan la animación. Ocultar/minimizar o cambiar de página conserva la pausa y el arranque suave al regresar.

El máximo visual permanece en 24 grados por segundo, ahora a 100 % de demanda. La actualización conserva perfiles, curvas, filtros térmicos, protecciones, retención opcional TZ01/DTT3 y recuperación automática. El control físico sigue sincronizado; el cambio afecta únicamente a la presentación.

Verificación: fixtures de demanda, RPM retrasadas/ausentes, continuidad de inercia entre publicaciones, caducidad de decisiones aunque lleguen lecturas nuevas, arranque/desaceleración y pausa; interfaz y renders Windows, regresiones y prueba prolongada de recursos. El instalador y el ZIP se verifican contra el manifiesto y se publican con SHA-256 y procedencia.

Detalles: [Inicio y giro por demanda](../docs/HOME_DASHBOARD.md).
