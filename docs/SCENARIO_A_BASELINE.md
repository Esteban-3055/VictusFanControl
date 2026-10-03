# Escenario A: observacion sin VictusFanControl

Primero seleccionar Firmware en la GUI, esperar la restauracion y cerrar la app. Cerrar OmenMon y otros controladores de ventiladores. Mantener constantes las aplicaciones HP, plan de energia y configuracion entre sesiones; registrar cualquier cambio. Un journal ausente no prueba por si solo ownership del firmware: el script no ejecuta restore ni lecturas de comprobacion hardware.

En PowerShell como administrador, desde el repositorio:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\Start-Victus-ScenarioA.ps1 -CaptureMinutes 30
```

No necesita compilar ni instalar .NET/PawnIO. Rechaza procesos VFC, servicios activos distintos de M4 y cualquier lease retenido. Solo pausa M4 si no existe lease y se verifica su ruta de estado estandar y ejecutable; lo reanuda al terminar si estaba activo. No cambia configuracion de servicios, energia ni ventiladores. No finaliza procesos VFC a la fuerza ni borra journals.

Esperar `ESCENARIO A ACTIVO`. Primera sesion: uso ligero habitual. Si termina sin sintomas, hacer otra sesion independiente con carga moderada conocida. Incluir cambios habituales de cargador, sin ciclos rapidos repetidos ni bateria casi agotada. No ejecutar pruebas CPU/RAPL, OmenMon ni abrir VFC durante la ventana.

Q termina antes de tiempo y empaqueta. Un ACPI 13 nuevo termina automaticamente; si aparece bateria anomala, hibernacion inesperada u otro sintoma, pulsar Q si el equipo responde. No cerrar la consola a la fuerza. Tras hibernacion el script puede continuar al volver: esa captura se considera interrumpida y debe identificarse al adjuntarla.

ZIP y SHA-256 se guardan en el escritorio por defecto. Adjuntar ambos y describir actividad, cargador y hora del sintoma. `scenario-a/summary.json` distingue ACPI nuevo, invalidacion y cierre; `isolation-timeline.ndjson` registra procesos, servicios y journals cada ciclo. La ausencia muestreada no descarta procesos que arrancaron y terminaron entre muestras. Tampoco excluye EC usado por Windows, firmware u otras aplicaciones.

El recolector conserva eventos System/Application/WMI, EVTX, ETW circular propia, procesos, servicios, drivers y logs previos. Para disminuir interferencia de preparacion, A omite enumeracion PnP y extraccion de tablas firmware. No ejecuta consultas HP WMI ni EC/MSR/NVML. Un reporte ACPI 13 sin VFC apoya una causa independiente de su ejecucion actual, pero no demuestra por si solo una averia fisica ni excluye estado de firmware heredado de una sesion anterior. Sin ACPI 13 solo significa no reproducido durante la ventana.

Fixtures sin hardware:

```powershell
.\scripts\Start-Victus-ScenarioA.ps1 -SelfTest
```

## Correccion del detector v2

La captura `170707_4061c7` del 03-10-2026 se detuvo tras una sola muestra: el detector v1 acepto registros historicos 4536/4540/4544/4550 como nuevos. System antes y despues conserva el mismo ultimo RecordId 4826; no hubo reproduccion ni observacion de 30 minutos. Conservar el ZIP original sin editarlo.

El detector v2 consulta XPath con RecordId superior al cursor inicial y verifica independientemente proveedor, ID, RecordId y timestamp UTC del XML de cada evento. Invalida la sesion si detecta retroceso del cursor System. El resumen registra `DetectorVersion=2`, cursor y tiempos ISO UTC. Los fixtures de PS5.1/7 incluyen los cuatro eventos historicos reales, un evento nuevo, combinaciones de tiempo/cursor incorrectas, otros proveedor/ID, timestamp invalido y validacion read-only del motor XPath Windows.
