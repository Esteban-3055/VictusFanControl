# Uso de las funciones independientes

Guía de VictusFanControl **1.15.0 preliminar**, preparada en `feature/independent-functions`. La validación de software no sustituye las observaciones físicas indicadas en las notas de la versión.

## Dos decisiones diferentes

**Ventiladores** conserva Firmware, Manual y Automático. **Funciones**, justo debajo de Rendimiento, controla CPU, GPU, cTGP y la base externa por separado. Firmware devuelve los ventiladores internos al sistema; no significa apagar todas las funciones.

En cada fila, **Encender / Apagar** solicita una acción de inmediato. El texto de estado expresa la confirmación recibida. «Encendido solicitado» puede coexistir con una espera: seleccionar una función no prueba que esté activa. Un fallo conserva su explicación y puede exigir recuperación antes de adquirir de nuevo el controlador.

CPU/GPU usan los valores de sus perfiles en Rendimiento. Sus botones de Funciones afectan solo al canal elegido. Cambiar AC/Batería en el editor no cambia la alimentación real. PL1 puede alcanzar el PL2 elegido; PL2 conserva sus límites de configuración existentes.

## Elegir la coordinación

Los botones de vínculo y los presets editan preferencias. **Aplicar vínculos** cambia la coordinación de esta sesión; **Guardar preferencias** la conserva para otros inicios. Ninguno equivale a confirmar una escritura de hardware. Un cambio de vínculo puede retirar una función que ya no corresponde al modo actual.

| Opción por función | Al entrar en el modo | Al salir del modo |
|---|---|---|
| Sin vínculo | Se usa Encender / Apagar | Conserva la solicitud |
| Iniciar con Automático | Enciende la selección una vez | Conserva la solicitud |
| Seguir Automático | Usa la selección al entrar | Retira el control; conserva la intención |
| Iniciar con ventiladores | Usa la selección al entrar en Manual/Automático | Conserva la solicitud |
| Seguir ventiladores | Usa la selección al entrar en Manual/Automático | En Firmware retira el control |

**Seleccionar para inicio vinculado** elige qué encender en esa entrada. No se repite en cada lectura: después se puede apagar CPU o GPU sin que Automático vuelva a encenderlos. **Iniciar con la aplicación** es una preferencia distinta, desactivada por defecto. Reanudar Windows o recuperar ventiladores conserva las decisiones de la sesión y vuelve a comprobar las condiciones necesarias.

- **Comportamiento anterior:** CPU/GPU se inician con Automático y pueden continuar después; cTGP lo sigue; la base sigue los ventiladores internos aceptados. Conserva las selecciones heredadas y el acoplamiento de fallos anterior.
- **Independientes:** conserva los atajos seleccionados, permite continuar después de perder Automático y conserva otras solicitudes independientes ya encendidas. Selecciona curva propia por temperatura para la base y separa sus fallos de Automático.
- **Sin vínculos:** cada función usa sus botones y su preferencia de inicio, sin encenderse al cambiar el modo de ventiladores. También selecciona curva propia para la base.

Se puede ajustar cada vínculo después de elegir un preset. Las opciones «Fallo CPU/GPU detiene Automático» y «Fallo cTGP detiene Automático» conservan como elección el acoplamiento anterior. Separarlas no elimina las comprobaciones térmicas, de identidad, alimentación o datos vigentes.

## Base externa: conexión y salida

**Base externa** configura conexión, emparejamiento, monitorización y diagnóstico. **Curva Externa** edita su respuesta. **Funciones** solicita el control del actuador y elige su fuente:

| Fuente de salida | Requisito | Qué significa |
|---|---|---|
| Seguir nivel interno | Orden interna aceptada y vigente | Conserva el funcionamiento anterior, también en Manual |
| Salida fija | Enlace vigente y firmware compatible | Solicita un valor lógico de 0 a 255 sin necesitar RPM internas |
| Curva por temperatura | Enlace compatible y temperaturas propias completas/vigentes | Usa el máximo de CPU normalizada 40–90 °C y GPU 35–81 °C, convertido a 0–100 % |

**Salida fija 0 solicita parada. Apagar libera el control remoto:** el dispositivo vuelve a su política local, actualmente 130/255; no se debe interpretar como una orden de parada. El cambio de modo y de salida fija es un borrador hasta aplicar los vínculos.

La conexión/LCD puede continuar sin control del actuador. Las modalidades independientes exigen que el ESP32 anuncie la capacidad correspondiente. Con firmware anterior se conserva la monitorización y el seguimiento compatible; no se simula una orden interna para aceptar una modalidad nueva. Esta rama no se ha grabado en la placa del usuario.

## Esperas, batería y recuperación

cTGP mantiene la política HP calificada y comprueba el cargador HP de 200 W en cada adquisición. En batería restaura esa política y conserva la solicitud para recalificarla al volver a AC. CPU/GPU comprueban su propia fuente y sensores; una retirada no exige RPM de ventiladores. Los plazos están en [SENSOR-TIMING-AUDIT.md](SENSOR-TIMING-AUDIT.md).

Una consulta WMI ocupada antes de enviarse puede esperar y reintentarse sin declarar un fallo definitivo. Una operación ya enviada cuyo resultado se desconoce bloquea nuevas órdenes de ese recurso hasta comprobar su finalización. Más espera no convierte un dato antiguo en reciente. Salir y las suspensiones conservan la liberación y supervisión correspondientes; el reintento local de ventiladores no sustituye el cierre global.

La bandeja usa los nuevos iconos: normal, Automático, Automático con micrófono silenciado y fallo. El silencio se representa solo con Automático activo. El fallo tiene prioridad. El giro dibujado es una animación basada en RPM vigentes; no mide vueltas físicas ni genera consultas adicionales.
