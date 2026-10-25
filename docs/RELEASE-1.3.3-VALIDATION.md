# Validación de la candidata local 1.3.3

25 de octubre de 2026. Canal **preliminar**, autorización estable desactivada. No se ha instalado ni publicado una release 1.3.3. Los archivos de la estable 1.3.2 conservan su identidad original.

## Fuentes y alcance

- `2ca9d2f`: ciclo de vida, cierre y comprobación del propietario NVIDIA; [bloque 1](STABILITY-BLOCK1-LIFECYCLE.md).
- `4b8062f`: publicación/IO opcional, configuración inmutable, arranque, sensores, recuperación compartida, energía e instalador; [bloques 2–9](STABILITY-RECOVERY-HARDENING.md).
- `7150cfa`: versión 1.3.3 y canal preliminar explícito; compatibilidad del actualizador y empaquetado.
- `e965e35fe8614bf3afbf82fba9dc12634164a64f`: integración local de 19+5 grupos con fuentes limpias; el caso de preferencias dañadas espera su lectura asíncrona y conserva el archivo original.
- `1d2235b2edc81652b311db8cd776cd7361714189`: fuente del paquete local. Solo cambia dos textos del instalador respecto de e965e35; ningún archivo del runtime, dependencias compartidas, parámetros de compilación o protocolo cambia entre ambos. Su payload conserva esos mismos inputs de compilación y se vuelve a verificar por archivo.
- `017db14`: comprobación histórica M9D adaptada a la declaración actual de módulos. Exige todavía la barrera anterior a la resolución y la ruta estricta de calificación. No cambia el producto ni los binarios del paquete local.

## Resultados locales

| Comprobación | Resultado y límite |
|---|---|
| Solución Release, versión de producto | PASS; cero advertencias y errores, 1.3.3.0 |
| Regresiones generales | 19/19 PASS sobre e965e35; protocolo/TCP real de loopback, DPAPI, GUI, curvas externas, telemetría, rendimiento, WMI, admisión y recuperación con fixtures |
| Estabilidad adicional | 5/5 PASS sobre e965e35: pipeline, GUI, NVIDIA aislado, instalador y prototipo separado |
| NVIDIA aislado | 25 casos: progreso con identidad/secuencia, presupuesto absoluto, fallos, procesos retenidos y salida; host High comprobado antes de enviar initialize, sin invocar GPU |
| Actualizador original 1.2.9 | PASS; 1.3.3 numérica requiere activar preliminares desde 1.2.9–1.3.2; el canal estable sigue eligiendo 1.3.2 |
| Contrato histórico/térmico | PASS; autorización preliminar separada de evidencia física; umbrales, identidad, EC prohibido y calificación histórica conservados |
| GUI de los binarios del paquete | PASS mediante su entrada DLL de fixture; incluye preferencias dañadas, cierre, suspensión, recuperación y publicación tardía |
| Soak del paquete | PASS; 30 aperturas/cierres, 924 renders, GDI 19→19 y USER 19→19; bytes privados 34.709.504→41.377.792, sin afirmar ausencia de toda fuga por una sola ejecución |
| Recuperación del paquete | PASS; ambas entradas ficticias del Guardian, sin órdenes de hardware |
| Instalador | PASS; 14 casos de paquete, preferencias y extracción del ZIP incrustado desde la entrada DLL; EXE autocontenido generado e identidad 1.3.3.0 verificada |
| Formato público e integridad | PASS; 119 archivos listados, tamaños/SHA-256/ZIP; prueba local del formato, sin atribuir sus bytes a un artefacto de CI |
| M9D | PASS local tras actualizar el reconocimiento de la declaración; se mantiene el rechazo de ejecución física y construcción histórica |

`OfflinePackages` omite únicamente la auditoría de paquetes en red de la ejecución local. Las pruebas de GitHub conservan su configuración habitual. Sus resultados por commit se enlazan en el [PR #44](https://github.com/Esteban-3055/VictusFanControl/pull/44); no se sustituye un resultado pendiente por el PASS local.

El empaquetado de GitHub encontró una carrera en el fixture de reinicio fallido: verificaba la presencia del ZIP opcional antes de concluir su escritor. La prueba comprueba inmediatamente que no exista recibo de liberación, espera después la finalización opcional y vuelve a comprobar recibo/evidencia antes de borrar su directorio ficticio. Esa espera pertenece solo al test; no se vuelve a acoplar el ZIP al drenaje de producción.

La primera ejecución directa del apphost empaquetado fue rechazada por Windows porque requiere elevación. Se conservó ese requisito y se ejecutaron las entradas DLL aisladas. **No se presenta como PASS del arranque normal elevado, del EXE instalador ejecutado ni de una instalación real.** La prueba del instalador incrustado utiliza los mismos recursos/ZIP y fuente en una compilación DLL de fixture. El EXE final no se ejecuta para instalar durante esta validación.

## Archivos locales de la preliminar

| Archivo | Tamaño | SHA-256 |
|---|---:|---|
| `VictusFanControl-1.3.3-win-x64.zip` | 7.058.909 bytes | `2171388e45d7cadc8a082844293c9c600f61f9a63f5121a2ae8d46cc5f203d2d` |
| `VictusFanControl-1.3.3-Setup-win-x64.exe` | 168.902.111 bytes | `2cbd1afdb4210dc3294ff826c0eedabdde73d55577077a4d6c010f2218dcb2f0` |

Estos hashes corresponden a la construcción **local** 1d2235b. Una construcción posterior de GitHub tiene su propia procedencia y no se asume idéntica. El instalador carece de firma de código, conserva perfiles y no migra journals a un formato nuevo. La recuperación previa a instalar mantiene su comprobación exclusiva y confirmación de retorno; un estado incierto detiene la operación.

La copia del instalador estable 1.3.2 disponible para reversión se volvió a comprobar: SHA-256 `16fef2ef8358fb2074c57864219cc55a885e50794d45923342693e9b823aa164`. Volver de versión exige cerrar normalmente y confirmar la liberación actual; nunca elimina incertidumbre por sí mismo. No se cambia la instalación para ensayar una reversión.

## Evidencia y trabajo físico pendiente

Los logs completos, manifiestos, procedencia local y hashes de binarios permanecen en `output/`, fuera de Git. No se incluyen rutas personales, claves, SSID, contraseñas ni diagnósticos privados en los assets. La prueba de arquitectura del bloque 9 queda fuera del paquete de uso normal y no adquiere hardware.

El historial de fases es pasivo y acotado. Su generación tiene contexto: época de energía del worker, secuencia de publicación o petición NVIDIA; no es una identidad intercambiable ni un recibo de liberación. La adquisición conserva su fecha original.

Quedan uso real AC/batería/retorno, sensores y recuperación, cierre, siguiente inicio habitual de Windows y arranque elevado del candidato. No se fuerza un reinicio para obtener evidencia. No hubo comandos físicos a ventiladores/límites, cambios en tareas reales ni nuevo PASS de ESP32. Firmware, NVS, pines, frecuencia/inversión PWM y HMAC se conservan.

Una llamada nativa CPU que no retorna sigue reteniendo exclusión; añadir limpieza y diagnóstico no la hace cancelable. No se promete recuperación universal del kernel. La función nativa concreta del bloqueo original del 23 de octubre continúa sin demostrarse por falta de una captura de pilas de aquel instante.
