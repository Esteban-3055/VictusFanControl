# Independencia de funciones: estado de desarrollo

22 de noviembre de 2026. Rama `feature/independent-functions`, basada en `e6c284f202d8f475808603f8e77989feecb759b7` (1.14.1). El usuario eligió **1.15.0 preliminar** después de cerrar estas pruebas de desarrollo. Las copias de las etapas 3/4 conservan sus identidades anteriores al versionado. Los archivos de `sources/` y la instalación del usuario no se modificaron durante esas verificaciones.

## Comportamiento implementado

Funciones aparece inmediatamente debajo de Rendimiento. CPU, GPU, cTGP y base tienen solicitudes separadas de encendido/apagado. El estado observado se muestra aparte; una solicitud no certifica una escritura ni un propietario activo. Los límites siguen editándose en Rendimiento y la curva de la base en Curva Externa.

| Vínculo | Entrada en el modo | Salida o fallo del modo |
|---|---|---|
| Sin vínculo | No cambia la solicitud | Conserva la solicitud |
| Iniciar con Automático | Aplica la selección una vez | Conserva la solicitud |
| Seguir Automático | Aplica la selección una vez | Retira el control y conserva la intención para la siguiente entrada |
| Iniciar con ventiladores | Al entrar en Manual/Automático aplica la selección | Conserva la solicitud |
| Seguir ventiladores | Al entrar en Manual/Automático aplica la selección | En Firmware retira el control y conserva la intención |

Los presets son comportamiento anterior, independientes y sin vínculos; editar cada opción produce una configuración personalizada. El preset independiente conserva los atajos seleccionados y las funciones independientes que ya estaban solicitadas. La reanudación y recuperación no repiten el atajo ni deshacen un apagado explícito del usuario. Iniciar con la aplicación es una preferencia separada, desactivada por defecto.

Aplicar vínculos modifica la coordinación de la sesión; Guardar conserva el borrador para próximos inicios. Los perfiles antiguos siguen cargando su comportamiento anterior. La primera escritura de `functions` guarda una copia exacta `.pre-functions-<hash>.json`; nunca se persisten PID, sesiones o confirmaciones como autoridad de hardware.

## Recursos y límites

CPU/GPU usan un supervisor de aplicación separado del runtime de ventiladores. Activar o actualizar un dominio exige sus propios sensores vigentes y fuente confirmada, sin exigir RPM o datos del otro dominio. La retirada usa `RELEASE_DOMAINS`, ligada al propietario y selección exactos; no necesita sensores ni fuente disponible. Una solicitud pendiente puede cancelarse aunque no haya arrancado el supervisor. Los estados y sesiones reales del otro dominio se conservan.

Ventiladores y cTGP comparten un único guardián, mutex y barrera de resultado nativo incierto. Cada sesión de ventiladores tiene una época y un recibo propio de liberación. Retirar ventiladores no termina al guardián que conserva cTGP. Iniciar solo cTGP emite cero setters de ventiladores. Salir globalmente solicita y verifica la restauración antes de cerrar los identificadores del supervisor.

cTGP sigue siendo la política HP fija calificada, con cargador HP 200 W comprobado en cada adquisición; no se añaden potencias arbitrarias ni se modifican bytes térmicos. En batería se restaura y conserva la solicitud. Un rechazo anterior a adquirir la política afecta a cTGP; una operación nativa de resultado desconocido sigue bloqueando el recurso WMI compartido. Solo se reintentan automáticamente admisiones no enviadas que estén expresamente clasificadas como tales.

Las opciones de fallo CPU/GPU y cTGP permiten conservar el acoplamiento anterior con Automático o retirarlo. La pérdida de sus confirmaciones no autoriza escrituras nuevas del componente fallido. Las comprobaciones térmicas y las fechas de adquisición siguen vigentes. Los tiempos y su significado están en `SENSOR-TIMING-AUDIT.md`.

Un fallo al preparar el nuevo backend de ventiladores, tras una retirada limpia y comprobada, puede abrir un runtime sin control de ventiladores conservando los supervisores independientes. Si WMI conserva un resultado nativo incierto, el reintento local se detiene antes de reemplazar propietarios o liberar CPU/GPU; el cierre global explícito mantiene sus comprobaciones y registros.

## Base externa

Se conserva la conexión y el LCD separados del actuador. Las tres modalidades son:

