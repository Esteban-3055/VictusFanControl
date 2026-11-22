# Iconos definitivos

Originales nuevos proporcionados por el usuario en noviembre de 2026, conservados también en `output/interface-references-20261121`. Se copia cada PNG sin alteración. `manifest.json` registra los nombres y SHA-256 originales, y los de cada ICO derivado.

La identidad normal, Automático, Automático silenciado y fallo corresponden a las cuatro imágenes nuevas. La imagen normal se comparte con el estado Firmware/Manual/preparación. Sus colores originales y transparencia se mantienen.

| Archivo | Uso |
|---|---|
| program | EXE del programa, ventana, instalador y acceso del menú Inicio |
| tray-default | Bandeja en Firmware/Manual/preparación |
| tray-automatic | Automático activo con autoridad Custom y runtime Healthy |
| tray-automatic-muted | Automático activo y micrófono HyperX confirmado en silencio; no se usa en Manual/Firmware/preparación |
| tray-error | Interrupción o fallo; permanece hasta reactivación correcta |

El fallo tiene prioridad sobre el silencio. Sin confirmación de silencio, o con el receptor desconectado/los audífonos apagados, se conserva el icono normal de Automático. La presentación utiliza los eventos HyperX existentes, independientemente de activar avisos de silencio, sin consultas USB adicionales.

`scripts/prepare-monochrome-assets.ps1` empaqueta las imágenes RGBA en ICO con 16, 20, 24, 32, 40, 48, 64, 128 y 256 píxeles mediante System.Drawing y entradas PNG. No aplica tintes ni redibuja los símbolos. La GUI los incorpora como recursos y los libera al cerrar; no depende de archivos de imagen instalados por separado. El rotor se copia sin modificar a `assets/rotors/home-fan.png` y su renderizador ya no cambia sus colores según el estado.
