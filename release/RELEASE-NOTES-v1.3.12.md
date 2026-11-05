# VictusFanControl 1.3.12 preliminar

Elimina el tope fijo de 44 W de PL1. Ahora PL1 admite desde 8 W hasta el valor configurado de PL2, tanto en AC como en batería. PL2 conserva su máximo de 114 W: PL2 = 80 W permite PL1 hasta 80 W; PL2 = 114 W permite PL1 hasta 114 W.

- Actualiza el editor, la validación de perfiles, el envío de ajustes al Guardian y la aplicación RAPL del producto. Aumentar PL1 no aumenta PL2; reducir PL2 baja PL1 si hace falta.
- La aplicación ampliada queda limitada al destino exacto HP 8C40/F.18 con i7-13700H y la referencia desbloqueada 45/115 W. Conserva la validación de los límites informados por el hardware, los bits ajenos a potencia, las protecciones térmicas, la propiedad de sesión y la restauración supervisada.
- Las herramientas históricas de diagnóstico/calificación conservan su política de reducción. La recuperación tras una caída sigue siendo solo de liberación; no reaplica ni eleva límites.
- Pruebas sin hardware de codificación 45/60/80/114 W, aplicación, cambio de preset, registro duradero, restauración, recuperación y editor AC/Batería. No se ha realizado un ensayo físico por encima de 44 W para esta versión: el valor solicitado no garantiza consumo sostenido ni aceptación del firmware.

Los valores guardados actuales se conservan. Incluye las correcciones anteriores de refrigeración al desconectar y HyperX. No cambia el firmware ESP32 ni requiere reflashear quien ya tenga 1.3.7.

Preliminar optativa: activar «Incluir versiones preliminares». Se conserva 1.3.6 como última estable. No se publican registros privados ni credenciales.
