# Estabilidad: primer bloque de correcciones

Fecha: 24 de octubre de 2026. Base auditada: `0a51c8e071139687dc8bcc76c9708ebfcd5d6cc6` (1.3.2). Implementación: `2ca9d2f6c892384edd54d5f0dca39e2cb8d991be`, rama `feature/external-curve-firmware`.

Este bloque implementa las reparaciones fundamentales de señales, vigilancia, limpieza y padre desaparecido. No constituye la corrección completa del incidente de alternancia Automático/Manual y cierre. El programa instalado y la versión pública no se actualizan con estas pruebas.

Trazabilidad con la auditoría: A02, A03 y el cierre local de A04; padre perdido de A08 y orden de admisión de A01. Las dependencias lentas de A01 y la inicialización/prioridad de A08 siguen en sus bloques posteriores. Del bloque 0 se formalizan aquí estas regresiones; el resto se incorpora junto con la reparación que debe verificar.

## Comportamiento corregido

- `TelemetryWorker.Wake`: una señal pendiente se combina con la siguiente; se elimina la carrera entre consultar el contador y liberar el semáforo. El máximo sigue siendo uno. Señales durante/después del cierre son inocuas.
- Vigilante: registra la petición de recuperación antes de notificar el cambio de estado o escribir diagnósticos. No altera la fecha de adquisición. El estado y cada observador de estado/log se entregan independientemente: una excepción no impide los siguientes. Se conserva en memoria un contador y el último error, con mensaje limitado a 512 caracteres y sin IO adicional en ese camino.
- Interrupción de Automático por cambio de estado: el bloqueo de admisión y la petición de retorno a Firmware preceden al registro opcional de la interrupción.
- Cierre: todas las llamadas concurrentes comparten una sola tarea de disposición, reservada antes de ejecutar callbacks de cancelación. Se espera a ambos bucles y al gate del lector. Los fallos de tareas terminadas no omiten limpieza; se intentan los recursos independientes aun si falla otro paso. Los errores originales se devuelven agregados.
- `TelemetryShutdownReport`: distingue errores de tareas terminadas y fallos de limpieza. Solo confirma drenaje local después de terminar ambas tareas y disponer los recursos. Una llamada nativa pendiente sigue reteniendo la espera; no se sustituye por un timeout que afirme liberación.
- `ProductRuntime`: un fallo histórico de tarea con limpieza local confirmada queda como diagnóstico y no se confunde con una limpieza pendiente. La liberación de ventiladores, CPU y GPU continúa comprobándose por separado. El informe del lector nunca certifica propiedad nativa ni permite ignorar journals.
- Auxiliar NVIDIA: padre ausente, identidad de inicio distinta o imposibilidad de verificarlo producen salida controlada 197 antes de inicializar GPU. Argumentos inválidos producen 2. Su monitor conserva la comprobación de vida del proceso abierto. No se añaden comandos de control ni terminación de mutadores.

## Regresiones mantenidas en el repositorio

`TelemetryLifecycleSelfTest` usa los métodos de producción con tareas/lectores sintéticos; esos fixtures no pueden iniciar adquisición de hardware. La suite forma parte de `--telemetry-coordination-self-test`, además de poder ejecutarse por separado.

| Caso | Resultado exigido |
|---|---|
| Ocho emisores, 600 rondas sincronizadas | 4.800 avisos sin excepción; el siguiente aviso sigue despertando |
| Observador de estado que falla | Estado confirmado, segundo observador ejecutado y error acotado |
| Observador y logger del vigilante que fallan | Recuperación solicitada antes de notificar, otros logs entregados, frescura intacta |
| Dos bucles ya fallidos | Limpieza ejecutada una vez y ambos errores conservados |
| Limpieza del lector que falla | Resto de recursos dispuestos e informe sin confirmación de drenaje |
| Una tarea fallida y otra retenida | Ninguna limpieza ni confirmación anticipada; se espera al retorno real |
| Dos solicitudes de cierre con tarea retenida | Misma tarea, ninguna disposición duplicada, espera efectiva |
| Callback de cancelación que reentra y falla | Misma tarea de cierre, limpieza restante intentada y fallo conservado |
| Cancelación esperada de los bucles | Drenaje normal sin inventar un fallo de tarea |
| Ocho emisores durante el cierre | 8.000 avisos concurrentes sin excepción no controlada |
| Padre ausente/PID con inicio distinto | Salida controlada antes de GPU; argumentos vacíos rechazados |

