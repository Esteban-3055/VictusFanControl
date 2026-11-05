# VictusFanControl 1.3.11 preliminar

PL2 determina ahora el rango configurable de PL1, tanto en AC como en batería. PL1 admite desde 8 W hasta el menor valor entre PL2 y el máximo existente de 44 W. Por ejemplo, PL2 = 20 W permite PL1 entre 8 y 20 W; PL2 = 60 W permite PL1 entre 8 y 44 W.

- Al reducir PL2 por debajo del PL1 editado, PL1 baja al mismo valor. Aumentar PL2 no eleva PL1 automáticamente; editar PL1 tampoco aumenta PL2.
- La regla es coherente en deslizadores, botones, teclado, accesibilidad y entrada numérica. También se admite el caso PL2 = PL1 = 8 W.
- PL2 conserva su rango de 8–114 W. Se mantienen los límites de ejecución, las protecciones y los perfiles independientes AC/Batería. La edición sigue siendo un borrador hasta pulsar Aplicar; los valores guardados actuales se conservan al actualizar.
- Pruebas aisladas de edición, persistencia, interfaz y aplicación del par PL1/PL2 ajustado, sin modificar límites del hardware durante esos ensayos.

Incluye las correcciones de refrigeración al desconectar y de HyperX de 1.3.10. Esta versión no añade una prueba física de apagado desconectando el cargador ni acredita una solución a los petardeos del enlace inalámbrico.

Preliminar optativa: activar «Incluir versiones preliminares». Se conserva 1.3.6 como última estable. El firmware ESP32 no cambia; quien tenga 1.3.7 no necesita reflashear. No se publican registros privados ni credenciales.
