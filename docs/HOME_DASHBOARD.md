# Inicio y curva ampliada de Ventiladores

La nueva interfaz se desarrolla después de v1.1.9 y no cambia su número de versión ni publica otra release por sí sola.

Inicio presenta dos ventiladores con 28 aspas, lectura CPU/GPU independiente, temperaturas, uso y estado de rendimiento. La rotación visual es proporcional a la velocidad informada; se detiene con lecturas no vigentes, velocidad cero, otra página, ventana oculta o minimizada. Un temporizador de presentación de 50 ms no consulta hardware. La animación no tiene autoridad sobre los ventiladores y conserva la semántica de las lecturas HP existentes.

La curva de Inicio usa la configuración aplicada y la fuente real, aunque se esté editando el otro perfil o existan cambios sin guardar. Fuera de Automático se identifica como curva del perfil. La demanda temporal procede exclusivamente de `AutomaticDecision.UnifiedDemand.Percent` y del instante de su adquisición. No se calcula con el borrador ni se fabrica en Firmware, durante una transición o con datos vencidos. Se conservan hasta cinco minutos y 600 adquisiciones; las interrupciones, cambios de fuente/activación y huecos de más de tres segundos separan los segmentos.

Ventiladores conserva Firmware, Manual y Automático y los controles manuales en el panel izquierdo. El gráfico derecho utiliza las mismas dimensiones de trazado que el editor Curvas: 634 × 350 en coordenadas de referencia. La navegación aparece como Inicio, Ventiladores, Rendimiento, Curvas, Perfiles, Monitorización, Protecciones, Configuración, Avanzado y Actualizaciones. Los identificadores internos de las páginas siguen estables para conservar la navegación y las sesiones existentes.

La verificación combina fixtures de historial/frescura, animación, navegación, separación del borrador y curva aplicada, pausa de ventana y renders Windows en tamaño mínimo y escalado. No se modifican curvas, filtros térmicos, protecciones ni rutas de recuperación.
