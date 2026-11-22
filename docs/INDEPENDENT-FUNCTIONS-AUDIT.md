# Estudio funcional y técnico para separar las funciones

Revisión iniciada el 21 de noviembre de 2026 sobre la base 1.14.1, commit `e6c284f202d8f475808603f8e77989feecb759b7`. Los cambios pertenecen a `feature/independent-functions`. La instalación del usuario quedó fuera de esta auditoría de desarrollo. Tras completarla, el usuario eligió **1.15.0 preliminar** para preparar la entrega; el checkpoint distingue las pruebas anteriores y el paquete de versión.

La situación actual, integración y nuevos hallazgos F17–F30 se describen en [INDEPENDENT-FUNCTIONS-IMPLEMENTATION.md](INDEPENDENT-FUNCTIONS-IMPLEMENTATION.md); F31/F32 de la interfaz están en [INTERFACE-IMPLEMENTATION.md](INTERFACE-IMPLEMENTATION.md). Este documento conserva la auditoría y propuesta inicial de las etapas 1 y 2; sus referencias a integración pendiente describen aquel punto, no la implementación de las etapas 3 y 4.

El objetivo es que cada función tenga una finalidad comprensible, una orden explícita, un estado verificable y una recuperación propia. La separación debe conservar como opción el comportamiento actual. La revisión contempla la interfaz, las decisiones de control y el funcionamiento del programa completo.

## Inventario funcional

| Función | Sentido para el usuario | Contrato de control y evaluación |
|---|---|---|
| Firmware, Manual y Automático | Elegir quién controla los ventiladores internos | Son modos excluyentes del mismo actuador. Se conserva una sola autoridad. Firmware no significa liberar todos los controles del programa. |
| Nivel manual | Fijar el nivel de ambos ventiladores | Orden nominal 10–54, con admisión y supervisión. El nivel no certifica RPM exactas ni propiedad física del firmware. |
| Curva e influencias | Ajustar la demanda de refrigeración | Las seis influencias son ganancias independientes; manda la contribución mayor, no su promedio. La emergencia usa temperaturas reales aunque la demanda use una media de núcleos. |
| Avanzado e inercia | Evitar cambios de ruido innecesarios | Es coherente separar filtros, confirmación de subida, bajada e historial. La pantalla identifica los ajustes comunes para AC y batería. El rango de N debe corresponder a los P-Cores disponibles. |
| Límite CPU | Seleccionar PL1 y PL2 | Tiene controlador y recuperación propios. PL1 debe seguir limitado por PL2, sin recuperar el antiguo techo de 44 W. Su admisión todavía depende parcialmente del estado global de ventiladores. |
| Límite de frecuencia GPU | Limitar la frecuencia solicitada | La aceptación de Set no certifica el rango completo aplicado por el driver. La interfaz debe expresar esa confirmación parcial con palabras comprensibles. |
| Política HP cTGP | Activar la política HP calificada | Es una política fija, restringida al equipo y cargador calificados. Actualmente comparte sesión con los ventiladores y depende de Automático. Separar su activación no permite introducir potencias o órdenes arbitrarias. |
| Perfiles AC y batería | Guardar preferencias distintas por alimentación | Cambiar la pestaña edita un perfil; no cambia la alimentación real. Se conserva esta distinción. El cambio real de fuente necesita confirmación del controlador. |
| Guardar, Aplicar e Importar | Conservar o ejecutar cambios de forma predecible | Guardar e importar no aplican hardware. Aplicar debe indicar la función afectada y mostrar sus valores confirmados. La separación nueva no debe aplicar borradores de otras funciones. |
| Protecciones de Automático | Elegir cuándo volver a Firmware y reanudar | Son preferencias de cesión del control de software. No modifican las protecciones del firmware. Deben mostrarse por separado la edición y la política aplicada. |
| Base externa y conexión | Consultar el ESP32 y controlar sus ventiladores | La conexión es independiente, pero la orden remota actual depende de control interno aceptado. Desconectar el PC lleva al valor local 130/255; no apaga físicamente la base. |
| Curva externa | Ajustar la refrigeración externa | Debe conservarse la curva y su fuente de entrada. Una curva basada en nivel interno no puede convertirse silenciosamente en una curva basada en demanda. |
| LCD y monitorización | Ver temperaturas, demanda, RAM y batería | Deben seguir disponibles sin Automático. La validez de una temperatura y la de la orden de ventiladores son conceptos distintos. |
| HyperX | Ver una estimación de batería y el silencio | Es un lector opcional independiente. La referencia guardada observada es 4118 mV. El protocolo actual no confirma la conexión del cargador. Se conserva la lectura aproximada y el último porcentaje solicitado por el usuario. |
| Icono y bandeja | Entender el estado y abrir o salir del programa | Fallo de Automático tiene prioridad sobre silencio. El icono de silencio corresponde solo a Automático activo. La bandeja debe continuar disponible durante esperas del controlador. |
| Inicio, suspensión y reanudación | Recuperar preferencias sin operaciones inesperadas | La intención guardada no es prueba de propiedad del hardware. Cada función deberá validar sus requisitos y adquirir su sesión antes de indicar actividad. |
| Enfriamiento al apagar | Mantener la fase temporal de refrigeración | La fase admitida tiene presupuesto original de 20 s; una desconexión posterior del cargador no lo reinicia ni acorta. Sigue sometida a la admisión normal. La simulación de mensajes de Windows no certifica RPM físicas. |
| Diagnóstico, actualización e instalador | Explicar fallos y mantener el programa | Exportación limitada, validación de paquetes, preservación de preferencias y liberación previa son contratos válidos. El diagnóstico debe incluir también cTGP y la política realmente aplicada. |

