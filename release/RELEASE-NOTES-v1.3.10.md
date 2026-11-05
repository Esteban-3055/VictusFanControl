# VictusFanControl 1.3.10 preliminar

La refrigeración previa al apagado conserva sus veinte segundos originales aunque se desconecte el cargador después de comenzar. Solo empieza si Windows confirma AC al inicio. Cambiar de fuente no reinicia ni amplía el plazo; se mantienen las protecciones, cancelación del apagado, suspensión, autoridad válida y liberación supervisada final.

- Corrige el porcentaje HyperX que desaparecía con voltaje alto: el voltaje por sí solo no confirma carga. La estimación existente se satura al 100 % por encima del último punto de la tabla, dentro del rango válido, sin añadir una calibración nueva. El estado de carga se presenta como no confirmado.
- Evita el aviso «Se quitó el silencio» al recuperar un estado desconocido. Los cambios confirmados de silenciado a activo siguen notificándose.
- Pruebas aisladas de alimentación AC → batería/desconocida, plazo, órdenes tardías, fallos y liberación; ensayo de veinte segundos reales con dos procesos auxiliares e IPC, sin acceso a hardware ni apagado de Windows.

La desconexión física del cargador durante el apagado de esta versión aún no se ha probado. Los veinte segundos incluyen la aceleración de los ventiladores; 5000 RPM es la solicitud nominal, sin garantía de velocidad sostenida. Las correcciones de HyperX no acreditan una solución a los petardeos del enlace inalámbrico.

Preliminar optativa: activar «Incluir versiones preliminares». Se conserva 1.3.6 como última estable. No cambia el firmware ESP32; quien ya tenga 1.3.7 no necesita reflashear. No se incluyen registros privados ni credenciales.
