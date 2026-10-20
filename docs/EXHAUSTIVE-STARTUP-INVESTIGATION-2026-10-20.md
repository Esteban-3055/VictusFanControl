# Investigación de inicio, cierre, actualización y recuperación

Este es el informe de la fase de investigación anterior a las correcciones. La implementación posterior y sus pruebas se registran en [validación 1.3.0](RELEASE-1.3.0-VALIDATION.md).

Fecha: 20 de octubre de 2026. Fase: **investigación y reproducción; sin nuevas
correcciones de producción**.

Se examinó `feature/external-curve-firmware`, commit de partida
`1d08ad29b1e77f2ee183d14c430570b4d83164ab`, junto con las correcciones locales
anteriores todavía sin publicar. Se conservaron sus 28 archivos de entrada y su
diff. La investigación no alteró esos archivos, la instalación, las tareas de
Windows, los límites de hardware, los journals ni las credenciales.

La revisión cubre las rutas alcanzables del ciclo de vida de la aplicación y sus
dependencias. No constituye una prueba de ausencia de cualquier fallo posible
del driver, Windows, hardware o código fuera de ese alcance. Los límites de cada
reproducción se indican a continuación; no se inducen averías físicas.

## Resultado principal

Las suites existentes pasan, pero las reproducciones nuevas descubren rutas de
error que esa cobertura no incluía:

- Seis fallos de coordinación reproducidos mediante excepciones controladas,
  un fallo real de IO en una carpeta sintética o pérdida de respuesta IPC.
- Dos escenarios condicionales reproducidos con primitivas de Windows y
  publicación de directorios sintéticos.
- Dos riesgos comprobados a nivel de componente/entrada que requieren más
  validación antes de atribuirles un bloqueo completo del producto.

No todos ocurrieron en el Victus. Ninguno de estos resultados demuestra la causa
del error NVML original. Una reproducción terminada con código cero significa
que **se observó el defecto esperado**, no que esté corregido.

## Hallazgos reproducidos

P1 significa resolver antes de promover otra versión con estas modificaciones.
P2 significa disponibilidad o robustez bajo una condición adicional. No se
observó una escritura real de hardware no autorizada.

| ID | Prioridad y evidencia | Disparador y resultado | Fuente actual |
| --- | --- | --- | --- |
| AUD-01 | P1; excepción controlada, cuatro variantes | Una tarea previa de reintento, reinicio, recuperación o actualización termina con excepción. El cierre deja de recorrer su único bloque `try` antes de `ShutdownRuntimeAsync`. Se reproduce **Dispose=0**, pero el formulario marca runtime dispuesto y señal de cierre completada | `ProductForm.cs:507` |
| AUD-02 | P1; excepción controlada | Falla la tarea de inicialización externa. El runtime interno se dispone, pero `DisposeExternalCoolingAsync` falla en el `finally`: la señal de cierre queda pendiente y `_exitTask` conserva esa excepción. Otra petición recibe la misma tarea fallida | `ProductForm.cs:509`, `ProductForm.ExternalCooling.cs` |
| AUD-03 | P1; excepción controlada | La tarea `_startup` falla cuando ya existe un runtime. `ShutdownRuntimeCoreAsync` vuelve a esperar esa tarea y no alcanza la disposición del objeto conocido. Se reproduce **Dispose=0** | `ProductForm.Restart.cs:69` |
| AUD-04 | P1; callback de cancelación controlado | Un callback registrado en la cancelación lanza una excepción. `Cancel()` se ejecuta antes del bloque protegido del cierre; no se llega al fence ni a la limpieza. Se reproduce tarea de salida fallida, **Dispose=0** y señal pendiente | `ProductForm.cs:505` |
| AUD-05 | P1; IO real sobre archivos sintéticos | El destino del ZIP diagnóstico no se puede crear. Recuperación definitiva termina antes de bloquear/disponer el runtime. El intento automático se consume sin llamar al recuperador ni realizar limpieza normal. Un nuevo tick no repite el intento | `ProductForm.ForcedRecovery.cs:45`, `ProductAutomaticRecovery.cs` |
| AUD-09 | P1 de disponibilidad; IPC real entre procesos ficticios | Se pierde la respuesta de SHUTDOWN y el hijo sale realmente con código 23. El cliente conserva el handle y no obtiene prueba de traspaso. Los siguientes Close siguen rechazados. El bloqueo es conservador y no confirma falsamente Reset; falta una ruta segura de continuidad | `PerformanceGuardianClient.cs:189`, `PerformanceGuardianHost.cs:928` |

