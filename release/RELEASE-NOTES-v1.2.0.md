# VictusFanControl v1.2.0 · estable

- **Nuevo Inicio** con dos ventiladores de aspas animadas, lectura CPU/GPU independiente, temperaturas y uso, estado de rendimiento y curva aplicada.
- **Demanda en tiempo real**: historial de cinco minutos calculado con las observaciones del controlador. Conserva huecos ante interrupciones, cambios de fuente/activación o pérdida de datos; no utiliza el borrador de curvas.
- **Curva ampliada en Ventiladores**, con el mismo tamaño de trazado que el editor Curvas. Los controles Firmware, Manual y Automático quedan en el panel izquierdo.
- **Nuevo orden de pestañas**: Inicio, Ventiladores, Rendimiento, Curvas, Perfiles, Monitorización, Protecciones, Configuración, Avanzado y Actualizaciones.
- La animación usa lecturas vigentes y se pausa al ocultar/minimizar la ventana, cambiar de pestaña, perder frescura o informar velocidad cero. Repinta sólo el área de los ventiladores y no añade consultas de hardware.

Conserva perfiles, curvas, preferencias, filtros térmicos, protecciones, retención opcional TZ01/DTT3, recuperación automática y actualizaciones de v1.1.9. La curva de Inicio sigue el perfil aplicado y la alimentación real aunque se edite otro perfil o haya cambios sin guardar.

Verificación: pruebas Windows de interfaz, navegación, animación independiente, datos vigentes, separación del borrador, historial y renders mínimo/4K; regresiones de recuperación y prueba prolongada de 924 renders/30 aperturas con recursos GDI/USER acotados. El instalador y el ZIP se verifican contra el manifiesto de la compilación y se publican con SHA-256 y procedencia. La actualización de interfaz no constituye una nueva cualificación física del control térmico.

Detalles: [Inicio y curva ampliada](../docs/HOME_DASHBOARD.md).
