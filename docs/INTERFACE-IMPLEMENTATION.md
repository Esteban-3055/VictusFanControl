# Interfaz de referencia: implementación y verificación

Estado de desarrollo del 22 de noviembre de 2026. Las funciones independientes se cerraron primero en una copia comprobada de la etapa 3. El tema nuevo se integró después, siguiendo las tres referencias y los cinco recursos gráficos entregados. La versión instalada, los perfiles del usuario y su inicio automático no se sustituyeron.

## Resultado

La superficie común de las 14 pestañas usa fondo negro, paneles grafito, esquinas biseladas, contornos finos y selección blanca iluminada. La cabecera, navegación y barra inferior comparten el marco. Funciones queda debajo de Rendimiento; el lateral conserva Actualizaciones en el tamaño mínimo.

Inicio conserva dos ventiladores, estado del sistema, curva aplicada e historial de demanda. Curvas conserva influencias/puntos a la izquierda y editor/simulador a la derecha. Configuración se reorganiza en Inicio de la aplicación, Interfaz y perfiles, Registros y estado, y un resumen de Base externa. Sus enlaces conducen a la conexión, curva y funciones; no duplican controles con significados diferentes.

Los gráficos habituales usan blanco y gris. En Monitorización se distinguen CPU con línea continua y GPU con línea discontinua, también en la leyenda. Amarillo y rojo conservan su función de aviso/fallo. Los estados principales de rendimiento se presentan en español; una solicitud GPU aceptada conserva expresamente la limitación de que el controlador no permite observar todo el rango aplicado.

Las acciones de edición, aplicación, guardado y recuperación mantienen sus contratos. La fecha del dato, la configuración confirmada y la autoridad real determinan las etiquetas. El tema no convierte un borrador, una lectura retenida o una selección en confirmación física.

## Recursos

`scripts/prepare-monochrome-assets.ps1` copia los PNG originales de forma exacta y prepara ICO de varias resoluciones. Conserva los canales RGBA y colores. El manifiesto de `assets/icons` registra sus hashes. El rotor original se incrusta sin el tinte de la interfaz anterior; su texto central se dibuja sin rotación.

El rotor se carga una vez y reutiliza mapas por tamaño. La animación conserva sus superficies pequeñas y opacas, pausa al ocultar la ventana y usa lecturas RPM vigentes. No abre sensores, WMI ni USB para dibujar. El atlas de bandeja muestra las representaciones ICO reales de 16, 20, 24 y 32 píxeles.

## Hallazgo durante la revisión visual

**F31:** Perfiles repetía las identidades de sus dos tarjetas en los selectores superiores AC/Batería. Curva Externa repetía cada identidad de punto en la tabla y en el gráfico. El foco y la accesibilidad buscaban por identidad, de modo que navegar a una representación podía resolver la otra. Se asignaron identidades distintas manteniendo la misma acción semántica. La prueba comprueba selección AC/Batería, selección de punto desde tabla/gráfico y ajuste por flechas desde ambos.

**F32:** los indicadores de Curva Externa solo entendían Demanda/Solicitud internas. Una curva térmica propia en Firmware podía aparecer sin entrada; una salida fija junto con Automático podía atribuirse falsamente a la curva. Los indicadores ahora interpretan la orden independiente confirmada, exigen su fecha original y capacidad para representar la salida aplicada, y rechazan combinar un modo recién aplicado con la respuesta del modo anterior. La salida fija no se representa como un punto de curva. Los selectores de entrada y rangos heredados se sustituyen por la fuente efectiva cuando no corresponden; la salida Manual se identifica como parte del seguimiento interno.

## Evidencia

`output/interface-stage4-20261122`, desde la raíz del proyecto, conserva la copia de binarios probada, identidad SHA-256, resultados JSON, logs y capturas. El conjunto de referencia produce 70 imágenes: las 14 páginas en 1672×941, 1040×660 y tamaños de píxel equivalentes a 125 %, 150 % y 200 %. Comprueba acciones dentro de superficie, identidades únicas, navegación completa y barra de estado persistente. Son ventanas Windows Forms reales con datos sintéticos y cero órdenes de hardware.

La regresión general añade borradores AC/Batería, teclado y accesibilidad, arrastre de curvas, estados de espera/fallo, prioridades de bandeja, ocultación, restauración y liberación. Las páginas de conexión y curva externa tienen pruebas propias con comunicación local. El ensayo de recursos abre/cierra 30 ventanas después del calentamiento y mide objetos GDI, USER y memoria privada. Los resultados y duraciones exactos pertenecen a `final-results.json`; no deben inferirse de este texto.

La copia final, posterior a F32, está en `output/interface-stage4-20261122/final/verified-app`. Pasaron sus trece grupos de `final-results.json` y los tres de `supplemental-results.json`: dieciséis grupos en total. La prueba de apagado mantuvo 20027 ms de enfriamiento, usó guardianes/IPC reales con controladores simulados y confirmó liberación; no apagó Windows ni emitió órdenes de hardware. El ensayo de recursos terminó con GDI 19→19 y USER 18→18, tras 924 renders y 30 ciclos medidos; la memoria privada final pasó de 34,8 a 41,8 MB decimales, dentro del límite del ensayo. No se deduce de ello ausencia universal de fugas.

Los tamaños de píxel no certifican cambios físicos entre monitores con DPI diferentes. Las mediciones de repintado son coste de dibujo aislado, no FPS físicos del monitor ni un porcentaje de CPU del usuario. Continúan pendientes las pruebas físicas de los controles nuevos en el portátil y el firmware del ESP32, reinicio/suspensión reales y una instalación de entrega. No se ha publicado una versión nueva.