## Validación

Antes de reparar, los siete casos iniciales fallaron: `SemaphoreFullException`, propagación de errores de observadores, omisión de limpieza, disposición duplicada y excepción por padre ausente. Después, los once casos finales pasan. Los cuatro casos adicionales refuerzan cancelación, cierre concurrente y espera cuando otra tarea ya falló; no se presentan como reproducciones independientes de la causa física del incidente.

| Comprobación | Resultado |
|---|---|
| Solución Release, advertencias como errores | PASS: cero errores y advertencias |
| Ciclo de vida de telemetría | PASS: 11 casos, sin IO de hardware |
| Aislamiento NVIDIA con auxiliares ficticios | PASS, sin inicialización de GPU real |
| Recuperación de producto/actualización con sesiones ficticias | PASS, conservando bloqueos por propiedad incierta |
| Batería integrada de 19 grupos | PASS: 19/19, commit `2ca9d2f` |
| Equipo/ESP32 físicos | Pendiente; estas pruebas no escriben hardware |

Comandos de reproducción desde el repositorio, en Windows con dependencias restauradas:

```powershell
dotnet build VictusFanControl.sln -c Release -m:1 --no-restore -warnaserror -p:NuGetAudit=false -p:UseSharedCompilation=false -p:VictusProductRelease=true -nr:false
$app = 'src/VictusFanControl.App/bin/Release/net8.0-windows/VictusFanControl.App.dll'
dotnet $app --telemetry-lifecycle-self-test
dotnet $app --nvml-telemetry-self-test
dotnet $app --product-recovery-self-test
./scripts/test-external-cooling.ps1 -SkipBuild -OfflinePackages
```

`OfflinePackages`/`NuGetAudit=false` evitan la consulta de vulnerabilidades de paquetes durante esta validación local; no equivalen a una auditoría de dependencias aprobada. La suite NVIDIA provoca fallos de procesos ficticios intencionales y puede generar informes de Windows para esos hijos; no constituyen un fallo observado de la GPU física.

Los registros privados del antes/después, compilación, suites y manifiesto SHA-256 se conservan fuera de Git, bajo `output/`. La validación integrada identifica el commit de código; cambios posteriores de documentación no modifican los binarios comprobados.

Paquete privado: `output/stability-block1-20261024/`, con 28 artefactos sellados y hashes de los ocho archivos de código y dos assemblies probados. SHA-256 de `evidence-manifest.json`: `aaf6bd1d35a46879a0b06f600a8a2e950212788d77617fff6a4e9daab16f2079`.

## Límites y siguiente bloque

Los callbacks y el log siguen siendo síncronos: aquí se aíslan excepciones, pero una dependencia lenta todavía puede retenerlos. Separar adquisición/control de disco, UI y ZIP, limitar colas y congelar perfiles aplicados corresponde al bloque 2. También siguen pendientes las reparaciones planificadas de arranque, fases de sensores, política única de recuperación, suspensión y relanzamiento.

No se amplían límites de frescura ni umbrales, no se cambia la prioridad elegida, no se introduce EC/TZ01/DTT3 y no se cambia protocolo, HMAC, NVS, pines o firmware ESP32. No se han instalado binarios, reiniciado Windows ni publicado una candidata. El retorno de un driver bloqueado continúa siendo una incertidumbre explícita, no una promesa de recuperación universal.
