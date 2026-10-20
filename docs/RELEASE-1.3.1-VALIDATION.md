# 1.3.1: estado de inicio con Windows

## Defecto y causa comprobada

La 1.3.0 podía registrar correctamente la tarea y mostrar «Registro de inicio actualizado. Guarda la opción Automático…», pero dejar desmarcada «Iniciar con Windows». Guardar los perfiles no corregía la marca.

La consulta de la tarea real confirmó que estaba habilitada, apuntaba al ejecutable 1.3.0 instalado y permitía iniciar y continuar en batería. El Programador de tareas había conservado el SID en Principal/UserId, pero exportaba LogonTrigger/UserId como nombre de cuenta. Ambos correspondían a la misma identidad actual de Windows. La comparación literal nueva de 1.3.0 rechazaba ese formato equivalente.

Las pruebas anteriores comparaban XML generado por el programa con su validador y no cubrían esa normalización de Windows. El defecto se reprodujo con una copia de solo lectura del XML real: la comparación anterior devuelve false y la corregida true. La tarea original no se modificó para obtener el resultado. Sus datos privados permanecen fuera del repositorio y los artefactos públicos.

## Corrección

- Se acepta exclusivamente el SID de la identidad actual de Windows o su nombre canónico obtenido de esa misma identidad. No se resuelven nombres arbitrarios ni se acepta una coincidencia de nombre corto entre distintos dominios.
- Se conserva el contrato de ejecutable, argumentos, módulos, directorio, usuario, elevación, disparador, batería, límites de ejecución y terminación. La lectura posterior al clic usa también el directorio de módulos configurado.
- El mensaje confirma «Inicio con Windows activado» o «desactivado» solo después de leer el resultado esperado. No pide guardar Automático. Si falla la escritura, lectura o confirmación, el estado pasa a desconocido y se permite volver a consultarlo.
- La casilla de inicio y las preferencias de Automático/minimizado mantienen sus funciones independientes. Guardar preferencias actualiza el registro habilitado con la opción de minimizado correspondiente.

## Pruebas

- Copia exportada de la tarea real, sin volver a registrarla: rechazo anterior reproducido y reconocimiento corregido de la misma identidad. El montaje se conserva localmente; no publica usuario, SID o rutas personales.
- Cuatro combinaciones SID/cuenta para Principal y LogonTrigger, incluida variación de mayúsculas. Se rechazan otro dominio con el mismo nombre, otra cuenta, otro SID, nombres cortos ambiguos y valores vacíos.
- Flujo GUI aislado: activar, desactivar, guardar, conservar módulos personalizados y minimizado; errores de escritura, lectura y confirmación. No crea tareas reales ni envía órdenes a ventiladores/CPU/GPU.
- Se repiten los 19 grupos de regresión Windows y los seis controles del commit exacto, incluido empaquetado y compilación del firmware. El publicador verifica la descarga desde el actualizador original 1.2.9, también simulando una instalación 1.3.0.

El firmware y los controles térmicos no requieren cambios por esta corrección. Las observaciones físicas de arranque y cambio de alimentación siguen pendientes; una tarea habilitada no demuestra por sí sola un nuevo arranque real. No se necesita reiniciar Windows para corregir la interfaz: basta actualizar normalmente la aplicación.

Se conservan las correcciones y límites documentados en [1.3.0](RELEASE-1.3.0-VALIDATION.md).