AUD-01 a AUD-04 prueban invariantes de limpieza ante tareas/callbacks fallidos.
No demuestran que las excepciones inyectadas sean frecuentes ni que cada una
tenga hoy un disparador ordinario en todos los caminos de la UI. Se deben
resolver porque el cierre ya admite dependencias que pueden fallar y debe
preservar su autoridad y evidencia al hacerlo.

En AUD-02 los errores habituales de configuración/emparejamiento externo ya se
capturan; la prueba introduce una excepción que escapa de la inicialización.
En AUD-09 el servidor de pruebas reproduce la pérdida de respuesta sin abrir
NVML ni crear journals: prueba el cliente y la frontera IPC, no un Reset físico.
El host real contempla IO fallida al responder, por eso el estado es relevante.

### Escenarios condicionales y comprobaciones parciales

| ID | Clasificación | Resultado y límite | Fuente |
| --- | --- | --- | --- |
| AUD-06 | P2; reproducción condicional con mutex real | Un observador conserva un handle del mutex nombrado sin tenerlo adquirido. `createdNew=false`, aunque `WaitOne(0)` puede adquirirlo. El predicado actual lo trataría como otra instancia activa. No se ha demostrado que un observador así existiera en el equipo | `Program.cs:160`; predicado similar en `PerformanceGuardianHost.cs:176` |
| AUD-08 | P2; intercalado de publicación reproducido | Dos instaladores CLI observan destino ausente. El segundo `Move-Item` mueve su staging dentro del destino ya publicado. Aparecen archivos adicionales; la verificación de manifiesto y los reintentos posteriores rechazan ese destino. No se ejecutó el instalador real. El filtro de procesos puede impedir la concurrencia habitual de los EXE empaquetados; el intercalado sigue siendo una condición para las entradas CLI | `release/Install-VictusFanControl.ps1:51` |
| AUD-07 | P2; componente reproducido y orden del constructor revisado | La reclamación de la frontera liberada se consume antes de terminar el constructor sustituto. Se probó que la segunda reclamación se rechaza. Si el constructor falla después de consumirla, el reintento en el mismo proceso puede quedar bloqueado. No se ejecutó el constructor físico ni se demostró esa secuencia completa con hardware | `ProductRuntime.cs:157`, `ClaimReleasedBoundary` |
| AUD-10 | P2; límite de entrada comprobado | El arranque acepta un perfil JSON válido de más de 2 MiB, mientras la importación UI rechaza más de 1 MiB. No hay límite previo a `ReadAllText` en Load. Se comprobó aceptación y conservación del original; no se provocó falta de memoria ni se afirma que ese tamaño por sí solo bloquee el inicio | `ProductProfiles.cs:219`; importación en `ProductForm.cs` |

Controles de comparación: una tarea previa viva se espera y luego se dispone el
runtime exactamente una vez. El reinicio normal intenta disponer el runtime
después del mismo fallo de diagnóstico que detiene la recuperación definitiva
(AUD-05). Esto delimita el defecto a la ruta correspondiente.

## Riesgos pendientes de reproducción completa