- Seguimiento del nivel interno aceptado: contrato anterior, también disponible con firmware antiguo.
- Salida fija de 0 a 255: no depende de RPM ni de sensores internos. Cero solicita parada remota; Apagar retira la autoridad y devuelve la base al control local.
- Curva propia por temperatura: usa el máximo entre CPU normalizada de 40 a 90 °C y GPU de 35 a 81 °C, limitado a 0–100 %. La curva externa transforma ese porcentaje en salida; no reinterpreta niveles 10–54 como porcentajes.

El mensaje `independentExternal` está separado de la evidencia de una orden interna. Nunca se fingen Manual, `SolicitudValida` o `WmiAcceptedSupervised`. El PC exige la capacidad `independentControlVersion=1`, configuración aceptable y una respuesta del dispositivo de menos de dos segundos. Un dispositivo antiguo continúa monitorizando y retira la solicitud incompatible.

El firmware C++ valida modo, tipos, rangos, identidad de sesión, MAC direccional, secuencia de respuesta y antigüedad de la muestra. El permiso remoto sigue caducando a los dos segundos; la vuelta local conserva 130/255 y la inercia existente. La curva propia deja de enviar una solicitud si faltan sus temperaturas actuales. Suspensión, MonitorOnly y desconexión retiran también las órdenes independientes. No se grabó ningún binario en la placa del usuario.

## Fallos adicionales encontrados durante esta etapa

| ID | Hallazgo | Corrección y evidencia |
|---|---|---|
| F17 | La admisión/retirada CPU/GPU dependía de RPM y del estado global de ventiladores. Un par fallido sin cambios podía bloquear al dominio sano. | Admisión por canal y retirada autenticada por máscara. Pruebas del runtime e IPC reales con lectores/controladores aislados. |
| F18 | El cargador se calificaba solo en la primera adquisición de cTGP. | La lectura de 200 W se repite al adquirir de nuevo. Prueba de cambio simulado a 65 W: cero setters adicionales. |
| F19 | Un rechazo de cTGP sin adquisición cerraba el guardián compartido de ventiladores. | Se distingue rechazo local de política adquirida/resultado nativo incierto. El host real de prueba conserva la sesión ante rechazo y admisión ocupada no enviada. |
| F20 | La primera implementación de la nueva vida WMI cerraba el identificador sin ejecutar su liberación global. No llegó a instalarse. | Reproducido por la prueba nueva de transferencia y cierre; corregido con liberación y recibo antes de Dispose. |
| F21 | Cambiar AC/Batería antes de la primera orden de ventiladores exigía autoridad Custom y un objetivo ya aplicado. | Se admite espera/configuración sin escrituras mientras Firmware aún es propietario y no existe un objetivo anterior. La seguridad térmica y la adquisición vigente se mantienen. Prueba del runtime con cambio inmediato y retorno, sin supervisor CPU/GPU. |
| F22 | La revisión de transición independiente todavía consultaba la confirmación CPU/GPU al esperar una fuente. | La opción independiente usa la fuente y seguridad de ventiladores; la compatibilidad mantiene el requisito anterior. Prueba AC/Batería del runtime. |
| F23 | Cancelar límites sin proceso no borraba/publicaba la solicitud pendiente. | Cancelación de intención antes de comprobar el proceso y publicación del resultado; prueba con sensor propio ausente y cero activaciones. |
| F24 | Reintentar ante un resultado WMI incierto podía escalar a una retirada global de controles sanos. | El reintento se retiene antes de cerrar el runtime. Prueba de formulario: cero retiradas/disposiciones hasta Salir explícito. |
| F25 | Guardar/restaurar temporalmente toda la solicitud durante una retirada podía perder otra decisión concurrente. | Se retira sin modificar la intención y las modificaciones de banderas usan intercambio atómico. Detectado por revisión; no se atribuye a un incidente físico observado. |
| F26 | Una preparación fallida podía dejar una solicitud sin propietario disponible para nuevos intentos de fondo. | Fallo de adquisición por función retenido hasta nueva acción explícita; no se reenvía una orden de resultado desconocido. La compatibilidad conserva su bloqueo de reanudación ante respuesta perdida. |
| F27 | En la nueva separación, cTGP independiente podía esperar a un sondeo para retirarse ante suspensión, y una solicitud anterior podía conservar permiso de adquisición. No llegó a instalarse. | Retirada explícita de cTGP en lifecycle y barrera síncrona comprobada tras esperar al inicio/cola. La prueba impide adquirir aun si un solicitante entrega el permiso antiguo; conserva intención y recalifica tras reanudar. El reintento local de ventiladores usa una barrera distinta de la suspensión del sistema. |
| F28 | La opción de detener Automático por fallo cTGP dependía de una selección hecha al entrar en Automático; una activación posterior podía quedar fuera de esa comprobación. | Comprobación por solicitud, vínculo y fuente actuales; se distingue selección pendiente de pérdida de propiedad confirmada. Prueba del runtime activa cTGP después de Automático, introduce una observación de fallo aislada y verifica la cesión. |
| F29 | Una orden explícita Apagar base sobre perfiles heredados podía dejar activo el exportador antiguo, que aún no consultaba las nuevas solicitudes. | Esa acción entra explícitamente en la coordinación de la sesión. La prueba del runtime y adaptador verifica que el seguimiento heredado retire la orden remota. La interfaz no presenta el seguimiento heredado como un apagado confirmado. |
| F30 | La curva externa independiente podía calcular una demanda baja con una topología térmica CPU incompleta o valores cero inválidos, aunque otras funciones estuvieran bien. | Admisión por sus temperaturas actuales y mapa de núcleos completo/válido, sin exigir RPM ni potencia. Las pruebas rechazan núcleos ausentes, NaN y temperaturas cero; la pantalla indica espera por temperaturas cuando el dispositivo permanece en control local. |

