# Cierre de Windows y recuperación tras un error de liberación GPU

## Evidencia y alcance

El 20 de octubre de 2026 la tarea de inicio abrió 1.2.17 correctamente. Automático
se interrumpió durante la preparación por falta de una adquisición completa.
Después, el Reset GPU devolvió NVML 999 y dejó el journal en ReleaseWriteArmed.
Los intentos posteriores reutilizaban la tarea de cierre fallida y no llegaban
al recuperador. La última sesión del día anterior también conservaba un registro
sin confirmación final de liberación. Un apagado correcto de Windows no demuestra
que la aplicación confirmara su limpieza ni que los límites persistieran al arrancar.

Este cambio corrige el cierre y el bloqueo de recuperación. No demuestra la causa
del atasco NVML, no modifica los perfiles de batería/AC ni relaja la frescura de
sensores. Una consulta posterior de nvidia-smi que funciona no confirma el Reset
del rango de frecuencias: NVML no permite consultar aquí el rango instalado exacto.

## Cierre normal y de Windows

- La salida usa una sola tarea de cierre y bloquea nuevas órdenes inmediatamente.
- WM_QUERYENDSESSION responde afirmativamente; si el apagado se cancela,
  WM_ENDSESSION(false) no libera la sesión activa.
- WM_ENDSESSION(true) espera la limpieza, atendiendo las continuaciones de la
  ventana para no interbloquear operaciones pendientes. Registra un motivo breve
  para el diálogo de apagado de Windows. Su presupuesto es de 20 segundos.
- Agotar ese tiempo no cancela un mutador nativo, no elimina journals y no se
  interpreta como liberación. Windows o el usuario pueden forzar la terminación.
  Los registros duraderos permiten la recuperación del siguiente arranque.

La distinción entre consultar y confirmar el cierre sigue la documentación de
[WM_QUERYENDSESSION](https://learn.microsoft.com/en-us/windows/win32/shutdown/wm-queryendsession)
y [ShutdownBlockReasonCreate](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-shutdownblockreasoncreate).
Windows [no garantiza que una aplicación pueda impedir su terminación](https://learn.microsoft.com/en-us/windows/win32/shutdown/shutdown-changes-for-windows-vista).

## Liberación fallida frente a recursos cerrados

El traspaso a recuperación se admite únicamente después de:

1. Un Reset GPU armado previamente en el journal que devuelve un resultado NVML
   concreto distinto de cero. Una excepción nativa, un timeout, la ausencia de
   resultado o un fallo al armar el journal no producen esta evidencia.
2. Detener correctamente el observador de energía; CPU desactivada si estaba
   habilitada. Solo SHUTDOWN autenticado puede solicitar el cierre del propietario.
3. Una respuesta privada explícita `SESSION_RELEASE_FAILED_DRAINING_FOR_RECOVERY`
   con la sesión GPU exacta, seguida de la salida real del proceso con código 23.
   La respuesta sola es insuficiente. Desde 1.3.0, si se pierde, puede sustituirse
   por el recibo duradero validado del mismo nonce/equipo/padre/hijo/horas/sesión,
   siempre después de comprobar la salida real. La salida sola nunca basta.
4. Liberar normalmente ventiladores, telemetría y tareas del runtime. Cualquier
   otro error conserva el bloqueo. La instancia anterior se dispone una sola vez.

El propietario termina con fallo, sin declarar Stopped ni volver a ejecutar Reset.
La GPU conserva su journal. La ventana retiene la referencia al propietario
cerrado aunque su tarea de cierre haya fallado. Así un reintento no queda atrapado
en la excepción guardada ni dispone dos veces el controlador.

La recuperación exige que siga presente el journal GPU exacto y que no haya CPU
pendiente. Conserva las verificaciones existentes de identidad, inventario de
ventiladores, exclusión de propietarios y respaldo duradero. El recuperador abre
un backend nuevo y realiza un Reset, sin Set. Solo su informe validado y la
ausencia comprobada de registros pendientes completan la liberación del runtime
anterior y permiten abrir el siguiente. El borrador y los perfiles se conservan.

Si el recuperador falla, se puede volver a solicitar recuperación explícita sin
repetir Dispose. No se añaden ciclos automáticos de Reset: se conserva la política
de un intento automático por inventario exacto y sus límites de episodios. La
reanudación de Automático sigue exigiendo preferencias autorizadas y tres
adquisiciones frescas. La recuperación manual abre en Firmware.

## Validación y límites

Las pruebas usan controladores ficticios y procesos IPC reales sin hardware:

- Reset retornado 999, sin resultado, excepción nativa y fallo de journal;
  conservación de registros y cero Reset implícitos al disponer.
- Rechazo de CPU activa/desconocida, observador sin detener, GPU aún liberándose,
  prueba ausente y comandos distintos de SHUTDOWN.
- Respuesta autenticada más salida del hijo, propietario desconocido retenido,
  fallo conservado y una sola liberación sin declarar Stopped.
- Reintento después de una recuperación fallida y una tarea de cierre fallida;
  rechazo de journal ausente, cambiado, CPU pendiente o limpieza desconocida.
- Consulta/cancelación de apagado, fin de sesión confirmado, continuación en el
  hilo de la ventana, límite de espera y fallo de liberación conservado.

Validación local completada el 20 de octubre de 2026: build Release con cero
errores/advertencias, los 19 grupos de regresión, CpuProbe `--self-test` y
PerformanceGuardian `--self-test`, todos PASS. Los resultados locales se guardan
fuera del repositorio en `../output/session-recovery-*.log`; el manifiesto
`../output/session-recovery-validation.json` identifica los archivos ensayados
por SHA-256 además del commit de partida, porque son cambios locales sin publicar.

No se ha instalado esta corrección, apagado Windows ni reproducido el fallo en el
hardware. El controlador de la instancia instalada 1.2.17 no adquiere este nuevo
comportamiento por compilar otra copia. La observación de una actualización limpia
y de un ciclo real de salida/arranque en batería sigue pendiente y se declara
como tal en la entrega 1.3.0; no se sustituye por los fixtures.
Un bloqueo del controlador nativo que no retorna permanece cerrado a nuevas
escrituras; no se promete recuperar cualquier fallo de Windows o del driver.
