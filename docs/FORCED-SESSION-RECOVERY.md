# Recuperar sesión definitiva

Disponible en Configuración → Registros y estado, Rendimiento → Guardián,
Actualizaciones cuando hay registros pendientes y el menú de la bandeja.
Sustituye la antigua recuperación manual y la opción forzosa separada.
Funciona aunque no haya registros pendientes.

## Proceso común

1. Captura los identificadores y hashes de los registros anteriores al cierre.
2. Guarda un diagnóstico previo y conserva perfiles y cambios sin guardar.
3. Cierra la admisión de órdenes, drena el runtime anterior y libera los controles
   de ventiladores, CPU y GPU. Nunca abre un segundo controlador antes del cierre.
4. Revisa el inventario restante: sólo puede recuperar el subconjunto exacto
   capturado. Cambios, rutas redirigidas, propietarios activos o registros inválidos
   detienen el proceso. El ayudante conserva sus verificaciones de identidad,
   exclusión WMI, respaldos y autorización de liberación, sin niveles normales.
5. Abre un runtime nuevo en Firmware dentro de la misma ventana. La recuperación
   manual deja Automático desactivado. Un fallo mantiene el monitor de recuperación.

La recuperación automática usa este mismo proceso. Está activada por defecto,
respetando la preferencia guardada. Además de registros pendientes estables durante
10 s, cubre un runtime Degraded, Failed o Recovering persistente sin inventario.
Espera 15 s en el primer episodio, 30 s en el segundo y 60 s en el tercero.
No actúa durante preparación, actualización de rendimiento, uso de revisión,
suspensión ni Automático activo sin interrupción. No ejecuta un bucle de cierres:
un cierre no confirmado queda enclavado; sólo una sesión nueva confirmada permite
otro episodio, hasta tres antes de un minuto de funcionamiento sano, también en
Firmware. Los reintentos de lectura de inventario anteriores mantienen sus límites.

Sólo reanuda Automático si existía intención previa, la preferencia lo permite,
el usuario no ha pedido Firmware y tres observaciones nuevas superan la admisión
existente. No usa RPM retenidas para autorizar control. Un reinicio automático de
sesión en Firmware no activa la preferencia de Automático al iniciar Windows.

## Recuperación de seguimiento WMI sin reiniciar Windows

Cada llamada recibe un identificador único. El propietario conserva una prueba
privada en memoria únicamente tras retornar el delegado nativo, o si el journal
falla antes de invocarlo. La prueba contiene carpeta y bytes exactos del marcador,
incluidos identificador, PID e inicio del proceso. Un archivo o su antigüedad no
permiten reconstruir esa prueba. Una respuesta nula o inválida no convierte una
llamada ya retornada en una llamada pendiente.

Las lecturas de diagnóstico permiten eliminar el marcador transitorio sin bloquear
su retirada. Si otra aplicación lo mantiene abierto, su retirada realiza cuatro
intentos con tres esperas de 25 ms. Rechaza y libera la respuesta de esa adquisición;
no la publica como fresca. Una siguiente lectura puede retirar el marcador conocido
antes de adquirir una muestra nueva, sin repetir el comando anterior.

El cierre de sesión espera automáticamente la desaparición de un bloqueo transitorio
sólo cuando tiene esa prueba de retorno, dentro del plazo total de 10 s. Mantiene
cerrada la admisión, usa el mismo mutex y avisa al guardián después de retirar el
marcador. No trata un plazo vencido como cancelación de firmware. Si el bloqueo no
cede, conserva el marcador y el estado de propiedad desconocida.

## Límite documentado: no existe garantía universal de recuperación

La aplicación nunca reinicia Windows automáticamente. Tampoco termina WmiPrvSE,
reinicia Winmgmt/controladores ni elimina marcadores inciertos como atajo.
Microsoft documenta que CancelAsyncCall en métodos cancela el extremo cliente:
el proveedor no recibe la cancelación y continúa ejecutándose. CancelIoEx tampoco
garantiza cancelación de un controlador. Un proceso aislado o terminado no demuestra
que una operación de firmware haya finalizado.

Si un proveedor/controlador queda bloqueado permanentemente, el proceso muere en
una llamada, surge una excepción nativa incierta, cambia el marcador o existen
marcas de 1.2.13 sin prueba de retorno, la recuperación conserva los registros y
no admite nuevas llamadas. Este paquete NO promete restaurar control en esos casos
sin reiniciar Windows. El mecanismo previo de verificación de arranque sigue
siendo una vía de recuperación si el usuario decide utilizarla; no se ejecuta ni
se fuerza desde esta opción. No se sustituye por acceso directo EC.

Fuentes primarias:
- https://learn.microsoft.com/en-us/windows/win32/api/wbemcli/nf-wbemcli-iwbemservices-cancelasynccall
- https://learn.microsoft.com/en-us/windows/win32/fileio/canceling-pending-i-o-operations

## Pruebas

La [revisión de coordinación posterior](AUTOMATIC-COORDINATION-REVIEW.md) corrige
el conteo compartido por inicio, reintento y reanudación, así como la caducidad
durante preparación. Conserva las barreras de recuperación descritas aquí.

Fixtures Windows sin acceso físico: bloqueo real de archivos después del retorno,
espera automática hasta liberación del lector, identidad exacta de llamada y sesión,
rechazo de marcadores antiguos/modificados/inciertos, excepción nativa sin prueba,
fallo de journal antes de despacho, respuesta rechazada liberada, sin repetición de
órdenes. La GUI verifica proceso único, inventario previo y subconjunto exacto,
diagnóstico antes del drenaje, recuperación automática de fallo sin registros,
conservación del borrador, tres observaciones nuevas, intención anterior, petición
Firmware, opción desactivada, suspensión y cierre incierto. Se exporta una captura.

Se conservan aislamiento CPU/GPU directo, exclusión WMI, frescura y barreras contra
publicación tardía. ESP32, HMAC, NVS, credenciales, GPIO, PWM y límites internos HP
no cambian. La validación física de esta recuperación sigue pendiente.