| Riesgo | Evidencia estática | Trabajo necesario |
| --- | --- | --- |
| Registro de notificaciones de energía fallido | Initialize intenta registrar antes de Start. La apertura de un reemplazo en recuperación no vuelve a registrar ni comprueba explícitamente esos handles | Inyectar fallo de las API de registro y validar la secuencia completa. Mantener el inicio bloqueado hasta tener supervisión válida; no afirmar que ya se produjo control sin notificaciones |
| Restauración opcional del caché retrasa recuperación | RestoreDownloadedUpdateAsync usa `_checkingUpdate/UpdateBusy`; TryAutomaticRecovery se pausa mientras estén activos. FindCachedAsync verifica archivos sin un plazo global propio | Medir caché grande y simular IO de archivo que no retorna. Separar la consulta opcional de una instalación con efecto de control, conservando la exclusión de esta última |
| Rollback de preferencias no atómico | La entrada `--configure-product-install` recupera el original mediante File.WriteAllBytes cuando falla registrar la tarea | Probar cortes entre fases con perfil y scheduler ficticios. Respaldo duradero, reemplazo atómico y recibo de fase. No cortar energía del equipo real |
| Lecturas nativas o liberaciones que no retornan | Intel/PawnIO sigue teniendo llamadas dentro del proceso; las liberaciones y los clientes de recuperación esperan sus propietarios sin matarlos | Instrumentar duración/propietario y probar fronteras aisladas. Un timeout no equivale a fin de una escritura nativa; no admitir otro mutador ni borrar el journal |
| Terminación del auxiliar de lectura no confirmada | Stop invalida su referencia antes de Kill/espera y puede devolver sin prueba de salida. El lector reintenta después del backoff | Inyectar fallo de terminación y comprobar límite de auxiliares; la prueba NVML existente sí cubre terminación normal/fatal/backoff, no un driver realmente irrecuperable |
| Estado de tarea de inicio incompleto | IsEnabledAsync comprueba Enabled y ejecutable, pero no todo el contrato de argumentos, triggers y condiciones de batería | Fixture de XML de tarea alterado. Distinguir tarea habilitada de configuración íntegra; preservar la decisión de desactivar el inicio |

## Cobertura recorrida

La tabla identifica qué se comprobó con ejecución, lectura de código o solamente
como límite pendiente. Los fixtures existentes también contienen subcasos que
no se cuentan como incidentes físicos separados.

