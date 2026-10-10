# Explicit GUI Performance recovery

Normal GUI startup continues to refuse existing CPU/GPU journals. A persisted
journal records prior ownership, not current hardware state or proof of release.
Recovery is an explicit, release-only action; it never starts Automatic, writes
fans, or reapplies PL1/PL2 or GPU presets.

Close VictusFanControl normally, including the tray, and close other GPU clock
controllers (Afterburner or scripts running `nvidia-smi -lgc`). Run from an
elevated PowerShell in a newly extracted, verified package:

```powershell
.\Start-ProductGui.ps1 -Mode Verify
.\Start-ProductGui.ps1 -Mode RecoverPerformance `
    -ExpectedCpuSession '<SessionId from cpu-power-session.json>' `
    -ExpectedGpuSession '<SessionId from gpu-clock-session.json>' `
    -ConfirmExclusiveGpuController
```

Both IDs must be nonempty and match each present journal. Missing journals are
already resolved; rerunning after partial recovery never reapplies a preset.
The helper requires elevation, the exact HP 8C40/F.18 target and i7-13700H,
the approved IntelMSR module hash, exclusive production Performance mutex,
and absence of other VictusFanControl processes. It does not kill processes.

Before opening hardware backends it validates both journals and durably copies
their exact bytes into a new `Victus-Performance-recovery-*` desktop directory.
A changed/corrupt/wrong-session journal is refused. A recovery report records
the result; retain the directory on failure. Never manually delete journals to
bypass the gate.

CPU uses the existing recovery executor: observe current RAPL, restore only
still-owned PL1/PL2 fields to the stored release target, preserve other MSR bits
and external changes, durably arm the write, compare again, and read back. A
locked or unresolved still-owned state retains its journal and blocks GPU reset.

GPU has no reliable exact locked-range ownership readback. The user's explicit
exclusive-controller confirmation authorizes one NVML Reset to NVIDIA defaults.
Reset is durably armed first; an unsuccessful or throwing call retains the
journal. A successful NVML acknowledgement permits clearing only the unchanged
armed record. It is not a measured locked-range readback or restored previous
external clock setting.

The 2026-10-06 diagnostic contains earlier successful CLIENT_SHUTDOWN releases,
but no terminal report for the 06:15 session. Its CPU Owned 30/50 W and GPU
ActiveUnverified 210/1802 MHz records cannot prove why the processes ended or
what limits remain in hardware. This recovery entry is covered by fake-backend
fixtures; physical recovery remains pending until its report is reviewed.


Recuperación unificada de sesiones retenidas (2026-10-10):

- La aplicación abre Configuración en modo recuperación antes de construir controladores o telemetría si hay registros pendientes. El inicio automático queda suspendido para esta apertura. El instalador usa el mismo inventario y ofrece recuperar antes de copiar/registrar la versión.
- Se detectan CPU/GPU del perfil actual, leases de GUI WMI y ensayos WMI, leases anteriores del watchdog y marcas nativas huérfanas de las sesiones locales de GUI. También se detectan esquemas desconocidos y registros de otros equipos; se conservan y no autorizan hardware.
- Cada selección incluye las huellas de los registros. Se capturan respaldos durables antes de admitir recuperación. Una sesión nueva, una huella distinta, otro proceso controlador/instalador o un servicio Victus activo bloquean la operación. Los procesos y servicios no se terminan por fuerza.
- GUI/ensayos WMI: sin intención de escritura y sin llamada incierta se archiva la lease sin escribir ventiladores. Con intención, sólo se admite FF/FF y LegacyDefault; ambos deben ser aceptados antes de archivar la lease. La aceptación WMI no acredita lectura independiente de propiedad Firmware.
- Watchdog anterior del mismo HP 8C40/F.18: se reutilizan JsonLeaseJournal, WatchdogLeaseManager y el adaptador M4 cualificado. Se valida el módulo LpcACPIEC antes de abrirlo; sólo lectura de propiedad EC y la restauración fija existente. Un setpoint externo/ambiguo conserva el journal. No se introducen escrituras de valores de registros EC ni niveles manuales. Esta ruta ocurre en un proceso de recuperación separado de la GUI WMI.
- Una marca de finalización nativa incierta del arranque actual impide nuevas llamadas. Tras Reiniciar Windows, LastBootUpTime y QueryInterruptTime deben concordar; todas las marcas deben ser al menos dos minutos anteriores al arranque. Se respaldan y archivan sus bytes originales antes de una liberación explícita. Suspensión, hibernación o Inicio rápido no se consideran prueba de reinicio del kernel. Un reloj incoherente impide la recuperación.
- Marcas huérfanas sin lease: sólo se archivan tras el reinicio comprobado, sin escribir hardware y únicamente si no existe intención de escritura. Una intención sin evidencia de propiedad, un archivo dañado o un registro de otro equipo requieren diagnóstico; no se inventa una sesión.
- La evidencia queda en %LOCALAPPDATA%/VictusFanControl/Recovery/<id>. La lease WMI original se conserva junto a su ruta con sufijo .recovered-<id>. Los fallos mantienen el registro pendiente. Exportar diagnóstico incluye un inventario y copias limitadas de registros/receipts, sin modificarlos.
- Al completar, la misma GUI conserva borrador/perfiles y construye una sesión nueva en Firmware. Automático requiere clic manual. Si la liberación del controlador activo no puede confirmarse, la GUI no abre otro controlador ni recuperador; cerrar normalmente y Reiniciar Windows permite volver a la pantalla de recuperación al iniciar.

Referencias de Windows: [estados de energía e Inicio rápido](https://learn.microsoft.com/en-us/windows/win32/power/system-power-states), [tiempo de interrupción, incluyendo suspensión/hibernación](https://learn.microsoft.com/en-us/windows/win32/sysinfo/interrupt-time), [LastBootUpTime](https://learn.microsoft.com/en-us/windows/win32/cimwin32prov/win32-operatingsystem).

Validación software: inventario compartido; casos de liberación/rechazo, marca del mismo/otro arranque, huellas cambiadas, intención huérfana y cierre/suspensión; ninguna fixture escribe hardware real. La comprobación física de esta nueva interfaz en el Victus sigue pendiente.
