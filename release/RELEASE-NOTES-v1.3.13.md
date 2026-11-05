# VictusFanControl 1.3.13 preliminar

Permite activar y desactivar los límites CPU y GPU por separado, manteniendo el modo de ventiladores. Al activar Automático se confirman los límites seleccionados; se admiten CPU sola, GPU sola, ambos o ninguno. Después puedes cambiar la selección en Rendimiento y pulsar Aplicar sin reiniciar Automático.

- CPU y GPU se adquieren de forma independiente. Apagar CPU restaura su potencia sin tocar el control GPU; apagar GPU solicita su Reset sin reaplicar CPU. Un dominio apagado no se abre ni recibe escrituras por cambios AC/Batería.
- «Desactivar límites CPU / GPU» desactiva ambos límites conservando Automático. Las casillas son selección pendiente: pulsar Aplicar ejecuta el cambio y Guardar conserva las preferencias de próximos inicios.
- La vuelta de pantalla apagada o suspensión conserva la selección confirmada. Los errores mantienen sus registros y estados reales; no se inventa una liberación ni se cancela la limpieza del otro dominio.
- Incluye PL1 hasta PL2 sin tope fijo de 44 W, con 8 ≤ PL1 ≤ PL2 ≤ 114 W. Conserva los valores de potencia y reloj guardados al actualizar.

Validación de software con editor, IPC entre procesos, fuentes AC/Batería, selección independiente, recuperación y ciclo de pantalla. Las comprobaciones físicas de la nueva selección y de PL1 sostenido por encima de 44 W siguen pendientes. Se mantienen las protecciones térmicas, el destino exacto y la recuperación de solo liberación.

Preliminar optativa: activar «Incluir versiones preliminares». 1.3.6 permanece como última estable. El firmware ESP32 no cambia; quien tenga 1.3.7 no necesita reflashear. No se publican registros privados ni credenciales.
