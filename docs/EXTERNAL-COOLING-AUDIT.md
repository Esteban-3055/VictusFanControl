# ExternalCooling: auditoría de base (2026-10-15)

## Repositorio vivo

Consultados mediante API de GitHub: repositorio, ramas, releases, PR abiertos y Actions.
La estable más reciente es **v1.2.9**, SHA `e8a58a5cb01eb673114a8dca0c5c0e925f55fddc`, en
`feature/fan-54-52-preview`. Los flujos build, cpu-rapl, oem-shadow y wmi-fan-experiment
del SHA terminaron success. `main` permanece en `9d3924ce1fdd76770df681586bb4cf8be4041788`.
PR abiertos: #24 (RAPL, otra base), #3 y #2 (dependencias Actions). No incorporados.
Nueva rama: `feature/external-cooling-wifi`, desde la estable; no desde main.
El checkout no tenía modificaciones tracked; se conservaron los logs untracked previos.

## Fuentes y límites de integración

* `Telemetry/TelemetrySnapshot.cs`: lecturas inmutables, fechas originales de sensores,
  tolerancia de producto y validez de RPM. CPU `CpuLoadPercent`, GPU `GpuLoadPercent`;
  temperatura de control CPU y GPU, sin nuevas adquisiciones de hardware.
* `App/TelemetryWorker.cs` publica SnapshotAvailable después del procesamiento automático.
* `App/Product/ProductRuntime.cs`: estado inmutable publicado bajo lock y evento Changed.
  `AutomaticDecisionSnapshot` corresponde a la decisión aceptada; `Snapshot` a la lectura actual.
* Demanda: `AutomaticDecision.UnifiedDemand.Percent`. Solicitud lógica aceptada:
  `FanLevel`, procedente de `Hp8C40WmiFanControlBackend.LastAcceptedLevel` mientras
  FanAuthority=Custom. `EqualFanLevel` es nivel lógico, no porcentaje de Demanda.
* `Hp8C40ProductFanTargets`: una solicitud lógica se mapea a destinos físicos distintos
  (máximos CPU 54 / GPU 52). Exportar también los destinos observados en el estado;
  no convertirlos a PWM ni imponer 10–50. El backend no ofrece lectura del setpoint
  físico: aceptación WMI no equivale a prueba física independiente.
* `ProductHomeTelemetry` y animaciones retienen datos para presentación. ExternalCooling
  tendrá admisión propia conservadora y no utilizará AnimationRequest/AnimationDemand.
* `ProductForm`: ocultar/minimizar mantiene runtime activo; WM_POWERBROADCAST gestiona
  suspensión/reanudación; ShutdownRuntimeAsync drena trabajos antes de DisposeAsync.
  ExternalCooling tendrá su propio ciclo cancelable, sin await de red en WndProc.
* `ProductProfilesStore`: JSON validado, escritura temporal atómica y preferencias en
  LocalAppData/VictusFanControl. El módulo usará el mismo directorio/patrón en archivo
  separado; emparejamiento protegido por DPAPI CurrentUser, fuera de diagnósticos.
* `.github/workflows/build.yml`: Release con warnings como errores, self-tests CLI y
  WinForms, guardianes, publicación del payload y pruebas del instalador ProductSetup.
  `release-v1.yml` solo publica tras push exitoso a main; esta rama no publica releases.

## Validación de base

El primer intento detectó Python fuera de PATH y assets NuGet ausentes. Se utiliza Python
incluido en el runtime del entorno y build serial (-m:1). NuGet no puede consultar su
servicio de vulnerabilidades en este entorno: restauración local con NuGetAudit=false;
esto no sustituye una auditoría de dependencias en CI. Resultados ejecutados se registran
en el informe de validación final. No se ejecutan pruebas físicas de control del notebook.
