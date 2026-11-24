# Validación de 1.15.1 preliminar

## Alcance

Correcciones de la auditoría de 1.15.0 y mejoras del editor Funciones. Las pruebas usan el coordinador de producción y supervisores restringidos a sus entradas de simulación. No abren la interfaz del usuario, adquieren sensores físicos, prueban apagados reales ni modifican los archivos instalados.

## Casos reproducibles

`scripts/test-function-lifecycle.ps1 -RuntimeDirectory <carpeta app publicada>` copia el candidato a una carpeta temporal y ejecuta 74 resultados comprobados, no solo la finalización de las pruebas:

- 13 de valores pendientes, cancelación, reintentos independientes y 1812 variaciones de perfiles.
- 11 de reanudación, rechazo de observaciones anteriores a la interrupción, cancelación del apagado y conservación de supervisores.
- 17 de cancelación previa al envío cTGP, transferencia, presentación en Manual y guardado con aplicación parcial, incluyendo Automático activo.
- 8 de restauración: borrador conservado, valores guardados y reglas de inicio suprimidas al reiniciar sesión.
- 6 de condiciones de reanudación y avisos de batería.
- 12 de valores y reintentos de funciones vinculadas en Manual y Automático.
- 5 controles de vigencia de publicaciones cTGP; no se encontró un defecto de caducidad.
- 2 de guardado de la base externa, fallo posterior de conexión y Descartar coherente.

Las pruebas adicionales de `--independent-repairs-self-test` y `--function-rules-self-test` comprueban reparaciones anteriores, aplicación/guardado, navegación y distribución fuera de pantalla. Los supervisores conservan las barreras ante incertidumbre nativa y el cierre global al salir.

## Límites

La validación de software no demuestra el comportamiento físico de ventiladores, límites de potencia, micrófono, enlace HyperX o base ESP32. Un fallo simulado del receptor no demuestra la causa de los chasquidos originales. Las pruebas de instalación aisladas comprueban extracción, versión, integridad y publicación; la instalación real y sus ajustes se verifican por separado al entregar el paquete oficial.
