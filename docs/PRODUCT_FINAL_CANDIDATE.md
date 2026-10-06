# Candidata final — HP 8C40 / F.18

Esta candidata reúne la GUI, los dos perfiles AC/Batería, la curva única,
influencias hasta 200 %, simulador continuo, límites CPU/GPU editables en vivo,
Avanzado y diagnósticos por sesión. Las preferencias existentes se conservan.
La identidad del paquete está en `PRODUCT-GUI-MANIFEST.json`; los pendientes
están en `PRODUCT-FINAL-CANDIDATE.json`. Aún no autoriza Automático normal.

## Comprobación del paquete sin usar hardware

Desde la carpeta extraída:

```powershell
.\Start-ProductGui.ps1 -Mode Verify
.\Start-ProductGui.ps1 -Mode FinalCheck
```

FinalCheck ejecuta las pruebas de GUI, el soak y los fixtures de recuperación
contra los ejecutables del paquete. No activa ventiladores ni límites reales.
CI también verifica compilación, IPC, Guardian, fuente, lifecycle, manifiestos
y distribución reproducible. Un PASS de software no aprueba el hardware.

## Contrato térmico de esta candidata

| Condición CPU cruda (paquete o núcleo más caliente) | Comportamiento |
|---|---|
| Entrada a Automático | Tres adquisiciones completas y vigentes con CPU ≤90 °C |
| Sesión establecida, CPU 90–94.x °C | Continúa; objetivo térmico máximo del perfil, subida protegida, sin plazo adicional de 90 °C |
| CPU 95–98.x °C | Confirmación de hasta 2000 ms y cinco muestras únicas; una lectura fresca <95 °C antes del plazo recupera |
| CPU ≥99 °C | Solicita Firmware inmediatamente |

La revisión anterior añadía una confirmación por encima de 90 °C. Se elimina
esa diferencia respecto al controlador; se conservan sus límites críticos.
La recuperación tardía no rescata un plazo vencido. GPU >82 °C, CPU >60 W,
GPU >75 W, datos inválidos/caducos, pérdida de supervisión y lifecycle siguen
interrumpiendo. No se promete cancelar una llamada WMI ya iniciada.

«Ver motivo completo» conserva la causa, la muestra del disparo y los estados
actuales aunque la telemetría se recupere. Un bloqueo requiere salida limpia
y nueva apertura; no hay reentrada espontánea. Firmware voluntario conserva
la sesión independiente de límites. «Desactivar límites CPU / GPU» y Salir
solicitan su liberación. NVML acepta la petición GPU sin probar el rango exacto;
WMI acepta solicitudes sin probar propiedad independiente de ventiladores.

## Única regresión física agrupada pendiente

Las pruebas ya aprobadas están en `PRODUCT_VALIDATION_STATUS.md`. No repetir
los ensayos largos de carga para volver a aprobar sus funciones.

1. Salir normalmente de la versión anterior. Verify y abrir esta candidata con
   `-Mode AutomaticExtendedReview`. Se inicia en Firmware; la activación sigue
   siendo explícita y cada revisión está limitada a 45 minutos.
2. Activar Automático con la configuración habitual. Hacer uso representativo
   breve, conservar el diagnóstico y registrar si aparece un pico. No provocar
   una temperatura crítica ni reducir umbrales para intentar pasar la prueba.
   La ausencia de un pico valida estabilidad observada, no una recuperación
   física de 95–98 °C. Una interrupción debe conservarse como resultado.
3. Con actividad ligera, suspender y reanudar Windows. Exportar: ventiladores
   en Firmware/bloqueados y ninguna reentrada espontánea. El Guardian de límites
   tiene su propio ciclo; su estado no concede autoridad a los ventiladores.
4. Salir desde la bandeja y conservar la salida de PowerShell. Comprobar ausencia
   de journals CPU/GPU pendientes; si existen, conservarlos con el informe.
   Abrir con `-Mode Open` y exportar: Firmware y límites sin aplicar por arranque.

Después de esta regresión se podrá promover el modo normal con su contrato
explícito. Arrancar con Windows seguirá abriendo en Firmware; no implica
rearme desatendido. Un fallo de liberación impide cerrar la versión estable.
