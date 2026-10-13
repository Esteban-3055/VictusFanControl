# VictusFanControl v1.2.4 · estable

- **Animación de Inicio según Solicitud**: usa el nivel aceptado del controlador en vez del porcentaje de demanda. La referencia es Solicitud × 100 RPM simuladas; el giro visual completa una vuelta cada **0,5 segundos a solicitud 30**, cada 1,5 segundos a solicitud 10 y cada 0,3 segundos a solicitud 50, una vez estabilizada la inercia.
- **Giro reconocible y gradual**: conserva la aceleración y desaceleración suaves, añade una aspa marcada y mantiene las pausas al ocultar/minimizar, cambiar de página, interrumpir Automático o caducar la solicitud.
- **RPM visibles durante los retrasos**: conserva por separado la última lectura válida informada de CPU y GPU, la identifica discretamente como «última lectura» y la reemplaza al llegar una lectura nueva, incluido un cero informado. Antes de la primera lectura muestra «Esperando».

La actualización conserva perfiles, curvas, filtros térmicos, protecciones, retención opcional TZ01/DTT3 y recuperación automática. Las RPM simuladas determinan el giro visual; las cifras centrales siguen mostrando las RPM informadas. El control físico continúa sincronizado y los gráficos conservan sus huecos de adquisición.

Verificación: compilación, fixtures de solicitud independiente de demanda, inercia, pausa/reanudación, RPM ausentes/parciales, caducidad, orden de publicaciones, valores inválidos/futuros y recuperación con cero informado; interfaz y renders Windows, regresiones y prueba prolongada de recursos. El instalador y el ZIP se verifican contra el manifiesto y se publican con SHA-256 y procedencia.

Detalles: [Inicio, solicitud y últimas RPM](https://github.com/Esteban-3055/VictusFanControl/blob/v1.2.4/docs/HOME_DASHBOARD.md).
