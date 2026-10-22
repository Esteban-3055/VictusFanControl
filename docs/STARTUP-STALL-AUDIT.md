# Auditoría de bloqueos durante el inicio — 2026-10-20

Alcance: código de inicio de la aplicación, registro en el Programador de tareas,
recuperación, construcción/cierre del runtime, admisión de sensores, propietario
único y reanudación. Continúa la corrección descrita en
[SESSION-END-AND-GPU-RECOVERY.md](SESSION-END-AND-GPU-RECOVERY.md).
Los casos nuevos se reproducen con fixtures; no se atribuyen al incidente físico
del usuario sin evidencia de sus registros.

| Caso reproducido en el código | Resultado anterior | Corrección |
| --- | --- | --- |
| La consulta del registro de inicio devuelve acceso denegado, timeout o XML inválido | Entraba en el catch de inicio después de arrancar el runtime; lo bloqueaba y solicitaba liberar sus controles | Consulta opcional independiente. No participa en la tarea de inicio/cierre del runtime. Configuración permite repetir la consulta sin modificar la tarea cuando se desconoce su estado |
| Start falla y FenceLifecycle también falla | La segunda excepción impedía encolar la limpieza y ocultaba el fallo original | Se conserva el primer fallo, se intenta liberar igualmente y se mantiene visible el monitor. Una publicación tardía Healthy no elimina el bloqueo inicial |
| La limpieza inicial termina con excepción | Shutdown esperaba esa tarea fallida y no llegaba a DisposeAsync | Se espera a que termine la limpieza anterior, se registra su fallo y se ejecuta una sola disposición completa. Solo su confirmación o el traspaso GPU verificado permite continuar; otro fallo final conserva el bloqueo |
| Los registros desaparecen legítimamente durante la espera de recuperación | Se retornaba sin abrir runtime ni actualizar el arranque pendiente | Se entra en el mismo cierre/verificación final con selección vacía. No se lanza el recuperador de escritura. La ausencia se verifica otra vez antes de construir el siguiente runtime |
| La lectura inicial de recuperación falla antes de obtener identificadores válidos | El preview no accionable quedaba guardado indefinidamente | Hasta tres comprobaciones de solo lectura, con esperas de 10/30/60 segundos. Un resultado válido pasa después por selección exacta y exclusividad. Una ausencia pasa por verificación final. Un resultado inválido no autoriza hardware |
| No se puede crear la carpeta/identidad del log al entrar en Program.Main | AppLog.Initialize podía terminar el proceso antes de mostrar la ventana | Inicialización diagnóstica de mejor esfuerzo con aviso y espera de 30 segundos entre reintentos. No cambia los journals, leases ni requisitos de persistencia previos a escribir hardware |

La consulta opcional y el refresco del inventario permiten una sola operación en
curso. Una respuesta posterior al cierre no modifica la ventana. El refresco se
descarta si hubo suspensión, pantalla apagada, apertura de otro runtime o cambio
del preview mientras leía. No se mata un recuperador ni se interpreta una espera
agotada como liberación. El refresco inicial requiere la preferencia de
recuperación automática; no repite Reset sobre un inventario ya intentado.

## Otros casos inspeccionados

| Caso | Resultado de la inspección / límite |
| --- | --- |
| Otra instancia GUI sigue activa | El mutex de la GUI impide una segunda instancia. No se mata el proceso anterior ni se toma su autoridad |
| Faltan módulos, ejecutable auxiliar o permisos de administrador | El paquete/manifiesto y los controladores rechazan el arranque correspondiente. Se requiere reparar la instalación o los permisos; repetir sensores no resuelve esos fallos |
| Perfil persistido corrupto | ProductProfilesStore conserva el original y carga valores seguros. No habilita Automático implícitamente desde un perfil ilegible |
| Cambio AC/Battery mientras se cualifican sensores | La admisión reinicia la cualificación de fuente; tres adquisiciones distintas y frescas siguen siendo necesarias. No se reutilizan datos de la fuente anterior para autorizar el inicio |
| Suspensión/pantalla apagada durante construcción | Se conserva la frontera de energía y se bloquea el runtime que termina de construirse. Un cierre espera la construcción y dispone el objeto tardío sin arrancarlo |
| NVML de lectura no responde, devuelve basura o termina | El auxiliar aislado invalida la muestra, tiene plazo de comunicación y espera antes de reintentarlo. No hay sustitución con datos antiguos ni reapertura directa de NVML en la GUI |
| Llamada nativa de liberación sigue ejecutándose | Sigue siendo una espera real de propietario, no un fallo que se pueda descartar. Los recuperadores no se matan a mitad de una escritura; no se admite otro propietario mientras no termine |
| Registro dañado, de otro equipo, identificador cambiado o journal ausente tras liberación fallida | Se conserva el bloqueo y la evidencia. La lectura periódica no inventa identificadores ni elimina archivos para desbloquear |
| ESP32 desconectado o configuración externa inválida | Su inicialización sigue separada del control interno. No toma WMI/EC, no sustituye la telemetría de CPU/GPU por NTC y no autoriza el inicio interno |

## Validación

`ProductGuiSelfTest.StartupFailures.cs` cubre errores de consulta de tarea,
reintento sin escritura, consulta concurrente y respuesta tardía, fallo de Start
más fence/limpieza inicial, disposición final confirmada/no confirmada, carpeta
diagnóstica inaccesible y refresco inicial válido/ausente/inválido, desactivado,
con error de lectura, suspendido o cerrado. Usa archivos temporales, tareas
controladas y runtime ficticio. No cambia ACL reales ni provoca un apagado.

`ProductGuiSelfTest.AutomaticRecovery.cs` añade la resolución del inventario
mientras el arranque espera: cero invocaciones al recuperador cuando ya no hay
registros, pero verificación final y sensores nuevos antes de reanudar.

Resultado local: build Release con cero errores y advertencias; los 19 grupos de
`scripts/test-external-cooling.ps1`, todos PASS. Evidencia local de esta revisión:
`../output/startup-audit-build.log`, `../output/startup-audit-regression.log` y
`../output/startup-audit-validation.json`. El manifiesto guarda hashes del código
local ensayado además del commit de partida.

La instancia instalada y la tarea real de Windows no se modifican en esta
auditoría. No se publica una versión ni se da por comprobado el arranque físico
en batería. Sigue pendiente correlacionar un ciclo real de apagado/encendido y
la causa de las demoras NVML; estos cambios no prueban que el driver se recupera
de cualquier fallo.
