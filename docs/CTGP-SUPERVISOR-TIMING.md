# Supervisor cTGP: trabajo, espera y suspensión

La 1.3.17 corrige el falso `LIFECYCLE_GAP` observado en 1.3.16. La comparación entre inicios de vuelta incluía las consultas WMI, su acceso serializado, el registro de eventos y la publicación de archivos. Una vuelta de más de tres segundos no acreditaba suspensión.

`WmiFanGuardianTiming` mide la espera desde el final del trabajo anterior. Conserva tres segundos de margen para una pausa sin supervisión y distingue `SUPERVISOR_IDLE_GAP` de `SYSTEM_SLEEP_GAP`. El segundo usa una cota inferior del aumento del tiempo de suspensión desde el inicio: se lee el reloj de interrupciones entre dos lecturas del tiempo activo. Una interrupción del hilo entre lecturas amplía el intervalo de incertidumbre y no inventa suspensión.

Contratos de Microsoft: [QueryInterruptTime](https://learn.microsoft.com/en-us/windows/win32/api/realtimeapiset/nf-realtimeapiset-queryinterrupttime) y [QueryUnbiasedInterruptTime](https://learn.microsoft.com/en-us/windows/win32/api/realtimeapiset/nf-realtimeapiset-queryunbiasedinterrupttime). Se usa el contrato de API de tiempo real ya empleado por la recuperación de arranque, sin cambiar la resolución de temporizadores ni impedir suspensión.

La comprobación de suspensión se repite después de trabajo bloqueante y antes de publicar una observación cTGP correcta; también forma parte de la admisión del setter de activación. La restauración conserva su vía independiente. No se amplían la vigencia de cTGP, el heartbeat ni los límites nativos, ni se permite reactivación desatendida tras una pérdida real. Si un trabajo excede la vigencia del estado, la aplicación conserva su rechazo por datos caducados.

El estado `Timing` del informe guarda `WorkMs`, `IdleMs`, `SleepLowerBoundMs` y `StopReason`. Los trabajos superiores a un segundo se escriben en la salida del supervisor y quedan en el registro de la aplicación. Un registro de comienzo de llamada no demuestra por sí mismo su duración nativa.

La observación periódica usa un vencimiento monotónico de dos segundos desde su finalización, sin ráfagas de consultas atrasadas. Así, una vuelta lenta no pierde la siguiente observación por saltarse una ventana de módulo temporal.

La regresión `--guardian-timing-self-test` ejecuta el mismo supervisor en modo fixture, con una observación de 3,4 segundos, heartbeat concurrente, cTGP simulado y liberación final confirmada. Comprueba también que el lector de la GUI conserve su confirmación de cinco segundos durante el trabajo. La siguiente vuelta debe permanecer activa, que es donde fallaba la versión anterior. Pruebas deterministas adicionales cubren suspensión dentro de trabajo, suspensión durante espera, pausa despierta, límite exacto de tres segundos, muestreo interrumpido y reloj regresivo. Se ejecuta también desde `--ctgp-self-test` y desde la validación del paquete.

No es una prueba física de continuidad, RPM, suspensión ni apagado. No se han emitido comandos de hardware durante estos fixtures.