F01–F16 y los hallazgos de interfaz/control anteriores se conservan en `INDEPENDENT-FUNCTIONS-AUDIT.md`. La incertidumbre sobre el origen físico del atasco WMI inicial no se convierte en una atribución causal a ventiladores, audio o hardware.

## Verificación y entrega

Durante una repetición, la prueba TCP de reconexión canceló justo después de autenticar, antes de observar el primer mensaje de la segunda sesión. Su aserción lo llamó reutilización de sesión aunque todavía no existía esa observación. Se corrigió la sincronización de la prueba: espera la segunda respuesta antes de cancelar y sigue comprobando que los identificadores observados sean diferentes. El fallo previo se conserva en `external-protocol-tcp-observation-race.log`; no se atribuye a un fallo físico de la base.

La evidencia está en `output/independent-functions-stage3-20261122`, desde la raíz del proyecto. `final-results.json` registra códigos y duraciones; `installed-verification.json` compara 33 archivos de instalación/ajustes. El archivo de identidad identifica los binarios comprobados.

Las pruebas incluyen las 16 selecciones de funciones en el coordinador real, procesos guardianes con IPC real y un fallo térmico de ventiladores simulado. Conservan las sesiones CPU/GPU exactas, cTGP, salida fija externa e intención; después verifican el cierre global. La matriz pura de política recorre otras 23040 combinaciones, sin confundirlas con pruebas físicas.

El protocolo se contrasta entre el cliente Windows y el parser/controlador C++ de producción mediante TCP local. Se prueban salida fija cero/máxima, curva, retirada, autenticación, muestras antiguas/futuras, mensajes incompletos y límites. Compilan las variantes ESP32-C3 de monitorización y PWM de banco sin flasheo. El primer intento local encontró un problema de rutas largas de las herramientas; la compilación repetida con alias temporales de ruta terminó correctamente y retiró esos alias.

Permanecen fuera de esta evidencia: respuesta física de la nueva separación en el portátil/ESP32, suspensión y reinicio reales, una instalación nueva y CI/GitHub de esta rama. No hay una versión de entrega nueva. Se debe revisar el comportamiento con hardware y preparar el paquete completo antes de publicarlo o sustituir la instalación.

La renovación visual se implementó después en la etapa 4, sin sustituir la instalación. Su estado, F31 de foco/accesibilidad y F32 de indicadores/fuente externa están en [INTERFACE-IMPLEMENTATION.md](INTERFACE-IMPLEMENTATION.md). La [guía de uso](INDEPENDENT-FUNCTIONS-USER-GUIDE.md) explica los vínculos y la diferencia entre solicitud, confirmación y guardado. La etapa 3 conserva sus binarios congelados como evidencia funcional anterior al tema nuevo. F29/F30 y la interfaz final se verifican además en la copia de la etapa 4.
