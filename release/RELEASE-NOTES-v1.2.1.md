# VictusFanControl v1.2.1 · estable

- **Animación lenta y suave en Inicio**: las aspas giran diez veces más despacio, hasta 24 grados por segundo a 6000 RPM; una vuelta visual cada 15 segundos a ese máximo.
- **Inercia independiente por ventilador**: aceleración gradual y desaceleración más prolongada al cambiar las RPM. Cero RPM termina suavemente el giro residual y apaga el temporizador.
- **Movimiento uniforme** con intervalos irregulares entre cuadros, temporizador de 33 ms y repintado limitado al área de los ventiladores.
- Al ocultar/minimizar la ventana, cambiar de página o perder lecturas vigentes, la animación se pausa de inmediato. Al regresar arranca suavemente, sin recuperar los cuadros omitidos.

Las cifras de RPM siguen mostrando las lecturas actuales. La inercia es exclusivamente visual: se conservan el control físico sincronizado, perfiles, curvas, filtros térmicos, protecciones, retención opcional TZ01/DTT3 y recuperación automática. La actualización conserva tus preferencias.

Verificación: fixtures de arranque, cambios de RPM, desaceleración hasta reposo, intervalos irregulares, pausa/reanudación y retrasos acotados; interfaz y renders Windows, regresiones y prueba prolongada de recursos. El instalador y el ZIP se verifican contra el manifiesto de la compilación y se publican con SHA-256 y procedencia.

Detalles: [Inicio y animación](../docs/HOME_DASHBOARD.md).
