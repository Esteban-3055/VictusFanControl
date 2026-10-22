# Preparación y validación 1.3.0

La investigación previa evaluó 31 áreas y separó defectos reproducidos, casos condicionados y riesgos de componentes. Este documento registra la fase de corrección. Los diagnósticos privados del usuario permanecen fuera de los artefactos públicos.

| Caso | Cambio | Evidencia de regresión |
| --- | --- | --- |
| AUD-01 | Cada coordinador termina antes de disponer; su fallo completado no omite la disposición final. | GUI: cuatro tareas fallidas, tarea viva que debe terminar, una sola disposición. |
| AUD-02 | El fallo externo se registra y clasifica; la señal final de cierre se completa. | GUI: inicialización externa fallida y cierre repetido. |
| AUD-03 | Una tarea de inicio fallida no impide disponer el objeto conocido. Si no hay propietario verificable, sigue fallando. | GUI: inicio fallido, liberación previa fallida, disposición final correcta o incierta. |
| AUD-04 | Una excepción de callbacks de cancelación no escapa antes de la limpieza. | GUI: callback que lanza y disposición única. |
| AUD-05 | Un error de ZIP intenta el cierre normal, mantiene el bloqueo y no llama al recuperador que escribe. | Error real de E/S: padre del ZIP es un archivo; contenido original intacto. |
| AUD-06 | La exclusión GUI/Guardian se determina adquiriendo el mutex, no por si el nombre ya existía. Las comprobaciones de journals siguen vigentes. | Reproducción previa de mutex existente sin dueño; regresiones IPC y exclusión. |
| AUD-07 | Se prepara el controlador sin activar la frontera WMI. Se consume el relevo solo al confirmar la construcción; un fallo anterior puede reintentar únicamente si la frontera sigue siendo la anterior exacta. | Transacción real de commit con callbacks de prueba: fallo previo, cambio incierto, commit y segundo uso. No se invoca el constructor físico completo. |
| AUD-08 | Un archivo exclusivo serializa la instalación; Directory.Move evita anidar una preparación dentro de un destino existente. Se verifica cada archivo antes de ejecutar. | Reproducción de carrera anterior; fixture de publicación de directorios y exclusión de instalación. |
| AUD-09 | Recibo duradero de relevo, ligado a nonce/equipo/padre/hijo/horas/sesión, más salida real del proceso. No representa Reset confirmado. | IPC real con host de prueba que pierde el ACK; once recibos alterados rechazados; salida 23 sin recibo y errores desconocidos siguen bloqueados. |
| AUD-10 | Carga y parser limitados a 1 MiB; lectura con exclusión de escritores. | Archivo válido sobredimensionado: valores seguros, sin Automático, original intacto. |

La recuperación GPU con error devuelto conserva `ReleaseWriteArmed`. El supervisor solo entrega su relevo si la fuente ya se detuvo y CPU quedó deshabilitada. La interfaz debe haber terminado también la limpieza local. Solo el recuperador exclusivo, después del respaldo y la validación del resultado exacto, puede cerrar la recuperación y permitir otro controlador. Un timeout, una excepción nativa sin resultado, un journal cambiado o desaparecido y una CPU aún pendiente no se convierten en éxito.

Otros riesgos tratados: caché de actualizaciones fuera de la coordinación de control, espera de lectura de diez segundos y descarte tardío; contrato completo de tarea programada; reinscripción idempotente de energía antes de iniciar reemplazos; rollback atómico de perfiles; identidad de lectores NVML retenida si no se confirma su terminación.

## Validación de la entrega

La entrega debe pasar build Release con advertencias como errores, los 19 grupos Windows, los fixtures adicionales NVML/Guardian/CPU/instalador y la comprobación del actualizador original 1.2.9. CI exige seis workflows exitosos del mismo commit: build, external-cooling, oem-shadow, cpu-rapl, wmi-fan-experiment y esp32-firmware. El publicador valida el ZIP original, instalador, hashes, versiones y procedencia antes de publicar y comprueba después la descarga con el actualizador original.

El firmware no cambia respecto de 1.2.17. Se recompila Arduino-ESP32 3.3.2 + ArduinoJson 6.21.5 y PlatformIO; se conservan las pruebas host de layout 16×2, tiempos 30/5/5/5, frescura, nulos e I2C/NTC. El ZIP Arduino se exporta con PWM habilitado y sin `config.local.h`.

Los resultados de CI, la validación de 19 grupos, los hashes y el resultado del actualizador se adjuntan con el commit exacto. Una prueba sintética no confirma el comportamiento físico de suspensión, arranque con batería, transiciones AC/batería, apagado, drivers, PWM o LCD. No se reinicia Windows, no se matan propietarios mutadores y no se modifica una instalación con liberación incierta para conseguir un PASS. Una llamada Intel/PawnIO/NVML sin retorno sigue siendo una limitación: no existe una prueba de que terminar otra tarea pueda completar esa llamada.
