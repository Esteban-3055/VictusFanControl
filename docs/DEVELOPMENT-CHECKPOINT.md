# Punto de continuidad: correcciones e independencia de funciones

Estado al 22 de noviembre de 2026. Rama local: `feature/independent-functions`. Base: `e6c284f202d8f475808603f8e77989feecb759b7` (1.14.1). Tras cerrar la etapa de software, el usuario eligió **1.15.0 preliminar**. Se está preparando la entrega con identidad nueva, contratos actualizados y comprobaciones de firmware del mismo commit; todavía no se sustituyó la instalación. Las copias congeladas descritas abajo preceden al cambio de versión y conservan sus hashes originales.

## Estado vigente: etapa 4

La implementación actual está descrita en [INDEPENDENT-FUNCTIONS-IMPLEMENTATION.md](INDEPENDENT-FUNCTIONS-IMPLEMENTATION.md). Ya están integrados CPU/GPU por canal, el propietario WMI compartido, cTGP independiente, las políticas persistidas con respaldo, Funciones y los mensajes/capacidades de control externo fijo y por temperatura. Pasan las 16 selecciones de funciones con el runtime y guardianes reales de prueba durante una cesión térmica simulada de ventiladores. La instalación sigue intacta: 33 archivos comparados, cero cambios.

Las verificaciones funcionales anteriores al tema están congeladas en `output/independent-functions-stage3-20261122`. La renovación de las 14 pestañas, nuevos iconos y rotor ya está integrada en desarrollo: [INTERFACE-IMPLEMENTATION.md](INTERFACE-IMPLEMENTATION.md). La guía de uso está en [INDEPENDENT-FUNCTIONS-USER-GUIDE.md](INDEPENDENT-FUNCTIONS-USER-GUIDE.md). F29/F30 y la interfaz final se contrastan con la copia de binarios de `output/interface-stage4-20261122`; consultar su `final-results.json` antes de describir qué verificaciones terminaron.

La siguiente entrega exige revisar con hardware sin confundir simulación con resultado físico y preparar el paquete/versionado. No instalar ni publicar automáticamente desde un checkpoint. La instalación permanece fuera de esta etapa de desarrollo. Los documentos de auditoría originales son históricos; el contrato vigente está en la implementación y la guía anteriores.

La revisión final posterior a F32 compila con cero advertencias/errores y pasa dieciséis grupos: trece en `output/interface-stage4-20261122/final/final-results.json` y tres suplementarios. La DLL App comprobada tiene SHA-256 `2e030d7136b5b10e2a41dd196998531a5e533cab3a7592dd8dbc437ff40c539b`. Su Core y guardián son idénticos a los de la copia congelada de la etapa 3. La galería de revisión está en `output/interface-stage4-20261122/review.html`; sus 70 capturas representan datos sintéticos. La instalación y ajustes vigilados mantienen 33 archivos sin diferencias.

Todo lo que sigue es historial de etapas 1 y 2: sus frases de trabajo pendiente, hashes y tiempos pertenecen a esas etapas y se sustituyen por el estado vigente anterior.

## Actualización de continuidad del 22 de noviembre

La conservación del supervisor CPU/GPU durante el reintento de ventiladores ya está integrada en el camino real de `ProductForm` y `ProductRuntime`. `ProductPerformanceLifetime` continúa sus consultas de estado aunque se haya retirado el runtime de ventiladores. El protocolo devuelve los identificadores de sesión CPU/GPU obtenidos de los controladores y registros reales. La transferencia comprueba PID, inicio del proceso, nonce, configuración, fuente AC/Batería y sesiones exactas. No crea un supervisor nuevo ni repite la activación de límites.

Una retirada local no es una liberación global. Si falla la preparación, se cancela por suspensión o el usuario sale, la ventana eleva el cierre y libera el supervisor conservado. Las referencias al runtime anterior no pueden cerrar un propietario ya transferido. Una lectura WMI tardía mantiene la barrera hasta su confirmación exacta. El lector HyperX sigue independiente y la conexión externa no se suspende por el reintento local; el actuador de la base todavía depende de órdenes internas aceptadas.

Pasaron doce grupos de regresión: vida independiente, actualización parcial, ventana de reintento, IPC de rendimiento, interfaz completa, cTGP, apagado simulado, dominios seleccionables, dominios CPU/GPU y recuperación CPU/GPU/ventiladores. La interfaz completa terminó en 59,17 s. Después de endurecer la comprobación de fuente, se repitieron la vida independiente y ambos dominios; pasan en la compilación final. La DLL App final tiene SHA-256 `f74dc1540831bf73b0d05ea7476b2d7b75ed2bedb8685ef5820db0930ff3efd4`.

La compilación final tiene cero errores y advertencias. `output/independent-functions-stage2-20261121` conserva scripts, resultados, logs y comprobación de instalación: 33 archivos comparados, cero cambios. El apagado probado fue simulado con mensajes reales de Windows y procesos de prueba: fase de enfriamiento 20,033 s, sin apagado del sistema ni comandos de hardware. No se instaló ni publicó la rama.

El siguiente trabajo es separar la admisión de CPU/GPU de los fallos y RPM de ventiladores, manteniendo barreras explícitas de suspensión/cierre y confirmación por dominio. La conservación de su vida no significa todavía independencia completa de sus botones. Después sigue el supervisor WMI compartido de ventiladores/cTGP, las políticas persistidas y Funciones, y el protocolo del actuador externo. Las secciones siguientes conservan el historial de la primera etapa; sus hashes y tiempos pertenecen a aquella compilación.

