# VictusFanControl 1.15.1 · Preliminar

Esta actualización corrige la coordinación de funciones independientes, la recuperación de sesiones y el guardado de preferencias. Incluye el selector de reglas integrado en la pantalla oscura de Funciones.

- CPU y GPU conservan sus valores y solicitudes independientes al activar, cancelar o recuperar ventiladores. Una función con fallo requiere su propio reintento; iniciar ventiladores solo reintenta las funciones vinculadas seleccionadas.
- Manual y Automático aplican los valores editados de las funciones vinculadas de forma coherente.
- Al reanudar, los límites esperan observaciones posteriores a la interrupción y una fase de telemetría válida. Cancelar el apagado libera el estado de mantenimiento sin volver a activar Automático por su cuenta.
- La recuperación de ventiladores conserva los supervisores independientes sanos. Una cancelación de cTGP demostrada antes de cualquier orden nativa no deja un falso fallo permanente; la evidencia de resultados nativos inciertos sigue bloqueando nuevas escrituras.
- Corrige el bloqueo al volver de suspensión que confundía al supervisor WMI compartido de la propia sesión con una sesión pendiente ajena. Solo se conserva el propietario exacto con la retirada de ventiladores acreditada; los registros ajenos o inciertos siguen bloqueando la recuperación.
- Reiniciar sesión conserva los borradores para editar, pero construye el nuevo control con los valores guardados y no ejecuta reglas de inicio de aplicación.
- El guardado de reglas informa por separado si se guardaron las preferencias y si una función pudo aplicarlas. La base externa conserva una referencia coherente con el archivo incluso si falla la conexión posterior.
- Los indicadores distinguen reglas sin aplicar de reglas sin guardar. cTGP muestra correctamente su estado en Manual. Los ajustes de pantalla de batería no repiten avisos ya emitidos.
- Corrige además la conservación de solicitudes durante cambios de alimentación, la recuperación automática y la normalización de archivos de perfiles dañados detectadas en la revisión anterior.

Validación: pruebas del coordinador real con dependencias simuladas, restauración, reglas, persistencia y publicaciones. Las pruebas no acceden a controles físicos ni prueban un apagado o suspensión reales. La validación física de la versión sigue pendiente. No requiere actualizar el ESP32 ni cambiar firmware, controladores o protecciones térmicas.
