# VictusFanControl v1.1.7 · estable

- La ventana se adapta al cambiar la resolución del escritorio, aunque Windows conserve el mismo porcentaje de escala. Al pasar de 4K al modo 1440p de un juego reduce su tamaño; al volver recupera el tamaño anterior.
- Los cambios de DPI, área de trabajo y monitor usan una referencia fija para evitar escalados acumulativos. Se conserva el tamaño elegido por el usuario y se contemplan ventanas ocultas, minimizadas y maximizadas.
- El diagnóstico incorpora resolución, DPI, área de trabajo, estado y dimensiones de la ventana, además de los cambios de presentación registrados.

Esta publicación es estable y está disponible en el canal predeterminado del actualizador. Conserva la descarga persistente con botón **Instalar**, las preferencias y las mejoras de Automático de v1.1.6, incluida la eliminación del corte por consumo GPU.

Verificación y alcance: `docs/PRODUCT_1_1_7_DISPLAY.md`. Las pruebas automatizadas de Windows simulan las notificaciones de pantalla; el cambio real de resolución durante un juego requiere observación en el equipo.
