# VictusFanControl v1.2.10 · Preliminar 2 — Base externa / ESP32

Preliminar de la fase Windows. Incluye la página **Base externa** en la barra lateral,
entre **Avanzado** y **Actualizaciones**, y las correcciones posteriores a preview.1.

## Actualizar desde el programa

En la versión 1.2.9 (también en la preliminar anterior que mostraba 1.2.9), abrir
**Actualizaciones**, activar **Incluir versiones preliminares** y pulsar **Buscar actualizaciones**.
Después pulsar **Descargar** y **Instalar**. Guardar o descartar cambios pendientes antes de instalar.
La descarga se conserva y el instalador mantiene los perfiles y el emparejamiento DPAPI.

Esta publicación usa la etiqueta numérica **v1.2.10**, versión interna **1.2.10** e instalador
**VictusFanControl-1.2.10-Setup-win-x64.exe**, compatibles con el actualizador 1.2.9.
GitHub la marca como **Pre-release**; la estable v1.2.9 sigue siendo la versión del canal estable.
La aplicación identifica esta compilación como **Preliminar**. Las siguientes publicaciones
usarán números superiores para que las instalaciones existentes puedan detectarlas.

## Cambios incluidos

- Configuración ESP32 integrada en su propia página, con habilitación, host, puerto,
  reconexión, simulador, emparejamiento y diagnósticos desplegables.
- Cambiar de pestaña conserva la sesión y el borrador; minimizar mantiene la transmisión.
- Diagnóstico actualizado desde la primera entrada y ocultación de la clave al salir.
- Reintento de clave Base64 inválida, cierre controlado del simulador y recuperación tras
  mensajes incorrectos; diagnóstico de fallos por categoría y fecha.
- Mayor validación de antigüedad de la Solicitud y temporizador independiente de fallback
  en el simulador, además de protecciones ante carreras de suspensión/reanudación y cierre.

El simulador se descarga por separado: **Esp32Simulator-v1.2.10-win-x64.zip**.
Requiere .NET 8 Runtime x64. Ejecutar `Esp32Simulator.exe --port 8765` e introducir
la misma clave generada en **Base externa**. Las instrucciones y el protocolo se adjuntan.

Validación mediante pruebas automáticas y simulador TCP, sin órdenes físicas al notebook.
No incluye firmware ni acredita funcionamiento físico de ESP32, NTC, LCD o PWM.
Los límites y protecciones del controlador interno conservan su comportamiento existente.
El instalador conserva el requisito de .NET Desktop Runtime 8 x64 para la aplicación.
