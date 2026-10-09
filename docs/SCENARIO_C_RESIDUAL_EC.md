# Escenario C: B + lecturas residuales del EC, sin control Manual

B `174956_0c08ef` (HEAD f4488fe, BIOS F.18/8C40) cubrio unos 14 min 41 s de CLI: 441 consultas HP RPM completas, cero fallos, mediana total 354.844 ms, maximo 591.555 ms, cero EC directo, 796/796 snapshots completos y cuatro ciclos AC/bateria verificados por EventData.AcOnline. Aislamiento valido, ExitCode 0 y ningun ACPI 13 nuevo. SHA externo y 422 entradas del manifiesto coinciden. Las consultas iniciadas dentro de +/-10 s de los cambios de energia tuvieron mediana nativa 364.840 ms (49 consultas), frente a 349.072 ms fuera (392). Es correlacion temporal, no causalidad.

El usuario desconecto la bateria y la BIOS volvio a defaults antes de A/B: USB charging y Fan Always On ahora ON, antes OFF. Mantener ambas ON durante C. Ausencia de eventos no prueba solucion definitiva ni permite atribuirla al software o a una opcion BIOS.

C mantiene la telemetria de B y agrega solo:

| Registro | Uso observado en VFC |
|---|---|
| 0x34 / 0x35 | Consignas y evidencia de ownership CPU/GPU |
| 0xEC / 0xF4 | MaxFan / FanSwitch, comprobaciones de control |

Se usa ReadStableFanSetpoint + ReadFanControlGuard del lector existente. La comprobacion de coherencia repite las consignas; una muestra normal necesita seis transacciones de byte, y puede necesitar mas ante incoherencia/reintentos. No usa tacometros 0xB0..0xB3 ni el snapshot diagnostico amplio. Espera inicial de 5 segundos despues de completar cada muestra, sujeta a la duracion del loop; no hay rafagas para recuperar muestras atrasadas. Leer EC tambien escribe el comando RD_EC=0x80 y la direccion en los puertos 0x66/0x62: esto participa en el protocolo EC, sin escribir valores de registros. No es una prueba libre de acceso EC.

La frontera C restringe los registros antes del handshake y permite solo comando RD_EC/direcciones calificadas en sus escrituras de protocolo. Solo admite HP WMI 20008h/2Dh de RPM. Rechaza combinaciones con otros probes, Manual, release o restore. Requiere el target exacto validado 8C40/F.18. El cambio afecta solo el nuevo modo de investigacion; B sigue bloqueando EC, y las rutas normales conservan handshake, reintentos y comprobaciones de seguridad.

## Ejecucion

Seleccionar Firmware, esperar restauracion y cerrar GUI/otros CLI. No borrar leases retenidos: bloquean el inicio. Cerrar OmenMon, HWiNFO y otros lectores EC, mantener el mismo plan energetico y uso ligero de B, sin cambiar BIOS ni ejecutar RAPL/pruebas de CPU simultaneas. Desde PowerShell como administrador en el repositorio:

```powershell
git switch feature/victus-8c40-wmi-broker-5sample
git pull --ff-only
Test-Path .\scripts\Start-Victus-ScenarioC.ps1
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\Start-Victus-ScenarioC.ps1 -CaptureMinutes 30
```

Si git switch falla por cambios locales, conservarlos; no usar reset/clean para descartarlos. Esperar `ESCENARIO C ACTIVO` antes de empezar. Usar AC/bateria de forma habitual, dejando tiempo entre cambios, sin ciclos rapidos. Q cierra y produce ZIP + SHA en el escritorio. El lanzador muestra etapas, informa actividad cada 30 segundos y conserva stdout/stderr. La GUI no se abre en este escenario.

M4 solo se pausa si estaba activo, limpio y con ruta verificada; se reanuda al finalizar. La preparacion minima y detector ACPI v2 son los de A/B. Un ACPI 13 nuevo termina la observacion y preserva evidencia. El primer fallo de una muestra EC, despues de los reintentos existentes, termina el CLI: no se reabre la sesion ni se insiste con nuevos lotes. El resultado parcial tambien debe adjuntarse. Una llamada nativa al driver/WMI no garantiza cancelacion; si no cierra, solo se termina nuestro CLI de lectura y la captura queda incompleta. No forzar apagado/cerrar la ventana durante el empaquetado.

## Evidencia e interpretacion

Revisar `scenario-c/isolation-summary.json`, `chronology.log`, `telemetry.csv`, `ec-control.csv`, `isolation-timeline.ndjson` y `analysis/summary.json`, `ec-reads.csv`, `wmi-calls.csv`. ec-control.csv conserva bytes hex, tiempo del lote y fallo; no afirma que los cuatro bytes sean atomicos ni que sus valores hayan cambiado fisicamente. Chronology registra cada intento, progreso del handshake, mutex, intentos repetidos, registro/valor y tiempo. El analisis offline se intenta incluso cuando la validacion del log o el CLI falla; la evidencia original se conserva.

La validacion exige identidad del proceso, llamadas WMI calificadas, los cuatro registros EC con cierre, al menos un lote completo, ausencia de operaciones denegadas/truncadas y cierre normal. ValidIsolation comprueba el alcance de la captura, no salud permanente del firmware. Windows/firmware y herramientas externas no quedan excluidos por el mutex de VFC. EC y WMI pueden coincidir, como en la aplicacion; C agrega lecturas/tiempo de loop, no un watchdog ni escrituras.

Si falla solo C, se fortalece la hipotesis de que el acceso EC residual contribuye bajo estas condiciones; aun no identifica un registro culpable ni demuestra una carrera con Windows. El siguiente aislamiento seria consignas frente a guards, no aumentar automaticamente la frecuencia. Si C no falla, conserva el resultado: no valida Manual ni justifica reactivar escrituras sin otra prueba.

Observaciones separadas de B: 27 muestras GPU exactamente 79 C alternadas con 48-49 C al inicio requieren revisar NVML, sin usarlas como prueba de calentamiento real o causa ACPI. El WMI 5858/0x80041032 corresponde a consulta de servicios PowerShell, no a HP RPM. No se modifica NVML durante C para mantener comparable el experimento.

Fixtures sin hardware en CI Windows: launcher PS5.1/7 (procesos nativos y evidencia adulterada), `--residual-ec-investigation-self-test` (todas las direcciones/comandos, target y HP writes, agenda sin catch-up y fallo retenido). Estos checks no sustituyen la prueba fisica en el Victus.
