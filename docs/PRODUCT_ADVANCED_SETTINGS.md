# Avanzado

La nueva entrada lateral recupera opciones que consume el motor actual, sin
reintroducir el editor de seis curvas, exportaciones antiguas, autorizaciones ni
controles que el backend protegido ignora. La respuesta temporal es común para
AC y Batería; cada fuente conserva su curva e influencias y sus límites CPU/GPU.
Editar cualquier ajuste común actualiza ambos borradores para que una transición
de alimentación no reemplace silenciosamente la inercia activa.

## Controles

- Temperatura CPU: paquete/núcleo máximo, media física, media de P-Cores o media
  de los N P-Cores más calientes. N debe existir en la lectura real al aplicar.
- Suavizado e inercia: filtros y confirmaciones de subida, bajada breve y
  prolongada/fija, y paso normal de subida. Se admite texto decimal con punto o coma.
- Historial: activar descenso adaptativo; duración acumulada, utilización,
  potencia CPU/GPU que cuenta como carga, pausa tolerada y reposo de recuperación.
- Respuesta térmica: umbrales de subida urgente CPU/GPU, conservar o no el pico
  en el filtro normal y pausa de adquisición normal.

El máximo 50, el mínimo de la entrada física autorizada, el paso térmico 4 y
el paso de bajada 1 se presentan como valores protegidos. La GUI no ofrece un
control de bajada de 2 que el backend actualmente reduciría a 1. La fuente de
emergencia siempre sigue siendo paquete o núcleo máximo. Los umbrales de
interrupción no son preferencias editables.

## Editar, aplicar y guardar

Editar cambia el borrador y el simulador, sin IO de hardware. Cada campo muestra
su rango y el valor de la configuración del motor; el panel distingue diferencias
pendientes y preferencias sin guardar. Restablecer ajusta únicamente la respuesta,
sin cambiar curvas, influencias ni rendimiento. Guardar conserva las preferencias
sin aplicar hardware.

Aplicar en Automático requiere telemetría fresca, CPU/GPU vigentes, fuente
coincidente, sesión actual y ninguna recuperación, cancelación o transición.
Mantiene el nivel físico solicitado, la EMA, la continuidad de muestras, el ID
y el plazo de revisión. Invalida la decisión anterior y reinicia las confirmaciones
para que una espera ganada con los parámetros antiguos no autorice un paso nuevo.
La próxima adquisición decide el siguiente comando. El cambio no envía por sí
mismo una escritura ni adquiere autoridad.

Cambiar la definición de carga (activación adaptativa, duración de cualificación,
utilización o potencia umbral) reinicia ese historial. Los cambios de respuesta
conservan el historial. Una modificación de reposo/tolerancia se evalúa en la
siguiente adquisición, sin inventar tiempo observado.

En Firmware, Preparar ajustes actualiza la configuración preparada sin activar
ventiladores; elegir Automático después usa el borrador completo congelado por el
clic. Manual y sesiones interrumpidas no permiten aplicar. Guardar sigue estando
disponible para un inicio posterior.

## Pendientes para cerrar la versión

La prueba prolongada del motor está documentada en PRODUCT_VALIDATION_STATUS.md.
La aplicación de ajustes dispone de fixtures sin hardware y una regresión física
breve aprobada: filtro de subida 7 → 8 s durante la misma activación, diagnóstico
`20261006T204954587Z-1c74bf2b83ad43f7bf160eda608d5932`. Esto no certifica todos los
parámetros, la suspensión ni el cambio de definición de carga bajo carga real.
El cambio de límites CPU/GPU en vivo propuesto es
una tarea independiente del Guardian y todavía no está implementado. Mientras
tanto el panel de rendimiento explica que Firmware conserva los límites y que
Desactivar límites sale de Automático a Firmware antes de liberarlos; Manual mantiene su control independiente.
