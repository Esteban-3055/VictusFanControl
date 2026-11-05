# VictusFanControl 1.3.17 preliminar

Corrige una interrupción de Automático/cTGP: el supervisor medía cada vuelta desde su inicio y podía confundir el tiempo de una consulta HP/WMI lenta con una pausa del sistema. Ahora mide por separado el trabajo y el intervalo sin supervisión. Una consulta que termina correctamente no dispara por sí sola `LIFECYCLE_GAP`.

La suspensión se comprueba también mediante los relojes de Windows que incluyen y excluyen el tiempo dormido. Se conserva el límite de tres segundos para pausas entre revisiones, la caducidad de cinco segundos del estado cTGP, el heartbeat, la fuente AC, las comprobaciones de propietario, los errores ACPI y la retención de sesiones con finalización nativa incierta. Una pérdida real sigue restaurando firmware; no se reactiva cTGP a ciegas tras un fallo.

El informe del supervisor distingue suspensión de demora entre revisiones e incluye tiempos de trabajo, espera y suspensión observada. Se registra el trabajo lento para facilitar el diagnóstico.

Pruebas sin hardware: una observación de 3,4 segundos en el supervisor real de prueba mantiene cTGP activo durante la siguiente vuelta y permite una liberación limpia; casos de suspensión durante trabajo/espera, pausa del planificador, relojes regresivos y muestreo interrumpido. Se conservan las regresiones de heartbeat, pérdida de AC, cambio externo de política y restauración incierta. La continuidad física durante una nueva anomalía WMI queda pendiente de observación en el equipo.

Conserva los ajustes y el firmware ESP32 existente. Preliminar optativa para el equipo calificado; no cambia límites de potencia ni calificación térmica.
