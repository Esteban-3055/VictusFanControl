# Interrupciones al cambiar de AC/batería y al arrancar con batería

Corrección posterior a la release 1.2.16. No forma parte del instalador 1.2.16 ya publicado y no se ha instalado sobre la sesión activa.

## Hallazgos reproducidos

1. La GUI consulta la alimentación real, pero el supervisor conservaba su última fuente hasta recibir una notificación de Windows. Si esa notificación no llegaba, `STATUS` podía seguir informando batería después de pasar a AC (o viceversa). El reconciliador GUI agotaba su espera aunque continuara recibiendo respuestas recientes. Una respuesta reciente no implica una adquisición nueva de alimentación.
2. La respuesta IPC obtenía CPU, GPU y fuente mediante varias lecturas independientes. Una transición concurrente podía intercalarse y mezclar estados de momentos distintos.

Se escribieron regresiones que fallaron antes de la corrección: `Missed Windows notification left the explicitly enabled Battery/AC profile stale` y `A source transition interleaved with domain/status capture`. Las mismas pruebas pasan después. Esto demuestra defectos de software; no demuestra que Windows haya perdido una notificación en un incidente físico concreto.

La sesión actual consultada en lectura seguía produciendo decisiones automáticas. Los registros históricos encontrados incluyen una interrupción por confirmación/fuente no válida, pero su mensaje genérico no identifica por sí solo cuál de las condiciones falló. No se atribuye una desconexión física al valor batería informado por Windows ni se declara causalidad eléctrica.

## Corrección

En una sesión GUI explícitamente habilitada, un `STATUS` válido también reconcilia la fuente mediante una nueva consulta a `GetSystemPowerStatus`. Reutiliza el coordinador y los presets ya autorizados: los duplicados no escriben de nuevo. No habilita una sesión, no agrega dominios y no reemplaza los parámetros guardados. Los hosts históricos de calificación conservan el comportamiento de consulta anterior.

La captura de respuesta mantiene la misma exclusión que las transiciones y lee una sola instantánea de los dominios. CPU/GPU, fuente y configuración se capturan juntos; el bloqueo se libera antes de codificar o escribir en el canal IPC. El envoltorio de suspensión/pantalla comparte esta coordinación y bloquea la reconciliación cuando corresponde.

No se reconcilia desde `STATUS` antes de habilitar, después de detener, con pantalla apagada/suspensión ni después de un fallo retenido. No se reintentan mutaciones fallidas ni se borra evidencia. Una fuente desconocida conserva la liberación y las sesiones CPU/GPU deshabilitadas no se recrean por detectar una fuente conocida.

Se añade `PRODUCT AUTOMATIC INTERRUPTION EVIDENCE` al registro local: perfil seleccionado, candidato de transición, vigencia/estado del supervisor, configuración aplicada y muestra exacta. No contiene credenciales Wi-Fi/HMAC.

## Arranque y continuidad

- Arranque directo en AC y en batería, cambio al otro perfil sin notificación, retorno y notificación duplicada: cubiertos con supervisor real y sumideros simulados.
- Cambio batería → AC durante la espera inicial: reinicia el conteo y exige tres adquisiciones nuevas de AC; no reutiliza las muestras de batería.
- Controlador real de producto: configuración inicial de batería antes de Automático, recorridos batería → AC → batería y AC → batería → AC, curvas y tiempos distintos, autoridad conservada, inercia acotada y ninguna orden de ventiladores durante la reconciliación.
- Captura concurrente, fallo parcial CPU/GPU, conservación del fallo, Stop y pantalla apagada: cubiertos con fixtures sin hardware.

Compilación sin advertencias y suites completas de supervisor/WinForms: PASS local. CI debe validar el commit antes de distribuir un instalador nuevo. No hubo cambio físico de alimentación, escrituras de prueba al portátil ni prueba térmica nueva.

## Límites conservados

La espera global GUI sigue siendo 8 s y el estado del supervisor sigue requiriendo menos de 6 s. No cambia la frescura de sensores, la confirmación de límites, la pausa sin nuevas órdenes ni las protecciones térmicas de 1.2.16. Un fallo real de CPU/GPU, una llamada nativa incierta, una fuente desconocida, una suspensión o una liberación incompleta pueden interrumpir Automático de forma deliberada. No se permite continuar sólo para ocultar esos errores.

ESP32, protocolo HMAC, NVS, pines y adquisición HP WMI permanecen sin cambios; no se añade acceso EC ni sensores TZ01/DTT3.