| Área / escenarios | Evidencia / resultado |
| --- | --- |
| Binarios/módulos ausentes; runtime .NET; cuenta/elevación | Guardas de Program, manifiesto e instalador revisadas; fixtures de paquete y propietario PASS. Sin instalación de dependencias reales |
| Perfiles válidos/corruptos/versiones anteriores; conservación del original | Fixtures existentes PASS; AUD-10 añade tamaño excesivo. Interrupción de rollback pendiente |
| Logs inaccesibles y exportación diagnóstica | Fixtures previos PASS; AUD-05 revela la dependencia que aún evita la limpieza |
| Tarea Windows y arranque con batería | Lectura real: tarea Running, permite iniciar con batería y no detenerse al pasar a batería. XML generado y escapes PASS. No se cambió la tarea |
| Error/timeout/XML inválido en consulta opcional de tarea; consulta repetida | Fixtures de las correcciones locales PASS; no libera el controlador por fallar una consulta opcional |
| Duplicado real de GUI/guardián; nombre existente sin propietario | Guardas de exclusión revisadas; AUD-06 reproduce el falso duplicado condicional |
| Exclusión de CPU/GPU entre sesiones Windows | El guardián de producción usa **Global**, también en recuperación. Se descartó la hipótesis de un mutex CPU/GPU limitado a la sesión. No se hizo cambio real de usuario |
| Constructor tardío tras salida o suspensión | Fixtures PASS; objeto tardío no se arranca y se dispone. Constructor que nunca retorna sigue pendiente de validación física |
| Start/fence/limpieza inicial fallidos; publicación Healthy tardía | Fixtures de las correcciones locales PASS; AUD-03 amplía el caso de tarea de inicio fallida |
| Cancelación de salida y errores de otras tareas | AUD-01 y AUD-04 reproducidos; CTRL-01 confirma el drenaje normal |
| Fin de sesión Windows confirmado/cancelado; espera acotada y STA | Fixtures anteriores PASS. No se solicitó apagar ni cerrar sesión de Windows |
| Recuperación con inventario exacto, cambiado, ausente, extranjero o malformado | Fixtures de inventario/GUI/clientes PASS; no se inventan identificadores ni se limpia evidencia para desbloquear |
| Inventario inicialmente ilegible; 10/30/60 s; respuesta tardía tras suspensión/cierre | Fixtures de las correcciones locales PASS; solo lectura antes de cualquier recuperador |
| CPU: campos aún propios, cambio externo, rollback y journal | CpuProbe/Guardian/recuperación con backend ficticio PASS |
| GPU: Set/Reset armado, error retornado 999, ausencia de resultado, excepción y fallo de journal | Fixtures PASS; sin Reset físico ni inferencia de liberación por nvidia-smi |
| Respuesta correcta de traspaso y salida del propietario | IPC existente PASS; AUD-09 añade pérdida de respuesta y demuestra disponibilidad pendiente |
| Recibo/informe incompleto o identidad/protocolo incorrectos | Fixtures de protocolo y recuperación PASS; mantienen el bloqueo |
| NVML auxiliar: EOF/fatal, silencio, replay, versión/sesión/secuencia y dato antiguo/futuro/inválido | Suite de aislamiento PASS, procesos sintéticos |
| NVML auxiliar: stderr abundante, reintento, lecturas simultáneas, Dispose y muerte del padre | Suite de aislamiento PASS; no prueba un bloqueo físico del kernel/driver |
| Inicialización NVML fallida y supuesto «nunca vuelve a intentar» | Hipótesis descartada por recorrido: RecoverAndValidateAsync dispone y reconstruye el reader; también existe backoff del auxiliar. No se confundió EnsureReader con el ciclo completo |
| Datos retenidos, RPM antiguas, coherencia y temperaturas de entrada | Fixtures de admisión/continuidad PASS; datos retenidos no cuentan como tres muestras nuevas |
| Batería inicial, AC inicial, ambas transiciones, rebote, fuente desconocida y respuesta anterior al cambio | Fixtures de fábrica/controlador y guardián PASS; sin desconectar físicamente la alimentación |
| Reanudación, cancelación del usuario, preferencias desactivadas y presupuesto de episodios | Fixtures PASS; no se agregaron bucles de Reset |
| Descarga interrumpida, digest inválido, recibo incompleto, canal estable/preliminar y caché | Fixtures existentes PASS; caché bloqueado al arrancar continúa como riesgo de dependencia opcional |
| Instalación: verificación antes de cierre; UAC cancelado; reintento con descarga retenida | Fixtures GUI PASS; no se lanzó el instalador real |
| Instalación: recuperación previa, cambio de inventario, cierre o suspensión durante espera | Fixtures GUI/clientes PASS; no reabre hardware entre recuperar e instalar |
| Paquete: traversal, ADS, nombres de dispositivo, duplicados, versión, hash y archivos extra | 14 casos del instalador PASS, además de elección de preferencias |
| Publicación simultánea / interrupción de instalación | AUD-08 reproduce el intercalado de directorios. Fases completas de scheduler/shortcut y corte de energía siguen pendientes de fixture transaccional |
| Reinicio de la aplicación, borrador, estado de versión y propiedad | Fixtures PASS; AUD-07 deja delimitada la frontera consumida por constructor incompleto |
| ESP32: desconexión, autenticación, cancelación, reconexión y telemetría exportada | Grupos host/loopback existentes PASS. No se reflasheó ni se dio PASS físico al ESP32; no se cambió NVS, HMAC ni pines |
| Recursos acumulados por abrir/cerrar | Soak PASS: 30 ciclos medidos, 924 renderizados; GDI 19→19 y USER 15→16 |

