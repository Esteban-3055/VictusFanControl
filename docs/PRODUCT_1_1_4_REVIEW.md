# Revisión de v1.1.4

## Fallos e inconsistencias corregidos

1. El inicio cancelaba la intención de Automático a los 30 s, aunque faltasen únicamente sensores. Ahora la espera persiste; los registros pendientes, lifecycle y operaciones del usuario mantienen prioridad.
2. `FreshHpWmiFanProof` bloqueaba la siguiente adquisición CPU/GPU. El producto habitual usa la lectura periódica WMI sin bloquear el lector térmico. El supervisor del backend consume la misma adquisición con su antigüedad original y mantiene la vigilancia del guardián.
3. Una muestra nueva podía ocultar la antigüedad de campos conservados. Cada dominio crítico conserva su propia fecha; repetir un fallo no renueva los cinco segundos.
4. La ausencia de utilización se confundía con la ausencia de temperatura. Las curvas usan un valor conservador solo como entrada de política, manteniendo el dato observado como ausente.
5. Un fallo de auxiliares opcionales interrumpía toda la curva. La retención pierde su aporte y recalifica; nunca inventa muestras ni renueva un episodio por recuperar una fuente tibia.
6. El constructor omitía una base de escalado DPI y el proceso no solicitaba PerMonitorV2. El layout se suspende durante la construcción para escalar conjuntamente ventana y contenido.
7. Los paneles no seleccionaban el preset al cambiar de alimentación. Se actualizan al cambio real, conservando los dos borradores y permitiendo elegir voluntariamente otro mientras la fuente no cambie.
8. Un diálogo numérico podía aplicar al preset seleccionado después de un cambio de fuente. Se congela su destino al abrirlo.
9. El plazo de confirmación CPU dependía de volver de una adquisición. Un monitor independiente cierra la admisión al vencer; una llamada nativa pendiente sigue conservando el mutex y la evidencia hasta terminar.
10. Una lectura CPU parcial podía ocultar un paquete o núcleo nuevo más caliente al conservar el dominio completo. Ahora se completan únicamente los datos ausentes; las temperaturas nuevas y las identidades inválidas permanecen visibles para las protecciones.

## Verificación

- Regresiones puras: bordes estrictos de 5/10 s, fechas conservadas, falta de uso, reset de lifecycle y frescura estricta al iniciar.
- Control sintético: conservar autoridad sin órdenes durante falta temporal de temperaturas, recuperar dentro del límite y solicitar Firmware al caducar.
- Retención: pérdida y recuperación de auxiliares sin renovar el episodio; pruebas históricas estrictas sin modificación de alcance.
- GUI Windows: espera después de 30 s, cancelación, cambio de fuente en rendimiento y curvas, preservación de borradores, destino de edición numérica y prioridad/recuperación de colores.
- Distribución: mismo commit para compilación, comprobaciones, manifiestos, instalador y publicación preliminar.

La lectura térmica continúa separada de la espera de RPM. Las órdenes al firmware y la liberación conservan la serialización nativa; no se promete interrumpir una llamada WMI síncrona ya despachada. Las pruebas de software no sustituyen la observación del equipo real, el cambio entre pantallas ni una comparación térmica/acústica.
