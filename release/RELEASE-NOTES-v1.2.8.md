# VictusFanControl v1.2.8

- Manual y Automático admiten CPU hasta nivel 54; GPU se limita a 52 en todos los comandos de producto. Los niveles 53 y 54 envían 53/52 y 54/52. Las rutas históricas de cualificación conservan su rango.
- El editor y simulador permiten extender el último punto de demanda a 54. Se conservan los puntos de los perfiles existentes: una curva que termina en 50 conserva ese comportamiento hasta que la edites.
- Añadir punto lo inserta inmediatamente a la derecha del seleccionado. Se deshabilita sin selección, sin intervalo disponible o con 64 puntos.
- Dos superficies pequeñas para los rotores mejoran la animación en 4K y conservan RPM independientes, inercia, ritmo editable y pausa al ocultar/minimizar o cambiar de pestaña.
- Corrige el arranque con Automático no autorizado o backend cerrado; las políticas experimentales solo se adjuntan cuando corresponde. Las pruebas usan el constructor real y cubren Manual/Automático 54/52 y liberación térmica.

La observación corta en el equipo mostró 5400/5200 RPM y descenso tras volver a Firmware. No establece límites mecánicos absolutos ni sustituye las pruebas prolongadas y de Automático bajo carga. [Uso y actualización](https://github.com/Esteban-3055/VictusFanControl/blob/v1.2.8/docs/FAN_LIMITS_54_52.md).
