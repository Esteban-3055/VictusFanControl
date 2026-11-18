# Revisión de coordinación: arranque, reentrada y comunicación

Revisión del 19 de octubre de 2026 sobre `feature/external-curve-firmware`,
partiendo de `3ece142`. Las correcciones siguientes aún no están instaladas en el
portátil ni cargadas en el ESP32. No cambian la tarea de inicio ni las preferencias
del usuario. No se incluyen diagnósticos privados ni credenciales en el repositorio.

## Defectos y correcciones

| Caso | Defecto encontrado | Comportamiento corregido y prueba |
|---|---|---|
| Arranque automático | Volver a comprobar la misma adquisición después de caducar sus RPM borraba las adquisiciones buenas ya contadas. El temporizador de interfaz podía impedir llegar a tres. | Una repetición caducada no cuenta ni admite, pero conserva el progreso. Una adquisición nueva inválida, cambio de fuente/estado o hueco superior a 3 s siguen reiniciándolo. Regresión en las tres fases: inicio, reintento manual y reanudación. |
| Lecturas durante la espera inicial | El temporizador podía observar repetidamente la misma lectura de ventiladores almacenada. | Solicita una lectura fresca en la siguiente adquisición del bucle existente; no abre un segundo lector ni acelera el bucle. |
| Entrada a Automático | Esperar una cola o la preparación CPU/GPU podía caducar la telemetría entre comprobación y admisión. | Adquiere datos frescos bajo la misma exclusión del lector antes de ambas etapas de preparación y, si caducaron, antes de confirmar. Comprueba generación, energía, recuperación y vigencia de nuevo. Mantiene RPM <3 s y las barreras de seguridad. |
| Estado mostrado y cambios de perfil | Dos publicaciones concurrentes podían alcanzar la interfaz en orden inverso. Un estado antiguo podía sustituir una fuente nueva o esconder el bloqueo reciente. | Secuencia creciente de publicación y rechazo de estados anteriores o de otro controlador. La telemetría también rechaza adquisiciones anteriores; una corrección del reloj no deja una muestra futura fijada. Fixtures verifican fuente y bloqueo. |
| Actualización de límites CPU/GPU | La pausa y el plazo de actualización comenzaban antes de obtener el canal IPC, mientras aún se esperaba una consulta STATUS. | Primero obtiene el canal y valida; después admite la pausa y comienza el envío con plazo de 4 s. Fallar antes de esa admisión conserva la sesión anterior. Una respuesta incierta después de admitir mantiene la liberación y los journals existentes. Fixture con canal ocupado y cancelación previa. |
| Reconexión ESP32 | Una sesión TCP nueva olvidaba el contenido de la revisión anterior. Podía aceptar el mismo identificador con otra curva; el controlador ignoraba ese contenido y podía devolver una revisión falsamente coincidente. | Conserva la curva validada entre sesiones y desde la misma NVS. Rechaza revisión reutilizada con contenido diferente antes de confirmarla; acepta la misma curva y revisiones nuevas. No conserva autenticación, secuencias ni autoridad Remote. Regresión C++ reproduce el rechazo ausente antes de corregirlo. |
| Indicador externo | El texto «Aplicado» admitía respuestas futuras o más antiguas que las aceptadas por el punto de la curva. | Ambos vencen a los 2 s y rechazan fechas futuras. Fixture comprueba coherencia entre texto y marcadores. Se corrigen también textos con codificación incorrecta. |
| Prueba de integración CPU/GPU | Un fixture consultaba el inventario real de recuperación del equipo, fallando por permisos o estado ajeno a la prueba. | Inyecta un inventario vacío solo en el fixture. Producción conserva el inventario real y su bloqueo. |

## Evidencia del arranque observado

En la instancia instalada 1.2.16, la tarea sí abrió el programa minimizado a las
15:53:10 (UTC−03). Se observó recuperación de sesiones y telemetría Healthy. La
entrada posterior falló con RPM de unos 3436 ms, frente al límite de 3000 ms.
Automático terminó activándose a las 16:01:09 sin abrir la ventana ni cambiar la
tarea. No fue una ausencia de lanzamiento del ejecutable.

