# VictusFanControl v1.1.3 · Cierre WMI e iconos

Versión preliminar que corrige el cierre de las consultas de ventiladores y actualiza los cuatro iconos proporcionados el 10 de octubre.

- Al salir, reiniciar la sesión o instalar una actualización, se cierra también la admisión de lecturas en la carpeta de telemetría que se crea después de volver a Firmware. Se espera a que termine la llamada nativa antes de dar el cierre por completado.
- El guardián espera el turno WMI antes de comprobar la marca de llamada en curso. Una lectura que termina normalmente durante el cierre ya no se confunde con una llamada incierta. Una marca retenida, un timeout real o un mutex abandonado siguen conservando el bloqueo y la evidencia.
- Las consultas propias se identifican por PID y hora de inicio del proceso; las marcas anteriores usan su UTC. La detección no depende de la fecha de creación del archivo, que Windows puede reutilizar.
- Mensajes de recuperación en UTF-8 entre el instalador, la aplicación y el recuperador.
- Nuevos iconos del programa, bandeja normal, Automático y error, con los PNG originales y los ICO en nueve tamaños.

Si una versión anterior dejó una llamada incierta en el arranque actual, cierra Victus desde **Bandeja → Salir**, usa **Reiniciar Windows** y ejecuta el instalador. La actualización no borra una llamada incierta del arranque actual ni transforma una consulta pendiente en una confirmación de liberación.

Conserva perfiles, Protecciones y el canal preliminar del actualizador. No modifica la curva ni los límites de CPU/GPU. Dirigido al HP Victus 8C40 / 9D0R1LA / BIOS F.18. Pruebas de software sin E/S de hardware; pendiente de confirmar el nuevo cierre en el portátil.
