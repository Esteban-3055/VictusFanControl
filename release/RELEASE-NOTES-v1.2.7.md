# VictusFanControl v1.2.7 · estable

- **Rotor PNG precargado**: integra la imagen suministrada, con el eje registrado al centro del disco y escala adaptada a la pantalla. Los números y textos permanecen en capas estáticas.
- **Avance de mayor precisión**: Stopwatch sustituye el reloj grueso y un productor de cuadros de alta resolución solicita hasta 120 cuadros/s, sin acumular devoluciones cuando la interfaz está ocupada.
- **Pausas y ajustes conservados**: se mantienen RPM medidas independientes, inercia, retención de cifras, `fan-animation.json` editable en vivo y pausa al cambiar de pestaña, ocultar o minimizar.
- **Mediciones de cadencia**: Exportar diagnóstico incorpora intervalos y tiempos reales de pintado de la interfaz, con muestras acotadas.

Conserva perfiles, curvas, preferencias y control físico. La publicación exige las suites Windows, recursos y verificación del paquete/instalador del commit final. Las mediciones de CI y la cadencia solicitada no garantizan FPS presentados en cualquier monitor ni constituyen validación física nueva.

[Detalles](https://github.com/Esteban-3055/VictusFanControl/blob/v1.2.7/docs/PNG_FAN_ANIMATION.md).
