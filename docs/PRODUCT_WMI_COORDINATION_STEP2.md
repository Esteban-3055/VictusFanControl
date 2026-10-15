# Coordinación interna de lecturas HP WMI — paso 2

Las solicitudes de comprobación de ventiladores que coinciden mientras esperan turno pueden compartir una única adquisición. El grupo se cierra antes de comenzar la lectura nativa. Una solicitud posterior al inicio crea una adquisición nueva; una respuesta completada nunca se reutiliza como prueba nueva.

Cada destinatario conserva su cancelación y límite de espera. Cancelar un destinatario no cancela a los demás. Si todos abandonan antes del envío, no se consulta hardware; si abandonan durante una llamada nativa, su turno queda ocupado hasta que esa llamada termina realmente. Los errores se propagan a los destinatarios sin recurrir a una muestra antigua.

Se conserva la identidad de adquisición, la hora original y la comprobación de antigüedad de cada destinatario. El resultado se publica una sola vez y no cruza las épocas de pausa, suspensión o reemplazo del lector.

El diagnóstico diferencia solicitantes de comprobación, solicitantes agrupados y lecturas nativas periódicas/de comprobación. Son contadores del proceso, no mediciones de otros programas ni prueba de una reducción de consultas ya observada en hardware.

## Alcance

- Se mantiene la cola nativa compartida y la exclusión entre procesos de las operaciones HP participantes.
- Se conserva la publicación de comprobaciones nuevas al lector periódico.
- No se agrupan escrituras, liberaciones ni lecturas que comenzaron antes de la solicitud que necesita una comprobación nueva.
- El intervalo periódico sigue siendo el anterior: este paso no aplica todavía los 5 segundos discutidos.
- CPU/GPU, sus protecciones y las reglas de frescura no cambian. TZ01/DTT3 siguen retirados y no se introduce acceso directo al EC.
- No se coordina ni bloquea a aplicaciones externas.

## Comprobación

Pruebas con transporte simulado: 16 solicitantes en espera producen una lectura nativa y una publicación; una solicitud posterior al inicio obtiene una lectura nueva. También se comprueban cancelación del primer/todos los destinatarios, plazos diferentes, fallo del proveedor, respuesta inválida, caducidad, entrega retrasada y pausa/recreación del lector.

La batería existente de caché, cola, diagnósticos, comprobaciones y límites WMI debe seguir pasando con `--fan-wmi-telemetry-self-test`. La compilación y las pruebas no activan sensores reales.

Esta modificación no acredita una causa del evento de alimentación y no tiene comparación física de carga/latencia. La instalación y la observación del equipo se registran por separado.

## Resultado local

Compilación Release aprobada, sin advertencias ni errores. Pasaron los nueve casos nuevos de agrupación y la batería completa de telemetría HP WMI, incluyendo caché, cola, diagnóstico, pruebas de lectura y exclusión de EC. También pasaron las pruebas simuladas del backend de control, retirada de sensores, coordinación de telemetría y los invariantes de control, telemetría y cualificación de Automático.

La suite general de interfaz pasó en la compilación local 1.2.9 tras aislar las pruebas de los registros reales de recuperación y del DPI del anfitrión. Incluye reintento del instalador, protecciones, recuperación, arranque y ciclo de vida. Estos resultados no equivalen a una prueba de hardware.
