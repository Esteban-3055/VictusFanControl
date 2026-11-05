# Iconos definitivos

Originales proporcionados por el usuario en `iconos.zip` (10 de octubre de 2026). Se conserva cada PNG sin alteración. `manifest.json` registra los nombres y SHA-256 originales, y los de cada ICO derivado.

El icono adicional de micrófono silenciado fue proporcionado el 5 de noviembre de 2026. Su PNG también se conserva sin alteración.

| Archivo | Uso |
|---|---|
| program | EXE del programa, ventana, instalador y acceso del menú Inicio |
| tray-default | Bandeja en Firmware/Manual/preparación |
| tray-automatic | Automático activo con autoridad Custom y runtime Healthy |
| tray-automatic-muted | Automático activo y micrófono HyperX confirmado en silencio; no se usa en Manual/Firmware/preparación |
| tray-error | Interrupción o fallo; permanece hasta reactivación correcta |

El fallo tiene prioridad sobre el silencio. Sin confirmación de silencio, o con el receptor desconectado/los audífonos apagados, se conserva el icono normal de Automático. La presentación utiliza los eventos HyperX existentes, independientemente de activar avisos de silencio, sin consultas USB adicionales.

Los ICO se convierten con Pillow, conservando RGBA, en 16, 20, 24, 32, 40, 48, 64, 128 y 256 píxeles. La GUI los incorpora como recursos y los libera al cerrar; no depende de archivos de imagen instalados por separado.
