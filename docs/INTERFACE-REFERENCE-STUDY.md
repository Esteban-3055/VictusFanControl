# Referencias para la renovación visual posterior

El usuario entregó ocho PNG el 21 de noviembre de 2026. Solicitó terminar primero las correcciones y la separación funcional; la renovación completa de la interfaz corresponde a la etapa posterior. Este documento estudia esa propuesta, sin cambiar el tema de la aplicación instalada ni sustituir todavía los recursos de producción.

El 22 de noviembre, tras cerrar la copia funcional de la etapa 3 y ante la indicación de continuar todo lo planificado, se inició esta etapa visual en desarrollo. Los primeros recursos y composiciones están incorporados sin modificar la instalación. Sus capturas con datos sintéticos y verificaciones están en `output/interface-stage4-20261122`. El resto de este documento conserva los criterios de referencia y aceptación.

## Material conservado

Los originales permanecen en `C:/Users/TheMa/Desktop/VictusFanControll/`. Sus copias exactas, dimensiones y SHA-256 están en `output/interface-references-20261121/assets-manifest.json`, desde el directorio del proyecto. Los ocho archivos coinciden con su copia. Los cuatro iconos y el rotor son PNG de 1254 × 1254 con transparencia. Las tres capturas tienen aproximadamente 1670 × 942 píxeles.

| Archivo | Papel previsto |
|---|---|
| Referencia 1.png | Configuración: bloques apilados, cabecera de sección, acciones alineadas y agrupación clara. |
| Referencia 2.png | Inicio: dos ventiladores, estado del sistema, curva aplicada e historial de demanda. |
| Referencia 3.png | Curvas: influencias a la izquierda, gráfico a la derecha, AC/Batería y editor/simulador arriba. |
| Rotor de ventilador.png | Imagen giratoria de cada ventilador. El texto central de RPM se dibuja después, sin rotación. |
| icono programa victus.png | Identidad normal del programa y recursos de instalación. |
| icono automatico.png | Automático activo sin silencio del micrófono. |
| icono automatico y silenciado.png | Automático activo y micrófono silenciado. |
| icono fallo programa.png | Fallo de Automático, con prioridad sobre el silencio. |

Las cifras, curvas, nombre de equipo y versiones dibujadas en las referencias son ejemplos. Los rótulos de la nueva interfaz se obtendrán de la versión, identidad, preferencias y estados reales del programa.

## Lenguaje visual

Se busca acercarse a las tres referencias: fondo negro, paneles grafito, contornos blancos finos con esquinas recortadas, resplandor contenido en la selección, tipografía blanca y decoración triangular discreta. La cabecera, navegación lateral y barra inferior forman un marco persistente. La selección de página ocupa el ancho del lateral y usa una silueta biselada. Los gráficos comparten rejilla gris, trazos blancos y nodos claramente distinguibles.

Como punto de partida, el tema puede usar negro `#000000`, superficies `#0B0D0E` a `#16191B`, texto `#F4F5F6`, texto secundario `#B7BBBE`, rejilla `#3C4145` y contorno `#D9DFE1`. Son valores de diseño iniciales, no colores extraídos con precisión de las imágenes. La comparación visual posterior ajustará grosor, degradados, iluminación y espaciado. El verde y el rojo de los PNG mantienen sus funciones de Automático, silencio y fallo.

Los efectos de brillo no deben borrar letras, nodos o límites de los controles. El foco por teclado debe diferenciarse de la página seleccionada y del botón principal. Los estados deshabilitados, pendientes y fallidos deben llevar texto además de color.

## Correspondencia con las funciones

La nueva página **Funciones** se ubicará debajo de Rendimiento y seguirá el mismo estilo. Mostrará solicitud, confirmación, espera y fallo por CPU, GPU, cTGP y base externa. Los editores detallados seguirán en sus páginas correspondientes. El selector de coordinación permitirá conservar el comportamiento anterior o mantener cada función independiente de Automático.

Inicio tomará la composición de la referencia 2. Cada rotor responderá a la lectura válida de su ventilador; el valor RPM permanecerá derecho y legible. Sin lectura vigente se mostrará esa indisponibilidad. La animación será una representación del giro, con velocidad visual acotada para evitar aliasing; no se intentarán dibujar físicamente miles de vueltas por minuto ni se usarán fotogramas como medición.

Curvas tomará la composición de la referencia 3, conservando las unidades, límites y datos actuales. La curva guardada, el borrador, la curva aplicada y el punto observado necesitan identificadores visibles. El simulador seguirá separado del hardware. Cambiar entre AC y Batería continuará editando el perfil elegido, sin simular un cambio de fuente real.

Configuración tomará los grupos de la referencia 1, pero respetará la separación funcional nueva. La conexión de la base, su pantalla LCD y su actuador se describirán por separado. Los ajustes detallados de la base tendrán su página; una tarjeta de Configuración puede enlazar a ella sin duplicar interruptores de significado distinto.

La barra inferior expresará el estado real. «Telemetría validada» se mostrará cuando corresponda a la evidencia disponible; una lectura antigua no conservará ese rótulo. Los fallos independientes deberán indicar la función afectada. Se conserva la prioridad ya pedida para la bandeja: fallo de Automático por encima de silencio; el icono de micrófono silenciado solo con Automático activo.

## Integración en el programa

La superficie actual es un control Windows Forms dibujado por el programa, con geometría común para dibujo y acciones. Permite construir paneles biselados, contornos, texturas y gráficos próximos a las referencias. La primera implementación debería cambiar esa capa visual conservando los comandos de control existentes, sin introducir otro sistema de control de hardware.

Se prepararán recursos de imagen para cada tamaño y escala a partir de los originales conservados. Los iconos de bandeja necesitan tamaños pequeños revisados visualmente; la transparencia y los símbolos deben seguir siendo reconocibles a 16–32 píxeles. Los bitmaps del rotor y las texturas se cargarán una vez y se reutilizarán. No se volverá a decodificar el PNG grande en cada fotograma.

La animación permanecerá en la capa de presentación, suspendida al ocultarse/minimizarse cuando corresponda y limitada por el presupuesto de dibujo. No enviará consultas de sensores, batería, WMI ni órdenes de ventiladores. Se medirá el consumo de CPU, memoria y objetos gráficos antes y después en el mismo escenario, sin dar una cifra de ahorro por supuesta.

El lateral deberá admitir la página Funciones adicional sin recortar Actualizaciones ni empequeñecer excesivamente los textos. Se conservarán navegación por teclado, foco, etiquetas accesibles, ventanas de confirmación existentes, movimiento de ventana y ajuste entre pantallas con escalas diferentes.

## Orden y aceptación

1. Terminar y verificar la separación funcional y los fallos de recuperación.
2. Preparar tema, geometría biselada, tipografía y recursos, sin alterar los controladores.
3. Reproducir Inicio, Curvas y Configuración y compararlas visualmente con las tres referencias al tamaño equivalente.
4. Extender la misma composición a Funciones y las demás páginas, conservando cada acción y estado.
5. Verificar tamaño mínimo, tamaño habitual, maximizado y escalas 100 %, 125 %, 150 % y 200 %, con textos españoles largos y estados de error.
6. Comprobar bandeja, silencio/fallo simultáneos, teclado, arrastre de curvas, ventanas auxiliares, cambio de monitor y coste de animación.

La equivalencia visual se evaluará con capturas reales de la aplicación de desarrollo. Los ensayos de dibujo no certifican el funcionamiento físico del equipo. La publicación e instalación corresponderán a una versión funcional completa y verificada.
