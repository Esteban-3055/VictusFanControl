# Iconos definitivos

Originales proporcionados por el usuario en `iconos.zip` (10 de octubre de 2026). Se conserva cada PNG sin alteración. `manifest.json` registra los nombres y SHA-256 originales, y los de cada ICO derivado.

| Archivo | Uso |
|---|---|
| program | EXE del programa, ventana, instalador y acceso del menú Inicio |
| tray-default | Bandeja en Firmware/Manual/preparación |
| tray-automatic | Automático activo con autoridad Custom y runtime Healthy |
| tray-error | Interrupción o fallo; permanece hasta reactivación correcta |

Los ICO se convierten con Pillow, conservando RGBA, en 16, 20, 24, 32, 40, 48, 64, 128 y 256 píxeles. La GUI los incorpora como recursos y los libera al cerrar; no depende de archivos de imagen instalados por separado.
