# Escenario B: telemetria con HP WMI y sin EC directo

A `171731_487a45` cubrio 18 min 03 s (17:17:46-17:35:49 Chile), 509 muestras, aislamiento muestreado valido, ninguna advertencia y cero nuevos ACPI 13. SHA externo coincide. System contiene cuatro cambios de energia durante la ventana. No constituye endurance ni prueba de causalidad.

B agrega la telemetria existente: RPM HP WMI 20008h/2Dh, CPU Intel MSR/PawnIO, carga Windows y GPU NVML. Por tanto A/B no aisla exclusivamente la consulta de RPM: tambien agrega el proceso de telemetria y sus otros backends. No se ejecuta Manual. Las escrituras HP y el acceso directo EC estan rechazados antes de I/O por la frontera existente. M4 se mantiene detenido durante la observacion.

Seleccionar Firmware, esperar restauracion y cerrar GUI/otros CLI. Mantener cerrados OmenMon, HWiNFO y otras herramientas que accedan al EC. En PowerShell como administrador desde el repositorio:

```powershell
git pull --ff-only
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\Start-Victus-ScenarioB.ps1 -CaptureMinutes 30
```

Compila Release y verifica que no haya lease, procesos ajenos VFC ni servicios incompatibles. Pausa solo M4 previamente activo, sin lease y con ruta de estado verificada; lo reanuda al finalizar. Requiere los backends ya instalados para telemetria. Esperar `ESCENARIO B / WMI-ONLY ACTIVO` antes de comenzar. Repetir el uso ligero y cambios habituales de alimentacion de A, sin ciclos rapidos ni pruebas CPU/RAPL adicionales. Q finaliza y empaqueta.

Se conservan ETW, eventos/EVTX, procesos y journals igual que A. La preparacion minima omite PnP y extraccion de tablas ACPI. El detector compartido v2 usa cursor System y tiempo UTC XML: un ACPI 13 nuevo finaliza la observacion y conserva la cronologia; el CLI se cierra mediante stop.signal. Errores de aislamiento y lectura del registro invalidan la captura. Un bloqueo WMI con falta de cierre puede producir captura incompleta, que tambien debe adjuntarse.

El lanzador posee el objeto Process real y mantiene su handle, copia stdout/stderr asincronamente a archivos y confirma ExitCode antes de empaquetar. No convierte un codigo desconocido en cero. Fixtures nativos en PS5.1/7 prueban salida 0, salida 37, streams y cierre idempotente sin hardware.

Adjuntar ZIP + SHA del escritorio; indicar actividad y alimentacion. Revisar `wmi-only/isolation-summary.json` (Scenario B, ExitCodeCaptured, StopReason, Acpi13), `chronology.log`, `telemetry.csv`, `isolation-timeline.ndjson` y `analysis/summary.json`. El analisis separa latencia WMI nativa/total y confirma ausencia de operaciones EC. Ausencia de ACPI 13 en B no valida Manual ni excluye un fallo intermitente.

Fixtures sin acceso hardware:

```powershell
.\scripts\Start-Victus-ScenarioB.ps1 -SelfTest
```
