# VictusFanControl v1.2.6 · estable

- **Ritmo de Inicio editable en vivo**: abre `%LOCALAPPDATA%/VictusFanControl/fan-animation.json`, cambia `segundosPorVueltaA3000Rpm` y guarda. No hace falta recompilar ni reiniciar.
- **Inercia conservada**: la animación se acerca gradualmente al nuevo ritmo. El valor inicial es 1 segundo por vuelta a 3000 RPM, con valores admitidos de 0.1 a 60 segundos.
- **Retención ante errores de edición**: un archivo incompleto, inválido, bloqueado o ausente conserva el último ajuste válido de la sesión. El archivo se crea en la primera apertura sin sobrescribir uno existente y queda separado de los perfiles y de cada instalación.
- **Pausas conservadas**: Inicio comprueba el archivo cada segundo mientras está visible. Otra página, ventana oculta o minimizada suspenden estas lecturas y el temporizador de animación.

Conserva RPM medidas por ventilador, aspas iguales, cifras sin etiquetas de retención, curvas, preferencias y control físico. Esta entrega cambia solo la presentación. La publicación exige las verificaciones Windows, regresiones, recursos y paquete/instalador del commit final, con SHA-256 y procedencia.

[Guía con valores de ejemplo](https://github.com/Esteban-3055/VictusFanControl/blob/v1.2.6/docs/FAN_ANIMATION_SETTINGS.md).
