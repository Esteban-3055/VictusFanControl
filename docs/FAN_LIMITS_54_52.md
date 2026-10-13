# Ventiladores 54/52 — VictusFanControl 1.2.8

El producto normal admite niveles lógicos 10–54 en el HP 8C40 / 9D0R1LA / BIOS F.18. CPU recibe el nivel seleccionado; GPU recibe `min(nivel,52)`. Manual y Automático usan la misma conversión en el backend. La GPU nunca recibe 53 ni 54.

| Nivel | CPU | GPU |
|---:|---:|---:|
| 51 | 51 | 51 |
| 52 | 52 | 52 |
| 53 | 53 | 52 |
| 54 | 54 | 52 |

Las consignas representan niveles nominales; no garantizan RPM exactas. La prueba Manual de preview.2 registró CPU hasta 5400 y GPU hasta 5200 RPM, aceptación WMI y descenso de RPM tras liberar. Fue una observación corta: aún quedan pruebas prolongadas y de Automático bajo carga. La resolución de las lecturas es 100 RPM. No se ha probado 54/54 ni establecido el límite mecánico absoluto.

## Actualizar

Vuelve a Firmware y sal desde la bandeja antes de actualizar. Puedes usar el instalador de 1.2.8 o extraer el ZIP en una carpeta nueva y ejecutar el instalador PowerShell incluido. Conserva tus preferencias, la configuración de ritmo de animación y los registros de recuperación. No mezcles DLL de versiones diferentes.

La apertura normal ya dispone del rango nuevo: no requiere FanRangeTest. El comportamiento de inicio guardado sigue vigente; si deseas empezar en Firmware, desactiva previamente la activación automática al iniciar.

## Curvas existentes

Los puntos guardados y los presets se conservan. Una curva que termina en 50 seguirá solicitando hasta 50. Para usar 54 en Automático, selecciona su último punto en Curvas, sube su nivel a 54 y guarda/aplica la modificación mediante los controles habituales. El extremo continúa en demanda 100 %, admite nivel final 50–54 y respeta el orden creciente. La GPU se limita a 52 incluso cuando la curva solicita 53 o 54. El simulador y los marcadores admiten el rango ampliado.

Añadir punto lo crea inmediatamente a la derecha del seleccionado, dentro de ese intervalo, conservando los demás puntos. Sin selección, en el último punto, sin espacio o con 64 puntos se deshabilita.

## Animación

El PNG gira según las RPM independientes de cada ventilador y conserva la inercia. Dos superficies pequeñas evitan repintar el lienzo completo cada cuadro. Al cambiar de pestaña, ocultar o minimizar se pausa y conserva su fase. El ritmo sigue siendo editable en `%LOCALAPPDATA%\VictusFanControl\fan-animation.json`. El diagnóstico identifica el renderizador y mide repintados de interfaz; no mide presentación/vsync del monitor.
