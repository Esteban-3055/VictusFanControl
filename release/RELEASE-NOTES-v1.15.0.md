# VictusFanControl 1.15.0 · Preliminar

CPU, GPU, cTGP y base externa pueden funcionar por separado de Automático. La nueva pestaña **Funciones**, debajo de Rendimiento, reúne Encender/Apagar por componente, vínculos y arranque opcional. El preset **Comportamiento anterior** conserva la coordinación heredada; **Independientes** mantiene los atajos seleccionados y permite continuar después; **Sin vínculos** deja cada función a sus propios botones.

La interfaz adopta las referencias entregadas: negro/grafito, contornos blancos biselados, nuevos iconos y rotor original. Inicio, Curvas y Configuración conservan las acciones y unidades existentes. El fallo tiene prioridad en la bandeja; el icono de micrófono silenciado se usa solo con Automático activo.

## Control, recuperación y datos

- CPU/GPU comprueban sus sensores y propietarios por canal. Retirar uno no necesita RPM del otro actuador ni recrea la sesión del dominio sano. Las confirmaciones parciales y respuestas perdidas conservan la evidencia y bloquean reanudaciones que no pueden justificarse.
- cTGP y ventiladores comparten un guardián WMI con épocas/recibos separados. cTGP comprueba el cargador calificado en cada adquisición. Una operación nativa incierta sigue bloqueando el recurso compartido; la independencia no elimina esa limitación física.
- Consultas WMI ocupadas antes de enviarse pueden esperar sin fingir datos nuevos. Los plazos originales de adquisición, protecciones, identidad y cierre permanecen vigentes. Reinicio de sesión, suspensión y Salir conservan la liberación global; el reintento local de ventiladores conserva los controles independientes confirmados.
- Configuración solicitada, edición, guardado y estado confirmado se presentan por separado. La primera persistencia de vínculos respalda el perfil anterior. No se cambian las curvas del usuario a partir de las cifras de las imágenes de referencia.
- Se corrigen indicadores de protecciones, topología para N, foco de tarjetas/puntos, exportación después de retirar WMI y representación de la fuente externa. Diagnósticos acotados y registros conservan las pruebas necesarias sin reactivar controles.

## Base externa

La conexión/LCD se separa del actuador. Funciones ofrece seguimiento del nivel interno aceptado, salida fija 0–255 y curva propia por temperaturas CPU/GPU. **Fija 0 solicita parada; Apagar retira la autoridad y devuelve la base al control local de 130/255.**

Las modalidades independientes requieren firmware que anuncie `independentControlVersion=1`. Un dispositivo anterior conserva monitorización y seguimiento compatible. Actualizar Windows no graba el ESP32. El protocolo mantiene autenticación, fechas originales, permiso remoto de 2 s y retorno local. Consulte `INDEPENDENT-FUNCTIONS-USER-GUIDE.md` y el firmware fuente antes de preparar una actualización separada de la placa.

## Validación y límites de esta preliminar

La rama pasó pruebas de software de controladores aislados, guardianes/IPC, 16 selecciones de funciones, interfaz, recursos, instalador y cierre de sesión. El ensayo de enfriamiento utilizó 20 s reales y procesos de prueba, sin apagar Windows ni emitir órdenes al hardware. Las 70 capturas de las 14 pestañas usan datos sintéticos. El protocolo Windows se contrastó con el parser/controlador C++ de producción y compilaron las imágenes ESP32 de monitorización y banco, sin flashear.

Estas pruebas no certifican la respuesta física de la separación nueva, reinicio/suspensión reales, traslado entre monitores con DPI diferentes ni enfriamiento observado del portátil. Los límites se conservan también en `PRODUCT-RELEASE.json`. No se modifican firmware del portátil, protecciones térmicas del fabricante, EC ni límites arbitrarios de potencia cTGP; permanece la política HP calificada de hasta 75 W. La causa primaria del atasco WMI inicial no queda demostrada por corregir su recuperación.

El instalador EXE conserva el asistente común, ajustes, inicio y recuperación. Cerrar con la X oculta la ventana; **Salir** en la bandeja libera los controles. Una sesión nativa pendiente conserva sus registros y puede bloquear la instalación/adquisición hasta confirmar la restauración.
