# VictusFanControl v1.1.6 · preliminar

- Eliminado el retorno a Firmware por consumo de GPU, tanto al iniciar Automático como durante su ejecución y en las revisiones explícitas. Los 81/83 W observados en el diagnóstico ya no cancelan el control. La potencia GPU sigue alimentando la curva de ventiladores.
- La opción de consumo en **Protecciones** controla únicamente **CPU >60 W**. Las preferencias existentes se conservan. Continúan las protecciones térmicas seleccionadas y las comprobaciones de sensores coherentes.
- Automático indica la causa concreta de espera o rechazo: sensor ausente, lectura inválida, antigüedad, núcleos incompletos, estado del controlador, equipo, ruta de control o sesión interrumpida. El diagnóstico registra la misma muestra y el mismo instante usados para decidir; no deduce la causa desde muestras cercanas.
- El inicio muestra el progreso de las tres adquisiciones nuevas necesarias. Las esperas se registran al cambiar la causa y cada 10 s mientras persisten, sin saturar los registros.

Incluye el actualizador de v1.1.5 con descarga persistente e instalación separada. Pruebas y alcance: `docs/PRODUCT_1_1_6_CHANGES.md`. Esta versión no acredita una nueva prueba física de temperatura, consumo ni ruido.
