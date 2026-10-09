# Prueba de aislamiento WMI sin EC directo (HP 8C40/F.18)

Objetivo: comprobar si las demoras HP WMI y ACPI 13 reaparecen cuando VFC no usa el lector directo del EC. Los ventiladores permanecen bajo firmware. Es una comparación diagnóstica: no habilita Manual por WMI ni demuestra todavía que el firmware esté libre de errores.

## Ejecutar

1. En la GUI, volver a **Firmware**, esperar la restauración y cerrar la app. Si aparece un lease pendiente, conservarlo; el lanzador rechazará la prueba. No borrar `lease.json` para saltar esa comprobación.
2. Abrir **PowerShell como administrador**. En el repositorio:

```powershell
cd C:\Users\TheMa\VictusFanControl
git switch feature/victus-8c40-wmi-broker-5sample
git pull --ff-only
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\Start-Victus-WmiOnlyInvestigation.ps1 -CaptureMinutes 30
```

El lanzador compila Release. Comprueba que no haya GUI, otro CLI, watchdog ajeno activo ni lease conservado, incluso vacío o ilegible. Verifica la ruta estándar del estado M4. Si M4 estaba ejecutándose, lo detiene temporalmente tras esas comprobaciones y lo vuelve a iniciar al terminar. Si estaba detenido, permanece detenido. No reinstala el servicio ni cambia su configuración.

3. Esperar **WMI-ONLY ACTIVO**. La preparación del recolector y de ETW tarda; comenzar la comparación después de ese mensaje. Esta prueba muestra una consola, no abre la GUI.
4. Usar el equipo normalmente con AC unos cinco minutos. Después, desconectar AC, esperar aproximadamente 20 segundos, reconectar y esperar un minuto. Repetir varias veces durante la sesión, con una carga similar a la captura anterior. Anotar cualquier demora visible y su hora. El muestreo registra el estado AC/batería informado por Windows cada dos segundos; ETW aporta la cronología ACPI/WMI.
5. Mantener la captura completa de 30 minutos, aunque no se reproduzca. Puede terminarse antes con **Q** en la consola del recolector. El cierre solicita al CLI que termine, vacía su log, valida el aislamiento y genera el ZIP y su SHA-256 en el escritorio. No cerrar la ventana a la fuerza.
6. Adjuntar el **ZIP completo**. Si se muestra `AISLAMIENTO INCOMPLETO`, adjuntarlo igualmente: incluye la causa del rechazo y la evidencia parcial. Para una sesión más larga, se acepta `-CaptureMinutes 60`.

No abrir la GUI ni otros arneses VFC durante esta comparación. Para aislar mejor el resultado, mantener cerradas herramientas externas que consulten el EC, si se estaban usando. El lanzador detecta procesos VFC mediante muestreo; no controla todos los programas del sistema.

## Qué queda registrado y qué se bloquea

El proceso instala una prohibición permanente antes de inicializar telemetría. El constructor de `AcpiEcReader` y sus primitivas de puertos rechazan acceso antes de cargar el módulo o efectuar I/O. El cliente HP WMI permite únicamente la consulta RPM `20008h/2Dh`, salida 128 bytes y payload de cuatro ceros. Todas las otras consultas HP, incluidas escrituras, restauración y el getter experimental `26h`, se rechazan antes de la llamada nativa. La combinación con comandos CLI de control, probes u otras pruebas se rechaza al analizar argumentos.

CPU conserva Intel MSR/PawnIO y GPU conserva NVML. **Sin EC directo no significa sin drivers ni sin EC:** HP WMI, Windows y el firmware pueden seguir usando internamente el EC. Las RPM son velocidad física; no sustituyen la verificación del setpoint o de la propiedad del control Manual.

El ZIP contiene las tablas ACPI estáticas, ETL, EVTX, mensajes de Windows, identidades y hashes habituales, además de:

| Archivo en `wmi-only/` | Contenido |
| --- | --- |
| `chronology.log` | PID/MVID, UTC/QPC, preparación y duración nativa WMI, RPM y marcadores de inicio/cierre |
| `telemetry.csv` | Telemetría periódica, incluida salud de los lectores y muestras RPM |
| `isolation-timeline.ndjson` | Procesos/servicios VFC, presencia de journal y alimentación informada por Windows |
| `isolation-summary.json` | Validación, errores, código de salida y pausa/restauración de M4 |
| `cli-stdout.txt`, `cli-stderr.txt`, `ready.json` | Salida visible del CLI e identidad de su inicio |

