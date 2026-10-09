# VictusFanControl v1.0.0 — HP 8C40 / F.18

Primera entrega para uso habitual del destino exacto **HP 8C40 / 9D0R1LA / BIOS F.18**, i7-13700H y RTX 4060 Laptop. Otros modelos/BIOS quedan fuera de la autorización de control de esta versión.

## Cambios

- Nueva curva AC predeterminada con menor demanda intermedia y descenso más pausado; batería conserva su preset.
- Automático habitual sin vencimiento de revisión; prepara ambos límites CPU/GPU antes de activar ventiladores.
- Instalación en ruta fija, inicio elevado con Windows, minimizar y activar Automático al iniciar, configurables.
- Arranque con un único intento tras tres lecturas frescas. Suspensión/interrupción requiere reactivación manual; reiniciar sesión abre en Firmware.
- TZ01/DTT3 opcional, experimental y desactivado de fábrica.
- Corrección del preflight de journals pendientes y de la transición AC/Batería con los nuevos tiempos. Se preservan curvas personalizadas, límites y evidencia histórica.

## Descargar e instalar

Descarga **VictusFanControl-1.0.0-win-x64.zip** (los archivos automáticos “Source code” son el código fuente). Extrae todo, sal de la GUI anterior desde la bandeja y abre PowerShell como administrador con tu misma cuenta:

```powershell
Set-ExecutionPolicy -Scope Process -ExecutionPolicy Bypass -Force
.\Start-ProductGui.ps1 -Mode Install
```

Esto habilita inicio con Windows, minimizado y Automático al iniciar; puedes desactivarlos en Configuración. Requiere Windows 11 x64, .NET Desktop Runtime 8 x64, PawnIO y NVIDIA/NVML. Se incluyen módulos de la aplicación, no los instaladores de requisitos.

Para comprobar sin comandos de hardware: `-Mode Verify` o `-Mode FinalCheck`. El archivo SHA-256 y la procedencia adjuntos vinculan el ZIP a su compilación y commit. [Guía completa](https://github.com/Esteban-3055/VictusFanControl/blob/v1.0.0/docs/PRODUCT_V1.md).

## Alcance comprobado

Pruebas de software Windows/Linux, fixtures del paquete y replay: sin hardware. Las evidencias físicas previas conservan su alcance original. Esta publicación es una entrega autorizada para el destino exacto y **no declara un nuevo PASS físico completo**.

Quedan observaciones de uso representativo con el contrato térmico actual, suspensión sin reentrada, salida/reinicio limpios, arranque real de Windows y medición térmica/acústica comparable. El replay reduce demanda simulada; **no demuestra menos dBA ni temperaturas equivalentes**. WMI no prueba propiedad independiente del firmware y NVML no verifica el rango exacto GPU. Los pendientes siguen en `PRODUCT-RELEASE.json`.

Se mantienen las protecciones CPU 95 °C/2000 ms y 99 °C inmediata, GPU >82 °C, potencia CPU >60 W/GPU >75 W, frescura y watchdog. [Estado de validación](https://github.com/Esteban-3055/VictusFanControl/blob/v1.0.0/docs/PRODUCT_VALIDATION_STATUS.md).