## Fallos y discrepancias identificados

Los estados de esta lista distinguen una corrección en desarrollo de una función terminada en el equipo.

| ID | Evidencia y consecuencia | Estado |
|---|---|---|
| F01 | El guardián conservaba el error de supervisión en `Failure` y devolvía error incluso después de restaurar cTGP y retirar el lease. El cliente rechazaba esa liberación y no reabría lecturas. | Reproducido antes del cambio y corregido en desarrollo. Se conservan error inicial y resultado de recuperación por separado. |
| F02 | El agotamiento de 2 s esperando el mutex WMI se trataba como un fallo permanente de política, aunque la consulta no hubiera sido enviada. | Corregido en desarrollo mediante clasificación explícita y reintento de observaciones no enviadas. La fecha de confirmación no se renueva. |
| F03 | Había una segunda clasificación de cualquier `Failure` como permanente en la ruta de interrupción de Automático. | Unificada en desarrollo con la clasificación usada al confirmar cTGP y al consumir la liberación. |
| F04 | Una restauración rápida podía sustituir el estado de fallo de cTGP antes de la siguiente lectura de la interfaz. | Corregido en desarrollo. El informe final y el recibo de liberación preservan la causa, ligados al proceso y sesión exactos. |
| F05 | Algunas pruebas de simulación empleaban el mutex nativo de producción. Podían competir con el programa instalado pese a no efectuar llamadas de hardware. | Corregido antes de ejecutar esas pruebas. Se utiliza un espacio de nombres de mutex exclusivo de cada simulación. |
| F06 | Un cambio confirmado de límites y una interrupción podían llegar antes del siguiente repintado. Al conservar solo el último estado, se perdían las selecciones confirmadas y la reanudación podía recuperar límites ya desactivados. | Reproducido con la interfaz y buzón reales de prueba; corregido en desarrollo. Incluye CPU/GPU desactivadas, valores confirmados, borrador distinto y cancelación del usuario. |
| F07 | Protecciones mostraba `Profiles.Protections`, el borrador, sin compararlo con `AppliedProtections`. | Corregido en desarrollo. La pantalla distingue edición, sesión en uso y sesión previa. La desactivación inmediata de la reanudación se explica aparte. Render mínimo inspeccionado. |
| F08 | Reglas de seguridad contenía frases fijas sobre cesión térmica que no representaban las preferencias elegidas. | Corregido en desarrollo mediante la política aplicada, o el borrador identificado cuando todavía no hay una sesión. No cambia los umbrales de control. |
| F09 | El diagnóstico general omitía cTGP y las protecciones aplicadas; al retirarse el lease también podía perder el acceso al informe de liberación. | Corregido en desarrollo. Exportación limitada de las sesiones WMI actual y liberada, estado cTGP y política aplicada. Una prueba verifica el ZIP después de retirar el lease. |
| F10 | README contenía nombres de instaladores 1.3.2 y un límite GPU de ventiladores diferente del código actual 54/54. | Corregido para usar los archivos de la versión elegida y describir el contrato actual. Los enlaces históricos de validación conservan sus versiones. |
| F11 | La entrada N permitía 1–64 aunque la selección térmica requiere N menor o igual al número real de P-Cores. Aplicar ya comprobaba esa condición antes de modificar el control. | Corregido en desarrollo: edición limitada a la topología física completa y vigente. Sin lectura válida conserva el valor y espera para editar. Pruebas con seis P-Cores, lectura antigua, incompleta y duplicados. |
| F12 | La secuencia `native-dispatch.jsonl` se ampliaba con cada operación sin la rotación de los logs generales. Su escritura se realiza dentro del acceso WMI serializado. | Corregido en desarrollo mediante rotación a 5 MiB con un segmento anterior. La prueba comprueba conservación del contenido, una sola invocación y marcadores correctos. La posible influencia de la latencia de disco sobre la anomalía del PC sigue siendo una hipótesis. |
| F13 | Después de liberar una sesión con fallo de cTGP, la página podía mostrarlo desactivado y pendiente de Automático sin indicar que requería revisión. | Corregido en desarrollo: se distingue un fallo con liberación confirmada de una restauración todavía pendiente y se conserva su causa. |
| F14 | La actualización parcial CPU/GPU conservaba los valores aplicados pero dejaba preferencias anteriores para reanudar Automático. | Reproducido antes de la corrección con `ProductRuntime` real y guardián IPC simulado. Corregido: ambas rutas reconcilian la configuración confirmada; si la respuesta se pierde, cancelan la reanudación desatendida hasta recuperación explícita. Pruebas de éxito, rechazo, aplicación parcial y respuesta perdida. |
| F15 | Avanzado mostraba el rango y las protecciones de una revisión antigua durante uso habitual. | Corregido: rango nominal actual y política aplicada, con distinción entre edición, última sesión y sesión en uso. No cambia umbrales de control. |
| F16 | Recuperar Automático cerraba también el supervisor CPU/GPU y suspendía la conexión externa, aunque el fallo perteneciera a ventiladores. El recibo de cierre no distinguía esa retirada local de la liberación global. | Integrado y probado en desarrollo: la retirada de ventiladores conserva el propietario CPU/GPU con PID, inicio, nonce y sesiones exactas de sus registros; su lectura de estado continúa. Cierre, suspensión o preparación fallida mantienen la ruta global. La conexión externa no se suspende por una retirada local; su actuador todavía sigue el contrato anterior de órdenes internas. |
| R02 | Parte de las pruebas de interfaz utiliza un runtime de grabación. Esto no cubre por sí solo todas las combinaciones del `ProductRuntime` real, incluido el manejo de errores duplicado de F03. | Se añadió inyección explícita de dependencias para ejecutar el coordinador real sin hardware; F14 usa esa vía. Sigue pendiente ampliar esa cobertura a las funciones independientes y su recurso WMI compartido. |

