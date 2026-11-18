# Prioridad Alta y publicación de sensores sin inventario síncrono

## Evidencia y alcance

El 21 de octubre de 2026, durante una instalación/descompresión, Automático se interrumpió a las 21:16:30.949 de Santiago por el watchdog de telemetría: 5375 ms sin completar una lectura. La fase observada inicialmente era `HardwareRead / IntelCoreTemperatures`. Esto no demuestra que WMI originara la demora ni que hubiera un corte por potencia CPU.

Una captura posterior de la pila administrada encontró al trabajador de recuperación en `ProductRuntime.Publish → PerformanceRecoveryPreview.Read → ProductRecoveryInventory.ReadFans → EnsurePlainPath → FileSystemInfo.Attributes`. La publicación de sensores estaba haciendo un recorrido síncrono del sistema de archivos. Esta captura confirma un bloqueo adicional en publicación; no identifica retrospectivamente la causa exacta de la primera demora.

## Cambios

- La aplicación de producto solicita y verifica `ProcessPriorityClass.High` al arrancar. Se aplica en el camino común de apertura manual, inicio minimizado con Windows y proceso nuevo tras reinicio de sesión. Windows muestra esta clase como **Alta**. No depende de heredar la prioridad del lanzador.
- Los Guardian de producción (`--gui-session`, `--fan-wmi-session`, `--recover-gui-session`, `--recover-product-fans`) también configuran su propia prioridad Alta. Las rutas de fixtures y sondas no la adquieren automáticamente. El resultado efectivo o el fallo de la solicitud queda registrado; una denegación no impide cerrar o liberar dominios.
- `ProductRuntime.Publish` consulta únicamente una instantánea en memoria. El inventario completo sigue intacto y se ejecuta con un máximo de un lector dedicado en segundo plano por runtime. El reloj de frescura se toma al terminar la lectura, no al consumir el resultado.
- La vista tiene vigencia de 5 s y solicita renovación a partir de 2,5 s. Durante una renovación normal conserva la vista todavía fresca, sin romper los plazos de observación de la recuperación automática. Un resultado inicial o vencido queda como comprobación pendiente, sin identificadores accionables y con admisión de rendimiento cerrada.
- El inicio automático espera una comprobación en curso sin cancelar su solicitud. Tras la comprobación requiere de nuevo las tres adquisiciones frescas habituales. Una sesión pendiente real conserva su bloqueo y su recuperación con identidad exacta.
- El cierre drena el lector y descarta sus resultados tardíos antes de habilitar el traspaso del runtime. No hay callbacks de publicación desde el lector a una sesión posterior.
- Los mensajes de reintento distinguen `unattended=true` de un reintento manual, evitando atribuir al usuario recuperaciones iniciadas por el coordinador.

La vista en memoria no autoriza escrituras: `PerformanceGuardianClient.EnableAsync` mantiene la lectura directa de los registros antes de lanzar un propietario. Los asistentes de recuperación vuelven a validar su inventario exacto. Se conservan journals, marcadores, exclusión de propietarios, frescura de sensores y comprobación del cierre. No se agregan accesos a EC, TZ01 o DTT3.

## Validación reproducible

Compilar `VictusFanControl.sln` en Release con advertencias como errores y ejecutar `scripts/test-external-cooling.ps1`. `--product-recovery-self-test` permite ejecutar también el grupo enfocado de recuperación.

`ProductSchedulingSelfTest` se incluye en las pruebas GUI y de recuperación:

- Solicita Alta en su propio proceso, confirma la lectura efectiva y restaura la prioridad original; comprueba denegación y resultado efectivo distinto.
- Mantiene una lectura simulada bloqueada y completa 1000 consultas de publicación en menos de un segundo, con exactamente un lector y sin comandos de hardware.
- Rechaza inventario inicial, vencido o ilegible; conserva las identidades CPU/GPU exactas y comprueba el drenaje al cerrar y el descarte de un resultado tardío.
- Aunque la presentación conserve una vista vacía, un inventario nuevo pendiente impide `EnableAsync` antes de iniciar un proceso Guardian.
- Simula renovaciones periódicas y verifica que el coordinador alcance su espera de 10 s sin adelantar ni posponer indefinidamente la recuperación.
- La prueba de inicio automático comprueba espera durante la lectura, continuación con tres observaciones nuevas y bloqueo ante registros realmente pendientes.

La validación local usa controladores simulados y procesos de prueba. No constituye un nuevo PASS físico, no cambia la instancia instalada ni actualiza el ESP32. La ejecución local con `OfflinePackages` omite la auditoría de paquetes en red; CI conserva su configuración habitual.

Resultado local del 21 de octubre de 2026: compilación Release con cero errores/advertencias y **19/19 grupos PASS**, incluyendo las nuevas pruebas de planificación y continuidad de inicio automático. Evidencia local: `logs/external-cooling-self-test/validation.json` y logs del mismo directorio. El campo `sourceHead` de esa ejecución identifica la base `bfe3151`: se probaron los cambios de este documento en el árbol de trabajo antes de su commit, no únicamente esa base sin modificaciones.

## Límites prácticos

Alta favorece la planificación de CPU bajo carga, pero no garantiza plazos ni hace retornar una llamada de driver o del sistema de archivos bloqueada. El cambio elimina el inventario síncrono del camino de publicación sin ampliar los límites de antigüedad ni dar por liberada una operación incierta. El cierre conserva la espera por las operaciones pendientes; no mata propietarios ni borra pruebas para aparentar recuperación.

Estos cambios son posteriores a la publicación 1.3.1 y requieren una actualización del programa para llegar a la instalación del usuario. No requieren cambiar el firmware ESP32.
