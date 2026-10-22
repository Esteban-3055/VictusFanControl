# Actualización detenida por una liberación WMI ya completada

## Observación de la instalación actual

La instancia instalada seguía ejecutando 1.3.0. La descarga de 1.3.1 estaba completa: 168725522 bytes y SHA-256 `844b4adb1cbebf5d284671e767cb969af1a7988c152679844b40a46c8c52c071`, iguales a los metadatos públicos. No era un fallo de descarga o integridad.

Los registros mostraban un fallo anterior al liberar ventiladores, seguido de intentos de recuperación y reinicio de sesión que volvían a encontrar la misma excepción. El supervisor WMI ya había terminado y dejado un informe: ambas solicitudes aceptadas, lease retirado, sin atribuir propiedad física independiente al firmware. El backend conservaba `_reentryBlocked` y rechazaba inmediatamente cualquier comprobación posterior. Por ello `DisposeAsync` fallaba y no se habilitaba el traspaso seguro al recuperador de GPU.

Además, el Guardian de rendimiento había terminado dejando el journal exacto de GPU en `ReleaseWriteArmed`, CPU liberada y fuente detenida, con recibo de traspaso. El reset había devuelto `NVML_999`. Ese error no demuestra un reset completado; el journal debe conservarse y el recuperador exclusivo debe confirmar su propio resultado. No se atribuye el origen del error NVIDIA al fallo de coordinación WMI.

La inspección fue de solo lectura: no quedaron leases WMI, marcadores nativos pendientes o procesos supervisores en las comprobaciones realizadas. La GUI seguía abierta. Estos datos describen ese momento y no autorizan matar procesos por nombre en otro caso.

## Corrección del código

- Una liberación WMI previamente fallida puede comprobar el resultado tardío del mismo supervisor, después de drenar la adquisición local. Esta vía no solicita otra liberación, no repite el fallback y no envía comandos WMI.
- Se conserva el objeto del proceso original: debe haber terminado realmente con código cero. No basta la ausencia de un PID ni encontrar un informe.
- Se valida esquema, identidad del propietario y su hora de inicio, equipo, prohibición de EC, ausencia de fallo, lease retirado y aceptación de ambas solicitudes cuando existe intención de escritura. Un marcador nativo pendiente o incierto impide confirmar.
- Solo ese resultado permite completar el cierre del backend. La confirmación WMI sigue sin representar una lectura física independiente de propiedad del firmware. El traspaso GPU existente mantiene sus comprobaciones de journal, identidad y recuperación exclusiva.
- El objeto del proceso se conserva si falla el rearme de la frontera, para no perder la prueba de salida antes de completar la transición.

Esta corrección se encuentra en la rama de trabajo posterior a 1.3.1; **no forma parte del instalador 1.3.1 publicado**. No se reemplaza una release existente.

## Validación

- Compilación Release: cero errores y advertencias.
- Backend simulado: error inicial, rechazo mientras el resultado sigue incierto y confirmación tardía durante la disposición. Se comprueba que no se repiten solicitudes nativas ni liberaciones.
- Supervisor separado real, sin hardware: espera de liberación cancelada mientras sigue vivo; confirmación rechazada hasta su salida; resultado tardío aceptado. Diez alteraciones de informe, lease conservado y ambos marcadores nativos se rechazan.
- Regresiones aisladas de recuperación y actualización: selección exacta, errores guardados, drenaje, conservación de descarga, verificación antes de recuperar, suspensión/cierre y rechazo de estados inciertos. PASS.

Estas pruebas no equivalen a una recuperación física de la GPU ni a una instalación completada.

## Salida de la instancia 1.3.0

1. Guardar los cambios de preferencias que se quieran conservar y usar **Salir** desde la bandeja. La X de la ventana solo la oculta. El cierre conserva los journals aunque la liberación anterior no se haya confirmado.
2. Abrir manualmente el instalador 1.3.1 ya verificado y elegir **Instalar / actualizar** con la misma cuenta de Windows.
3. Si el instalador encuentra la sesión pendiente, usar su recuperación con respaldo. Solo continuar si confirma el resultado. Si el controlador vuelve a rechazar la operación, conservar el informe y el journal para investigar ese fallo concreto.

No se reinicia Windows, no se fuerza el cierre de mutadores y no se eliminan registros para habilitar una instalación.