`ValidIsolation=true` exige una cronología con marcadores completos, al menos una respuesta RPM, sin intentos EC ni llamadas HP distintas de `2Dh`, sin pérdidas/truncamiento registrados, y comprobaciones de procesos/servicios durante la ventana. El watchdog se reinicia después del fin de esa ventana y antes de empaquetar, de modo que las trazas finales pueden incluir su arranque. La frontera cubre el CLI VFC; el muestreo no excluye procesos externos o transitorios entre muestras. Una captura circular ETW también puede perder eventos; su contenido se revisará aparte.

Al cerrar, el CLI espera hasta diez segundos por una consulta WMI ya admitida antes de vaciar la cronología. Cada inicio WMI debe tener un cierre con la misma operación. Si la llamada sigue pendiente, la captura se marca incompleta; no se afirma haber cancelado la llamada nativa.

Si no vuelve a iniciar M4, el lanzador lo informa y conserva el fallo. Tras cerrar la prueba, revisar su estado con `Get-Service VictusFanControlWatchdogM4`; si estaba activo previamente y quedó detenido, ejecutar `Start-Service VictusFanControlWatchdogM4` desde la misma consola elevada.

## Interpretación

Si reaparece una demora de unos cinco segundos y ACPI 13 con aislamiento válido, el lector directo VFC no es necesario para ese episodio; habrá que estudiar el camino Windows/BIOS/EC y otros clientes. Si no reaparece, ganamos evidencia a favor de la interacción con el lector directo, pero una sola sesión negativa no demuestra causalidad, especialmente porque el fallo previo fue intermitente. El modo Firmware y la pausa M4 también cambian respecto de Manual: para atribuir el origen harán falta comparaciones adicionales.

El diagnóstico MaxFan ahora decodifica FFFS con máscara `0x04`: `0x90` no implica que ese bit esté activo. Manual sigue rechazando todo byte no nulo hasta calificar otros estados; esta corrección no habilita valores nuevos ni cambia el transporte EC. La calificación del getter WMI `26h` y la sustitución de verificaciones Manual siguen pendientes.

## Informe automático de tiempos

Al finalizar una captura validada, el lanzador analiza offline `chronology.log` y añade `wmi-only/analysis/summary.json`, `wmi-calls.csv`, `ec-reads.csv` y `report.md` antes del ZIP. El análisis no inicializa sensores ni llama a HP WMI. Si falla, `AnalysisFault` lo registra y se conserva la cronología original.

También puede analizarse un log anterior después de compilar:

```powershell
.\src\VictusFanControl\bin\Release\net8.0-windows\VictusFanControl.exe --analyze-ec-wmi-trace "C:\ruta\ec-wmi-22704.log" --analysis-output-dir "C:\ruta\analisis-wmi"
```

Los intervalos usan QPC y la frecuencia del encabezado, por operación dentro de un solo proceso. `NativeMs` mide entre `wmi.invoke.begin/end`; `TotalMs` mide `wmi.send.begin/end`; `OutsideInvokeMs` es la diferencia e incluye preparación y finalización, sin medir la cola anterior al inicio de `send`. Los tiempos UTC solo ubican eventos. La mediana y P95 se calculan sobre intervalos disponibles; un timeout o excepción sin final nativo no recibe un tiempo nativo inventado. Los CSV conservan cada llamada y lectura EC. Una llamada `2Eh` se etiqueta escritura o liberación: el log de solicitud no registra payload y no permite distinguirlas por sí solo.

`ChronologyConsistent` evalúa integridad estructural del log, no equivale a `ValidIsolation` ni a ausencia de fallos físicos. Se reportan encabezados ausentes, IDs repetidos, finales sin inicio, operaciones pendientes, JSON inválido, pérdidas y límites. Logs concatenados con varios encabezados se rechazan para evitar mezclar PID/relojes. Las colas perdidas pueden hacer incompletos los recuentos. El resumen contiene el SHA-256 de los bytes exactos analizados; el archivo fuente permanece intacto. El analizador no sustituye la revisión ETL/EVTX ni establece causalidad.