## Trabajo verificado

La auditoría funcional y técnica está en `INDEPENDENT-FUNCTIONS-AUDIT.md`. Enumera F01–F15 y distingue el comportamiento heredado de defectos accidentales. Las correcciones de desarrollo abarcan recuperación WMI/cTGP, contención de una consulta no enviada, evidencia final de liberación, conservación de selecciones confirmadas, presentación de protecciones, diagnóstico tras retirar el lease, rotación del registro nativo, topología para N y actualización parcial CPU/GPU.

F14 quedó reproducido con el `ProductRuntime` real y un proceso guardián IPC simulado, antes de corregirlo. La prueba original fallida está conservada en `runtime-partial-before.log`. La prueba corregida recorre éxito, rechazo, confirmación parcial y respuesta perdida. Una confirmación parcial conserva los valores realmente comprometidos. Una respuesta perdida cancela la reanudación desatendida; no se toma el estado anterior como prueba de rollback.

La inyección explícita de dependencias permite probar ese coordinador con identidad, alimentación, telemetría y backend de ventiladores aislados. El inicio de producción sigue construyendo los adaptadores calificados existentes. No se abrió adquisición real de hardware para estas pruebas.

La coordinación pura recorre 23040 combinaciones, además de las 16 selecciones heredadas. Distingue solicitud, política de activación y requisitos; nunca declara actividad física. Mantiene el encendido de funciones seleccionadas al entrar en Automático, permite conservarlas después y no repite la selección al reanudar ni en cada repintado. **Todavía no está conectada a la interfaz o a los controladores de producción.**

La última compilación completa terminó con cero errores y advertencias. Se ejecutaron 33 grupos existentes y verificaciones específicas adicionales a lo largo de esta etapa; no todos corresponden a la misma compilación. En la compilación final de este punto se repitieron Avanzado, coordinador real de rendimiento, conservación de intención y regresión completa de interfaz. Esos cuatro grupos pasaron; la regresión completa terminó en 79,66 s. Su DLL tiene SHA-256 `8139d692091388605d94aa657b2f3dd61afb498e9b60357919707a04c34299db`.

Los registros y sus resultados JSON están bajo `output/independent-functions-stage1-20261121`, desde el directorio del proyecto. La comparación `installed-after-runtime-stage.json` verificó 33 archivos de instalación y ajustes con cero diferencias respecto a `installed-before.json`. La calibración HyperX observada de 4118 mV se leyó sin modificarla.

Las pruebas de esta etapa son de software, con procesos y comunicación local. No certifican reinicio real de Windows, voltajes, RPM físicas, instalación nueva o firmware grabado. No se hizo flasheo, cambio térmico, parada del programa instalado ni publicación en GitHub.

## Trabajo funcional que falta

1. Completar admisión y operaciones independientes CPU/GPU sobre la vida ya separada. `PerformanceAdmissionPermitted`, `CanUpdatePerformance`, `HasCurrentTelemetry` y la disponibilidad de los botones aún incluyen dependencias de ventiladores. Las preferencias solicitadas y la configuración confirmada por cada dominio deben tratarse por separado, también ante fallos parciales.
2. Separar la intención y liberación de ventiladores/cTGP dentro de un supervisor WMI compartido. La entrada exclusiva de cTGP debe emitir cero setters de ventiladores. La liberación de ventiladores no debe terminar un guardián que aún posee cTGP. Todos deben compartir el marcador de resultado nativo incierto, además del mutex.
3. Integrar la política pura, preferencias y migración respaldada. Conservar como opción el comportamiento actual; una preferencia guardada nunca certifica un propietario activo. Separar solicitud, estado observado y borrador.
4. Crear Funciones debajo de Rendimiento y conectar acciones por componente con sus estados de espera, confirmación y recuperación. Probar pérdida de Automático, cambio AC/Batería, suspensión, cierre, cancelación y fallos simultáneos usando el coordinador real.
5. Separar conexión/LCD del actuador externo. Incorporar salida fija y curva propia con capacidades explícitas de PC y ESP32. El firmware v1 exige una orden interna aceptada: no se deben falsificar esas pruebas para conseguir independencia. Conservar curvas y unidades existentes. La programación de la placa del usuario no está autorizada por este trabajo de desarrollo.
6. Completar pruebas del protocolo y firmware, matriz de funciones y validación de presentación. Preparar publicación e instalación cuando la versión esté completa, tenga número de entrega y esté verificada, respetando la instrucción de trabajar primero en desarrollo.

Los plazos actuales y su significado están en `SENSOR-TIMING-AUDIT.md`. Una tolerancia mayor no debe renovar fechas de adquisición ni permitir órdenes superpuestas cuando se desconoce el resultado nativo.

## Etapa visual posterior

El estudio de las ocho imágenes entregadas está en `INTERFACE-REFERENCE-STUDY.md`. Las copias exactas y manifiesto están en `output/interface-references-20261121`. Se conserva el orden solicitado: terminar primero los cambios funcionales y después reproducir la interfaz negra/grafito, contornos blancos biselados, navegación lateral y nuevo rotor/iconos.

Las capturas son referencias de estilo. Sus valores, perfiles y versiones ilustrados no se importan como configuración. La prioridad de bandeja sigue siendo fallo de Automático por encima del silencio; el icono silenciado corresponde solo a Automático activo.
