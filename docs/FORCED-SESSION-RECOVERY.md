# Recuperación forzosa de sesión

**Configuración → Registros y estado → Recuperación forzosa de sesión** permite
cerrar la sesión actual y abrir otra dentro de la misma ventana, aunque el
inventario no muestre registros pendientes. Es una acción manual con confirmación.

1. Reserva una sola tarea de recuperación y guarda un diagnóstico previo.
2. Cancela la reanudación automática, bloquea nuevas órdenes y espera el cierre
   del controlador, telemetría, límites CPU/GPU y procesos propios.
3. Comprueba que no quedan registros pendientes. Esta acción no selecciona ni
   recupera registros de otros propietarios; para ello se conserva el flujo
   explícito **Recuperar sesiones**, con identidad, respaldos y hashes.
4. Sólo tras confirmar la liberación abre un runtime nuevo en **Firmware**.
   Conserva perfiles guardados y cambios sin guardar. Automático requiere otra
   acción del usuario; su preferencia de inicio no lo reactiva por esta recuperación.

Si falla el diagnóstico previo, no empieza el cierre. Si falla la liberación,
cambian las condiciones de energía o quedan registros, no abre otro controlador.
La monitorización de recuperación sigue disponible. Un cierre no confirmado se
muestra con autoridad desconocida, sin afirmar propiedad física de Firmware.

## Recuperación del marcador sin reiniciar Windows

Cada operación nativa recibe un identificador único en `native-inflight.json`.
El proceso conserva una prueba privada en memoria únicamente después de que el
delegado nativo retorne, o si el journal de despacho falla antes de invocarlo.
La prueba contiene la ruta de sesión y los bytes exactos del marcador, incluido
identificador, PID e inicio del propietario. No se reconstruye desde un archivo
ni por el tiempo que haya transcurrido.

Si falla la retirada del marcador por uso compartido, se hacen como máximo cuatro
intentos con tres esperas de 25 ms. La adquisición se rechaza y su objeto de
respuesta se libera; no se publica como dato fresco. El siguiente despacho o el
drenaje de cierre puede reintentar esa retirada, siempre bajo el mutex nativo y
comparando los mismos bytes. Después se adquieren datos nuevos; nunca se repite
automáticamente el comando anterior ni se renueva una muestra antigua.

El propietario drena la prueba antes de avisar al guardián separado para que éste
no clasifique el marcador conocido como incierto. La parada cierra la admisión
antes de esperar al slot y se publica mientras se mantiene el mutex.

Una excepción nativa, un slot ocupado/abandonado, una marca de incertidumbre, un
marcador modificado o una sesión distinta no autorizan esta limpieza. Los registros
de versiones anteriores o de un propietario que murió no tienen esta prueba en
memoria. En esos casos puede seguir siendo necesario un arranque nuevo de Windows
verificado por el recuperador. La opción no termina procesos nativos a la fuerza,
no reinicia servicios/controladores del sistema y no borra leases inciertos.

## Verificación

Pruebas remotas de Windows, sin hardware: bloqueo real de archivos tras retorno,
reintentos acotados, liberación de respuesta rechazada, limpieza antes de nueva
lectura y antes del guardián, identidad de operación/sesión, negativa ante marcas
antiguas/modificadas/inciertas y fallo de journal antes del despacho. Los fixtures
WinForms verifican diagnóstico previo, tarea única, conservación del borrador,
runtime nuevo sin Automático y rechazo ante cierre incierto, suspensión, fallo
de diagnóstico o registros pendientes. Se exporta una captura de Configuración.

Se mantienen la serialización WMI, frescura original, publicación tardía aislada,
contratos HP de ventiladores internos y prohibición de acceso EC. Esta mejora no
cambia firmware ESP32, HMAC, credenciales ni la curva externa.