La distinción Global/Local y el caso de nombre existente se contrastaron con
[Kernel object namespaces](https://learn.microsoft.com/en-us/windows/win32/termserv/kernel-object-namespaces)
y [Object Names](https://learn.microsoft.com/en-us/windows/win32/sync/object-names).
La GUI por sesión y la exclusión global de hardware cumplen propósitos distintos;
no se propone cambiar su alcance sin mantener la autoridad validada.

## Plan de corrección posterior

1. **Cerrar las excepciones en el coordinador de salida (AUD-01 a 04).** Esperar
   que terminen todas las tareas que conservan trabajo; recoger cada fallo sin
   saltarse los intentos posteriores de limpieza. Disponer un runtime conocido
   una sola vez. Separar recursos cerrados, hardware liberado y cierre de ventana.
   Un coordinador pendiente o un mutador nativo vivo no se abandona. Un fallo de
   la base externa no debe omitir la señalización final del resultado interno.
2. **Desacoplar diagnóstico y liberación normal (AUD-05).** Intentar el fence y
   cierre aunque falle exportar el ZIP, como ya hace Reiniciar sesión. Mantener
   todas las exigencias de journal/backup antes de cualquier escritura. Reintentar
   preparaciones de solo lectura con límites, sin repetir Reset no confirmado.
3. **Recuperación tras respuesta perdida (AUD-09).** Diseñar un recibo duradero
   de propietario/nonce/target/sesión y salida real verificadas, o un reinicio
   supervisado del proceso de aplicación cuando sea seguro. No aceptar código
   23 o ausencia del journal como prueba de Reset. No matar al mutador.
4. **Propiedad y sustitución (AUD-06/07 y notificaciones).** Distinguir nombre de
   mutex de propiedad real; conservar la exclusión global y los journals. Hacer
   explícita la frontera de construcción parcial. No volver a poner un contador
   en cero después de un constructor de resultado desconocido. Validar el registro
   de energía antes de autorizar un reemplazo.
5. **Instalación y persistencia (AUD-08/10 y rollback).** Serializar instalaciones,
   publicar de forma idempotente, comprobar identidad del destino existente y no
   mover staging dentro de él. Añadir límites de perfil antes de leerlo y rollback
   atómico con evidencia. Nunca limpiar un destino o journal desconocido para
   permitir continuar.
6. **Dependencias opcionales y bloqueos nativos.** Plazos y estado diferenciados
   para caché/consulta, telemetría y autoridad. Añadir las inyecciones todavía
   pendientes antes de atribuir o declarar resuelto un fallo físico de NVML/WMI.

Cada cambio deberá transformar su reproducción en una prueba que exija el
resultado correcto y conserve los controles negativos. Después: build Release,
19 grupos, IPC/propiedad, instalador y recursos. La promoción requiere además
validar actualización limpia y un ciclo físico controlado de salida/arranque en
batería/AC con rollback disponible. No se promete que un driver que no retorna
pueda liberarse simplemente reiniciando una tarea del programa.

## Evidencia, reproducibilidad y límites

Directorio local: `../output/exhaustive-startup-audit-20261020` desde el checkout.

- `input-snapshot.json`, `preexisting-changes.patch`, `production-unchanged.json`:
  versión y correcciones previas preservadas; ningún archivo de entrada cambió.
- `harness/AuditProgram.cs`, `harness/Prepare-Harness.ps1`, `README.md`: montaje
  reproducible que compila el código real con una entrada exclusivamente de prueba.
  Todos los runtimes adjuntos son ficticios; no se invoca el constructor de hardware.
- `independent-probes.json`, `installer-race.json`, `source-references.json`:
  observaciones, condiciones y líneas verificadas. Se conservaron controles y el
  primer fallo de montaje del harness sin contarlo como defecto del producto.
- `release-build.log`, `baseline-regression.log`, `baseline-validation.json`:
  build sin errores/advertencias y 19 grupos PASS.
- `nvml-isolation.log`, `guardian-self-test.log`, `cpu-fixtures.log`,
  `setup-fixtures.log`, `gui-soak.log`, `gui-soak-report.json`: pruebas adicionales
  PASS de aislamiento, propiedad, paquete y recursos.
- `coverage.csv`, `audit-manifest.json`: matriz de revisión y hashes de las
  evidencias/código evaluados.

La observación privada de la instancia real confirmó versión 1.2.17, tarea Running
con batería permitida y una última muestra registrada con temperaturas/RPM válidas.
El inventario inicial dentro del aislamiento no veía los procesos reales y se
repitió fuera de él. No se pudieron leer los comandos de dos guardianes elevados;
no se afirmó que fueran duplicados ni se verificó su rol o liberación de GPU.
No se adquirieron sensores nuevos ni se detuvo ningún proceso instalado.

No se apagó Windows, se cambiaron usuarios, se desconectó AC, se forzó un reset de
driver, se cortó energía ni se escribió en ventiladores/CPU/GPU/ESP32 desde estas
pruebas. Las evidencias de usuario se mantienen locales; el informe no necesita
publicar sus rutas, SID, PID, SSID, claves o contraseñas. La actualización,
publicación y las nuevas correcciones quedan para la fase posterior.
