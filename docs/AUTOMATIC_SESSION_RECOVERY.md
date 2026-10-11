# Recuperación automática de sesiones (desarrollo sin nueva release)

La GUI supervisa los registros pendientes también al iniciar y mientras está
minimizada. La preferencia `recoverSessionsAutomatically`, activada por defecto,
se edita en Configuración y se conserva al guardar. No reinicia una GUI muerta:
la supervisión necesita que su proceso siga vivo.

Un inventario válido y estable durante diez segundos permite una recuperación.
La existencia de un journal activo no basta: las sesiones Automático activas se
excluyen y el inventario inicial ignora los leases propios vigentes. Antes de
recuperar se bloquea y libera el runtime anterior. Los identificadores y hashes
se comparan otra vez después de la liberación. Las operaciones de lectura,
liberación y recuperación se esperan sin bloquear el hilo de presentación.

Se reutilizan los recuperadores de ventiladores y Performance Guardian:
respaldos durables, identidad exacta, mutex, exclusión de procesos anteriores,
restauración CPU solo de campos todavía propios, reset GPU y verificación del
informe y ausencia de registros. No se matan procesos ni llamadas nativas. La
aceptación del reset NVML no acredita lectura independiente del rango GPU ni
detecta universalmente otros programas que lo modifiquen; se mantiene el
requisito de uso exclusivo del controlador de frecuencias GPU.

Cada inventario tiene un único intento automático. Un reset no confirmado no
se repite. Hasta tres inventarios distintos pueden recuperarse antes de agotar
el presupuesto; un minuto continuo de Automático sano lo renueva. Los registros
inválidos, propiedad desconocida y marcas WMI inciertas conservan los bloqueos
del recuperador; pueden requerir Reiniciar Windows. Un fallo deja aviso visible
y notificación de bandeja, sin cuadro modal ni nueva activación.

Tras éxito, se crea un runtime nuevo en Firmware. La vuelta a Automático usa
el perfil aplicado anterior, no el borrador, y requiere la preferencia de
reanudación y tres adquisiciones nuevas completas, con admisión de temperatura
y límites CPU/GPU. Al iniciar se respeta la preferencia guardada de activar
Automático. La recuperación manual sigue terminando en Firmware. Una orden de
Firmware durante la recuperación cancela la reanudación; suspensión y cierre
impiden abrir otra sesión y esperan la operación pendiente antes de salir.

El filtro CPU conserva los dos segundos y el bypass extremo de 99 °C. Sus
diagnósticos añaden adquisiciones frescas, adquisiciones suprimidas y bypasses;
son contadores desde el último reinicio del filtro, no de episodios térmicos.
Las muestras retenidas no incrementan los contadores.

La retención TZ01/DTT3 conserva +2 niveles crudos, techo del último nivel
aplicado, episodio máximo de 60 s y filtro compartido. Se hace observable su
aporte y motivo (retención, demanda base dominante, fuentes no vigentes,
ausencia de nivel aplicado o plazo agotado). No se aumentan tiempos ni se
deduce una ubicación física del sensor. Sigue experimental y opcional.

Los fixtures cubren espera/inventario/presupuesto, rechazo de sesiones activas,
liberación previa, cambios de registros, preferencias, borrador, arranque,
reentrada fresca, parada del usuario, fallo del recuperador, suspensión y salida.
Las pruebas no escriben hardware; la experiencia térmica y acústica futura
requiere observación física. No se cambia número de versión ni publicación.
