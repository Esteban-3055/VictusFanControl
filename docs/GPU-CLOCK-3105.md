# Graphics clock máximo configurable: 3105 MHz

La preliminar 1.3.4 amplía el rango GPU de **210–2535** a **210–3105 MHz**, tanto en AC como en batería. El máximo se comparte entre el deslizador, la entrada numérica, perfiles JSON, configuración de rendimiento y presets enviados a NVML. Los mensajes de validación y la ayuda de la interfaz usan la misma constante.

El cambio amplía una preferencia; no selecciona 3105 automáticamente, no eleva valores guardados y no fija una frecuencia constante. Los predeterminados continúan en 1850 MHz para AC y 1200 MHz para batería. Los perfiles anteriores, incluido 2535 MHz, conservan su valor y formato. Un perfil nuevo guardado por encima de 2535 no es compatible con las versiones anteriores que rechazan ese rango; no se reduce silenciosamente al volver de versión.

La consulta de solo lectura del equipo HP 8C40 con RTX 4060 Laptop GPU informó `Max Clocks / Graphics: 3105 MHz` y ese valor en la lista compatible. No demuestra que se alcance bajo carga ni que una solicitud de locked clocks sea aceptada. Tampoco equivale a una lectura independiente del rango aplicado: `ActiveUnverified` conserva su significado actual.

NVML puede rechazar una solicitud. Se mantienen el resultado de error, liberación, exclusión de propietarios, journals y recuperación existentes. No se modifican protecciones térmicas, frescura, frecuencia/potencia reales, ventiladores, perfiles CPU ni firmware ESP32.

La cobertura de software comprueba 3105 en ambos presets, rechazo de 3106 y 209, persistencia independiente, aceptación de valores anteriores, valores predeterminados y edición exacta en ambos perfiles. Los fixtures no aplican frecuencias al equipo. La publicación conserva el canal preliminar y requiere las comprobaciones del commit exacto, hashes y verificación del actualizador.