La reproducción usa 146 adquisiciones capturadas y únicamente la clase pura de
admisión. Con los mismos datos y comprobaciones cada 250 ms:

| Reproducción | Solo adquisiciones | Adquisiciones y comprobaciones intermedias |
|---|---:|---:|
| Antes | Admite en adquisición 4 | Admite en adquisición 135 |
| Después | Admite en adquisición 4 | Admite en adquisición 4 |

Esto confirma el defecto de conteo; no demuestra que explique cada segundo de la
demora real. Los retrasos de HP WMI observados tampoco prueban el origen de un
cambio de alimentación informado por Windows. El diagnóstico local se conserva
fuera del repositorio.

## AC/batería y recuperación: contratos revisados

El reconciliador existente conserva el plazo global de 8 s, respuesta nueva por
candidato, dos observaciones y estabilidad mínima. Los fixtures cubren arranque
en batería, vuelta a AC, recorridos en ambos sentidos, rebotes, respuestas viejas,
reloj regresivo y vencimiento. En esta revisión no se encontró otro defecto
reproducible en ese reconciliador y no se amplían sus plazos. La corrección de
publicaciones evita que la interfaz aplique estados fuera de orden; no prueba una
transición física AC/batería.

Reintento y reanudación comparten la corrección de las tres adquisiciones. Una
recuperación pendiente, llamada nativa incierta, liberación sin confirmar o fallo
real de CPU/GPU sigue bloqueando nuevas órdenes. El drenaje de un proveedor nativo
que nunca devuelve continúa siendo una limitación: no se declara recuperación
universal sin reiniciar, ni se matan proveedores o borran journals para simularla.

## Validación de software

- Solución Windows Release: cero advertencias/errores, advertencias como errores.
- Los 19 grupos de `scripts/test-external-cooling.ps1`: PASS, incluida GUI,
  coordinación de telemetría, actualización IPC y recuperación con fixtures.
- Prueba pura `--automatic-coordination-self-test`: fallo reproducido antes,
  PASS después; también integrada en `--product-gui-self-test`.
- Lecturas de admisión: exclusión entre solicitudes, cancelación, suspensión y
  rechazo de muestras caducadas, futuras, incompletas o retenidas. Sin hardware.
- C++ con `-Wall -Wextra -Werror`, 35 vectores de protocolo, controlador real,
  configuración hostil, reconexión y presentación/exportación canónica: PASS.
- Cinco sesiones cliente Windows real ↔ parser/controlador C++: PASS en parámetros,
  revisión aplicada, autenticación y retirada de Remote. Transporte local simulado.
- PlatformIO `supermini` y `supermini-bench`: PASS. No se programó la placa.
- Arduino-ESP32 3.3.2 y ArduinoJson 6.21.5 se verifican en el job `arduino3` del
  workflow `esp32-firmware`. El core fijado no está instalado localmente; comprobar
  el resultado del commit en CI antes de distribuir sus imágenes.

Los logs locales de esta revisión llevan el prefijo `output/coordination-` fuera
del checkout. Las pruebas de software no certifican arranque físico de Windows,
reanudación real, RPM reales, LCD, NTC, PWM ni circuito de potencia.

## Contratos externos preservados

GPIO3 PWM invertido 18 Hz/14 bits, GPIO0 NTC, GPIO6/7 I2C, Wi-Fi 8,5 dBm,
HMAC TCP 8765 y estructura/espacio NVS permanecen iguales. La retención de revisión
no modifica el protocolo ni persiste claves nuevas. Sin solicitud vigente, Local
converge por la rampa a 130; NTC sigue siendo informativa. Windows envía la curva
y los parámetros; ESP32 ejecuta EMA, histéresis, confirmación y rampas. Es un motor
distinto del control de ventiladores internos HP. ExternalCooling sigue leyendo
snapshots y no adquiere WMI, EC ni autoridad de control interno.