La dependencia actual de cTGP y de la orden externa respecto a Automático es un contrato existente que se va a cambiar. No se clasifica como un defecto accidental del código anterior.

## Contrato de independencia

La pestaña **Funciones**, debajo de Rendimiento, separará estado solicitado, estado observado y configuración pendiente. Cada función ofrecerá activación/desactivación, configuración aplicada y motivo de espera o fallo. El control de ventiladores mantendrá sus tres modos exclusivos.

El comportamiento actual no equivale a encender y apagar todo con Automático. Hoy CPU/GPU se activan al iniciar Automático y pueden conservarse al detenerlo; cTGP se libera; la base sigue órdenes internas aceptadas, también en Manual. El preset de compatibilidad deberá reproducir esas diferencias.

Se proponen tres presets de coordinación: comportamiento actual, independiente y personalizado. La activación ligada a Automático será un evento de entrada. No se repetirá en cada ciclo, para permitir apagar CPU o GPU después. Una reanudación automática deberá conservar las decisiones confirmadas durante la sesión.

| Evento | Contrato propuesto |
|---|---|
| Detener o perder Automático | Liberar ventiladores; conservar CPU/GPU, cTGP y base que estén configurados como independientes y mantengan supervisión válida. |
| WMI ocupado antes de enviar | Estado En espera y reintento limitado. No fingir una lectura nueva ni duplicar órdenes. |
| Operación WMI de resultado desconocido | Cerrar nuevas órdenes del recurso compartido; conservar registros hasta confirmar su finalización. El paso del tiempo no prueba que haya terminado. |
| Pérdida real de AC | cTGP se restaura y mantiene la intención solicitada. CPU/GPU reconcilian su perfil por separado. La base conserva su política válida. |
| Suspensión o cierre de Windows | Liberar las autoridades correspondientes. Al volver, validar identidad, alimentación y nuevas lecturas antes de adquirir control. |
| Salir del programa | Liberación global explícita y verificada; el ESP32 adopta su política local cuando caduca la orden remota. |
| Recuperar una función | Actuar sobre su propietario y registros. Una recuperación global seguirá siendo una acción distinta. |

