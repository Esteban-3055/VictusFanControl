# Uso habitual: GPU y transición de alimentación en 1.2.16

Automático habitual con protección GPU activada admite temperatura GPU hasta **95 °C inclusive**. Una adquisición válida **>95 °C** provoca retorno a Firmware; no apaga Windows ni ordena detener los ventiladores. Entrada, ejecución, espera de cambio de fuente, actualización de límites y reanudación comparten el umbral. La CPU conserva entrada ≤90 °C, confirmación ≥95 °C hasta 2000 ms y retorno inmediato ≥99 °C. El enfriamiento conservador ante temperatura cruda alta sigue funcionando.

Las revisiones físicas explícitas de 5/45 minutos conservan GPU ≤82 °C, CPU 60 W y el contrato histórico del núcleo ≥87 °C. Son pruebas distintas de Automático habitual. `SafetyGate` y sus constantes históricas no se modifican; la política del producto habitual se selecciona en memoria, sin agregar autorización a perfiles JSON ni a NVS.

No se modifica el throttling térmico del fabricante. Este umbral de software no garantiza que GPU alcance 95 °C: el driver/firmware conserva sus propios límites. CPU >60 W ya no cancela Automático habitual, y el máximo GPU configurable es 210–2535 MHz en ambos perfiles, sin cambiar los valores guardados ni los defaults 1850/1200.

## Investigación de AC y batería

La fuente se lee mediante `GetSystemPowerStatus`: Windows informa AC, batería o desconocida. Un estado batería informado no demuestra por sí solo una desconexión física ni su causa. La notificación de energía sólo solicita una consulta nueva: no se usa su llegada como prueba de la fuente. El supervisor serializa cambios, evita reaplicar por eventos duplicados y libera ante una fuente desconocida.

Se encontró una carrera reproducible en el reconciliador GUI: después de AC → batería → AC podía admitir una respuesta del supervisor recibida antes del último cambio de candidato. Ahora captura el identificador de respuesta al comenzar **cada candidato**. Exige una respuesta posterior, ambos dominios activos, configuración y fuente coincidentes, estado fresco y al menos dos observaciones separadas por un segundo. Esto corrige un defecto de software; no establece que fuese la causa de un incidente físico anterior.

La espera total de reconciliación pasa de 4 a **8 segundos** para permitir confirmar el perfil; los rebotes no reinician el plazo. No se despachan nuevas órdenes de ventilador mientras espera. No se amplía la vigencia de sensores ni se admiten respuestas parciales, fallidas o antiguas. Fuente desconocida, agotamiento del plazo, CPU ≥85 °C durante la espera o GPU >95 °C devuelven el control a Firmware. La espera independiente para aplicar límites en vivo conserva su plazo de 4 segundos.

Si faltan campos críticos, la política distinta de datos retenidos conserva su corte anticipado CPU ≥85/GPU ≥78 °C: un valor retenido no equivale a una temperatura GPU fresca admitida hasta 95 °C. Las exclusiones de llamadas WMI inciertas y los journals pendientes siguen bloqueando nueva autoridad; no se borran para desbloquear la instalación.

## Evidencia y alcance

Fixtures sin hardware cubren 82/83/87/94.999/95 °C admitidos, 95.001 °C rechazado, núcleo térmico real con políticas habitual e histórica, datos inválidos, despacho, deadlines CPU, respuesta nueva tras rebote, cambio lento después de 4 s y antes de 8 s, límite exacto de 8 s, fuente desconocida, reloj regresivo y estados parciales. Las pruebas históricas de fuente y de recuperación continúan formando parte de CI.

La publicación estable está autorizada para uso habitual en el destino exacto HP 8C40/F.18. Las pruebas nuevas son evidencia de software, sin escrituras físicas en este equipo ni calificación térmica nueva. La observación bajo carga y los cambios físicos AC/batería de esta versión quedan pendientes; se declaran en el contrato distribuido.
