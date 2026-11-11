# VictusFanControl 1.3.18 preliminar

Añade el icono proporcionado por el usuario para indicar que el micrófono HyperX está silenciado, exclusivamente cuando Automático está activo, con autoridad Custom y runtime Healthy. Al quitar el silencio se recupera el icono normal de Automático.

El icono de fallo conserva prioridad aunque el micrófono esté silenciado y mantiene su comportamiento anterior hasta una reactivación correcta. Manual, Firmware y preparación conservan sus iconos habituales. Un estado de silencio desconocido, un receptor desconectado o unos audífonos apagados no muestran silencio confirmado.

Utiliza el lector de eventos HyperX existente, sin nuevas consultas USB, sin cambiar el silencio y sin depender de tener habilitados los avisos de silencio. El porcentaje de batería y su frescura no se usan para decidir el icono. El PNG original se conserva sin alteraciones y el ICO incorpora tamaños de 16 a 256 píxeles.

La prueba de interfaz comprueba las transiciones de silencio, vuelta al icono normal, prioridad del error, preparación, Manual/Firmware, desconexión y reactivación. La reacción a un botón físico de silencio queda pendiente de observación en el equipo.

Conserva la corrección de cTGP de 1.3.17, los ajustes y el inicio automático. No requiere actualizar el firmware ESP32. Preliminar optativa para el equipo calificado.