cTGP y ventiladores comparten transporte WMI físico. Sus propietarios y decisiones podrán separarse, pero una operación nativa incierta puede afectar a ambos. La coordinación debe dar prioridad a liberaciones y comprobaciones próximas a caducar, evitando que las lecturas periódicas monopolicen el transporte.

La base necesita separar conexión/LCD de su actuador. Las modalidades serán seguimiento actual, velocidad fija y curva propia; detener la salida remota será una orden explícita. El firmware actual exige una solicitud interna aceptada. Las modalidades nuevas requieren adaptar PC y protocolo/firmware, con detección de capacidades y conservación del seguimiento actual en dispositivos antiguos. Este trabajo no incluye flashear el ESP32 del usuario.

Se implementó una primera política pura de coordinación en `ProductFunctionCoordination`. Distingue selección para entrada ligada, solicitud actual y requisitos para adquirir cada controlador. Preserva el comportamiento heredado, el seguimiento externo en Manual, el requisito AC de cTGP y la desactivación del usuario durante una reanudación. Sus pruebas recorren 23040 combinaciones y las 16 selecciones heredadas. Todavía no está conectada a los controladores ni a la interfaz de producción; no activa funciones por sí sola.

El preset independiente conserva los atajos de activación seleccionados: Automático puede encender las funciones seleccionadas una vez, y la entrada en Manual puede iniciar la base seleccionada. Esas funciones podrán continuar después de detener el control interno. Ese preset tampoco apaga funciones independientes ya solicitadas cuando no están seleccionadas para el atajo. Desvincular elimina los atajos; la compatibilidad conserva la aplicación exacta de las selecciones antiguas.

## Integración pendiente del programa real

La vida de los servicios debe pertenecer a la aplicación. La etapa del 22 de noviembre integró `ProductPerformanceLifetime`: mantiene el supervisor CPU/GPU y sus consultas de estado al reemplazar el runtime de ventiladores. Su transferencia exige sesiones exactas de registros, proceso e inicio correctos, configuración confirmada y fuente AC/Batería válida. Las referencias antiguas no pueden cerrar el propietario ya transferido. El cierre global sigue siendo obligatorio ante Salir, reinicio completo, recuperación o preparación cancelada. HyperX conserva su vida de formulario; la conexión externa ya no se suspende durante la retirada local. El actuador externo sigue pendiente de separación.

La siguiente dependencia CPU/GPU está en su admisión: `PerformanceAdmissionPermitted`, `CanUpdatePerformance`, `ProductPerformanceUpdate.HasCurrentTelemetry` y `ProductCanvas.CanApplyPerformance` todavía incluyen parte del estado global y las RPM. Separar esos requisitos necesita salud por canal, fuente real y una barrera específica de suspensión/cierre. Debe permitir liberar un dominio sin exigir sensores de un actuador ajeno, conservando las comprobaciones de propietario. Un supervisor CPU/GPU parcialmente fallido también necesita tratamiento por dominio; no basta con cambiar un booleano de la interfaz.

El transporte HP WMI necesita un propietario compartido con estados separados de ventiladores y cTGP. Dos controladores con directorios de seguimiento independientes no bastan: una llamada fallida puede liberar el mutex conservando incertidumbre sobre el efecto nativo. Todos los usuarios del mismo recurso deben observar el mismo marcador de incertidumbre. Liberar ventiladores mientras cTGP continúa exige un recibo propio de ventiladores; el guardián compartido no debe terminar mientras conserve otra función activa.

