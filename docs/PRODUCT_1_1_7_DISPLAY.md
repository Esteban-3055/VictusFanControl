# v1.1.7: cambios de resolución y DPI

La ventana anterior solo se limitaba al área de trabajo al abrir y al recibir un cambio de DPI. Un juego puede cambiar 3840×2160 a 2560×1440 conservando el 200 % de escala, por lo que no llegaba la notificación de DPI y la ventana permanecía demasiado grande.

El formulario atiende ahora WM_DISPLAYCHANGE, WM_SETTINGCHANGE y WM_DPICHANGED. Recalcula en la cola de interfaz, después de los ajustes nativos de Windows, con los rectángulos actuales del monitor y el DPI del formulario. El tamaño normal elegido se normaliza y se conserva como referencia; los ajustes automáticos nunca reemplazan esa referencia. La escala es el menor valor entre DPI/96, ancho/1920 y alto/1080. El área de trabajo limita tamaño y posición sin perder el mínimo original.

La ventana oculta sigue oculta, la minimizada sigue minimizada y la maximizada ocupa el área de trabajo actual sin activarse. Al restaurar se aplica el tamaño normal correspondiente. Estas notificaciones solo cambian presentación: no adquieren control de ventiladores, no aplican rendimiento ni solicitan una interrupción de Automático.

El diagnóstico manual añade `window` a `gui-state.json`: DPI, dispositivo, resolución, área de trabajo, posición, dimensiones, mínimo y referencia normal. El registro estructurado `PRODUCT DISPLAY LAYOUT` incluye el motivo y los límites antes y después de cada cambio efectivo.

## Verificación

- Compilación directa de núcleo y GUI con Roslyn; GUI y prueba geométrica con advertencias tratadas como errores.
- Pruebas geométricas de 4K/200 % → 1440p/200 % → 1080p/100 %, cambio exclusivo de DPI, 100 ciclos completos, tamaño personalizado, área reducida, barra de tareas y coordenadas negativas de otro monitor.
- El arnés Windows envía notificaciones nativas al formulario con geometría aislada, comprueba el resultado y genera capturas antes, durante y después del cambio. Comprueba ventana oculta, minimizada, maximizada, área de trabajo, exportación del diagnóstico y ausencia de comandos o interrupciones del runtime.
- La publicación exige las comprobaciones del mismo commit, el paquete completo verificado y el instalador con ese mismo payload. v1.1.7 se publica como estable; no sobrescribe versiones anteriores.

La simulación Windows no demuestra el comportamiento de un juego real, un cambio físico de monitor ni refrigeración o ruido. Esas observaciones mantienen su estado pendiente en `PRODUCT-RELEASE.json`.

## Diagnóstico recibido el 11 de octubre de 2026

Archivo: `VictusFanControl-diagnostico-20261011T034055912Z-a69b1703af854201bafab96bf740d858.zip`; SHA-256 `b368c72f5972738e1059cdd9785fd07eaae8ea7dd84536d2ff64ac77257f188c`.

La captura final de v1.1.6 muestra runtime Healthy, Automático activo en AC y sin interrupción o fallo declarado. Los 857 registros de telemetría conservados abarcan aproximadamente 15 minutos y 27 segundos: no faltan temperaturas, potencia ni RPM, y no usan retención de sensores críticos. El mayor intervalo entre adquisiciones es 1,531 s. Los eventos conservados cubren aproximadamente siete minutos y no contienen errores o interrupciones.

Estos fragmentos no acreditan toda la sesión de una hora y 55 minutos. La CPU alcanza 100 °C en la telemetría conservada; la protección térmica CPU estaba desactivada en las preferencias del usuario, que esta versión conserva. La GPU no supera 75 W en este fragmento y, por tanto, no constituye una nueva prueba física de la eliminación de aquel corte. Un registro de sesión bloqueado por un proceso activo tampoco demuestra una sesión huérfana.

El formato anterior carece de geometría de pantalla, por lo que no permite reconstruir el cambio concreto de resolución. Esta versión incorpora esos datos para las siguientes observaciones.
