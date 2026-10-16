# VictusFanControl v1.2.11 · Preliminar 3 — Interfaz integrada de Base externa

La pestaña Base externa utiliza las mismas tarjetas, botones, tipografía, escala y acentos
que las demás páginas de VictusFanControl. Se mantiene entre Avanzado y Actualizaciones.

## Cambios

- Estado real de conexión y última comunicación siempre visibles, separados del modo en edición.
- Tres botones de modo: Deshabilitado, Monitorización y Monitorización + Solicitud.
- Tarjetas separadas de conexión y emparejamiento; campos oscuros sin bordes ni casillas clásicos.
- Guardar cambios, Descartar y Conectar/Desconectar alineados abajo. El borrador es independiente
  de los perfiles del notebook y se conserva al navegar.
- Olvidar emparejamiento prepara una eliminación que se aplica al guardar y puede descartarse.
- Diagnóstico con ambiente, PWM, contadores y mensajes operativos seleccionables para copiar.
  Los datos del dispositivo caducados se muestran como —. La clave vuelve a ocultarse al salir.
- La conexión permanece activa al cambiar de página o minimizar. El protocolo TCP no cambia.

## Actualizar desde 1.2.9 o 1.2.10

1. En **Actualizaciones**, activar **Incluir versiones preliminares**.
2. Ir a **Configuración → Guardar preferencias**.
3. Volver a **Actualizaciones → Buscar actualizaciones → Descargar → Instalar**.

Etiqueta numérica **v1.2.11**, aplicación e instalador **1.2.11**. Es una **Pre-release**;
la estable v1.2.9 permanece en el canal estable. Se conservan perfiles y emparejamiento DPAPI.
El simulador separado es **Esp32Simulator-v1.2.11-win-x64.zip**, ejecutable con .NET 8 Runtime x64.
La aplicación conserva el requisito de .NET Desktop Runtime 8 x64.

Validación mediante pruebas automáticas y simulador TCP. Sin firmware ni PASS físico del ESP32,
NTC, LCD o PWM. El rediseño no cambia controladores, límites ni protecciones del notebook.