La supervisión CPU/GPU existente ya admite dominios seleccionables. Su estado debe sobrevivir a los reintentos de ventiladores y conservar tanto los cambios confirmados como los resultados parciales. Antes de reutilizar un propietario hay que confirmar su identidad, sesión y estado; una referencia a un proceso no sustituye esos datos. F14 cubre ya resultados parciales y respuestas perdidas en el coordinador real con dependencias aisladas. La vida independiente de sus propietarios todavía debe implementarse.

La telemetría debe expresar salud por canal y antigüedad original. Un control externo fijo no necesita RPM internas; una curva externa requiere los sensores de su propia fuente. La ausencia de una lectura WMI no debe convertirse automáticamente en indisponibilidad de todos los servicios.

Funciones mostrará activación solicitada, actividad confirmada, espera y fallo por componente. Los límites y curvas permanecerán en sus editores. Una preferencia de activación al iniciar el programa no certifica que exista una sesión de hardware activa. Los archivos antiguos deberán migrarse conservando sus curvas y selecciones, con respaldo; identificadores de propietarios y confirmaciones seguirán siendo datos de ejecución.

## Verificación y siguientes etapas

La base modificada ha compilado con cero errores y advertencias. Se completaron 33 grupos de pruebas de software de interfaz, telemetría, límites, recuperación, apagado simulado, HyperX, red, protocolo e instalador. Se añadieron verificaciones específicas de conservación de intención y de presentación de protecciones, además del proyecto de coordinación pura. Pasan también la exportación después de liberar WMI y la rotación del registro nativo. La regresión completa de interfaz, incluido el diagnóstico actualizado, completó en 127,67 s. Los registros distinguen las etapas y compilaciones verificadas.

El primer intento del conjunto completo de interfaz alcanzó el presupuesto del ejecutor de 120 s. Una repetición con presupuesto de 360 s completó en 117,23 s. Esto se conserva como incidencia del ensayo; no demuestra un bloqueo de la aplicación en uso normal ni autoriza ampliar automáticamente los tiempos de control.

Las pruebas utilizan controladores simulados, procesos de prueba y comunicación local. La instalación, firmware, reinicio real de Windows y respuesta física de ventiladores no se certifican mediante estos resultados.

El orden de trabajo es cerrar la regresión de recuperación, completar las discrepancias de presentación/diagnóstico, introducir controladores y pruebas del coordinador real por función, separar propietarios WMI, implementar Funciones, adaptar el protocolo externo y ejecutar la matriz completa de combinaciones. La instalación y publicación quedan para una versión terminada y verificada.

El usuario entregó después ocho archivos para una renovación visual posterior: tres referencias de Configuración, Inicio y Curvas, cuatro iconos y un rotor. Sus copias exactas y estudio están bajo `output/interface-references-20261121`. La especificación está en `docs/INTERFACE-REFERENCE-STUDY.md`. Esa propuesta usa negro/grafito, contornos blancos biselados y los PNG transparentes entregados. Los números de las referencias son ejemplos gráficos; no reemplazan ajustes ni curvas. La renovación completa se realizará después del trabajo funcional, según el orden solicitado.

La etapa de vidas independientes está verificada en `output/independent-functions-stage2-20261121`: doce grupos de regresión pasan, incluidos cuatro enmascaramientos CPU/GPU con tres reemplazos de ventiladores cada uno, fallos de preparación, cierre durante retirada y confirmación de una lectura tardía. La última compilación tiene cero errores y advertencias. Los 33 archivos vigilados de instalación y ajustes siguen sin diferencias. La integración completa de políticas, Funciones, cTGP y actuador externo continúa pendiente.

Las referencias de implementación son las clases `WmiFanExperimentBoundary`, `WmiFanGuiGuardianHost`, `WmiFanGuiGuardianClient`, `Hp8C40CtgpControl`, `Hp8C40WmiFanControlBackend`, `ProductRuntime`, `ProductProtections`, `ProductDiagnostics`, `TelemetryExportService`, `SelectableGuiPerformanceDomains`, `UnifiedFanDemand`, `ExternalController` y el instalador `ProductSetup`. Las evidencias de esta etapa están bajo `output/independent-functions-stage1-20261121` en el directorio del proyecto.
