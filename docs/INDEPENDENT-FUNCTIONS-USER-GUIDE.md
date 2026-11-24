# Uso de las funciones independientes

Guía de VictusFanControl **1.15.1 preliminar**, preparada en `feature/independent-functions`. La validación de software no sustituye las observaciones físicas indicadas en las notas de la versión.

## Dos decisiones diferentes

**Ventiladores** conserva Firmware, Manual y Automático. **Funciones**, justo debajo de Rendimiento, controla CPU, GPU, cTGP y la base externa por separado. Firmware devuelve los ventiladores internos al sistema; no significa apagar todas las funciones.

Selecciona la función a la izquierda y utiliza sus botones de acción en el panel derecho. **Encender / Apagar** solicita una acción de inmediato. La base usa **Controlar desde el PC / Volver a control local**. El texto de estado expresa la confirmación recibida. «Encendido solicitado» puede coexistir con una espera: seleccionar una función no prueba que esté activa. Un fallo conserva su explicación y puede exigir recuperación antes de adquirir de nuevo el controlador.

CPU/GPU usan los valores de sus perfiles en Rendimiento. Sus botones de Funciones afectan solo al canal elegido. Cambiar AC/Batería en el editor no cambia la alimentación real. PL1 puede alcanzar el PL2 elegido; PL2 conserva sus límites de configuración existentes.

## Elegir la coordinación

**Reglas de encendido** abre las opciones dentro del propio panel, con una explicación de cada comportamiento. Elegir una opción edita un borrador; puedes cancelar con Escape o cerrar el selector sin modificarlo. **Aplicar y guardar reglas** cambia la coordinación de esta sesión y la conserva para otros inicios. Solo guarda estas reglas: los cambios pendientes de curvas o valores de Rendimiento conservan sus propias acciones. El estado distingue «sin aplicar» de «sin guardar». Guardar una regla no equivale a confirmar una escritura de hardware: si una función no puede aplicarla, se indica por separado. Un cambio de vínculo puede retirar una función que ya no corresponde al modo actual.

| Opción por función | Al entrar en el modo | Al salir del modo |
|---|---|---|
| Sin vínculo | Se usa Encender / Apagar | Conserva la solicitud |
| Iniciar con Automático | Enciende la selección una vez | Conserva la solicitud |
| Seguir Automático | Usa la selección al entrar | Retira el control; conserva la intención |
| Iniciar con ventiladores | Usa la selección al entrar en Manual/Automático | Conserva la solicitud |
| Seguir ventiladores | Usa la selección al entrar en Manual/Automático | En Firmware retira el control |

La opción elegida activa la selección para inicio vinculado; **Avanzado: fallos y compatibilidad** permite personalizarla. No se repite en cada lectura: después se puede apagar CPU o GPU sin que Automático vuelva a encenderlos. **Iniciar con la aplicación** es una preferencia distinta, desactivada por defecto. Reanudar Windows o recuperar ventiladores conserva las decisiones de la sesión y vuelve a comprobar las condiciones necesarias. **Reiniciar sesión** abre el control en Firmware, conserva el borrador para editar y no vuelve a ejecutar las reglas de inicio.

- **Comportamiento anterior:** CPU/GPU se inician con Automático y pueden continuar después; cTGP lo sigue; la base sigue los ventiladores internos aceptados. Conserva las selecciones heredadas y el acoplamiento de fallos anterior.
- **Independientes:** conserva los atajos seleccionados, permite continuar después de perder Automático y conserva otras solicitudes independientes ya encendidas. Selecciona curva propia por temperatura para la base y separa sus fallos de Automático.
- **Sin vínculos:** cada función usa sus botones y su preferencia de inicio, sin encenderse al cambiar el modo de ventiladores. También selecciona curva propia para la base.

Se puede ajustar cada vínculo después de elegir un preset. Las opciones «Fallo CPU/GPU detiene Automático» y «Fallo cTGP detiene Automático» conservan como elección el acoplamiento anterior. Separarlas no elimina las comprobaciones térmicas, de identidad, alimentación o datos vigentes.

## Base externa: conexión y salida

**Base externa** configura conexión, emparejamiento, monitorización y diagnóstico. **Curva Externa** edita su respuesta. **Funciones** solicita el control del actuador y elige su fuente:

| Fuente de salida | Requisito | Qué significa |
|---|---|---|
| Seguir nivel interno | Orden interna aceptada y vigente | Conserva el funcionamiento anterior, también en Manual |
| Velocidad fija | Enlace vigente y firmware compatible | Se configura como porcentaje de 0 a 100 %, convertido internamente a 0–255, sin necesitar RPM internas |
| Curva por temperatura | Enlace compatible y temperaturas propias completas/vigentes | Usa el máximo de CPU normalizada 40–90 °C y GPU 35–81 °C, convertido a 0–100 % |

**Velocidad fija 0 % solicita parada. Volver a control local libera el control remoto:** el dispositivo vuelve a su política local, actualmente 130/255; no se debe interpretar como una orden de parada. El cambio de modo y de salida fija es un borrador hasta aplicar y guardar reglas.

La conexión/LCD puede continuar sin control del actuador. Las modalidades independientes exigen que el ESP32 anuncie la capacidad correspondiente. Con firmware anterior se conserva la monitorización y el seguimiento compatible; no se simula una orden interna para aceptar una modalidad nueva. Esta rama no se ha grabado en la placa del usuario.

## Esperas, batería y recuperación

cTGP mantiene la política HP calificada y comprueba el cargador HP de 200 W en cada adquisición. En batería restaura esa política y conserva la solicitud para recalificarla al volver a AC. CPU/GPU comprueban su propia fuente y sensores; una retirada no exige RPM de ventiladores. Los plazos están en [SENSOR-TIMING-AUDIT.md](SENSOR-TIMING-AUDIT.md).

Una consulta WMI ocupada antes de enviarse puede esperar y reintentarse sin declarar un fallo definitivo. Una operación ya enviada cuyo resultado se desconoce bloquea nuevas órdenes de ese recurso hasta comprobar su finalización. Más espera no convierte un dato antiguo en reciente. Salir y las suspensiones conservan la liberación y supervisión correspondientes; el reintento local de ventiladores no sustituye el cierre global.

La bandeja usa los nuevos iconos: normal, Automático, Automático con micrófono silenciado y fallo. El silencio se representa solo con Automático activo. El fallo tiene prioridad. El giro dibujado es una animación basada en RPM vigentes; no mide vueltas físicas ni genera consultas adicionales.
