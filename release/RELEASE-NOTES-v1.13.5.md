# VictusFanControl 1.13.5 preliminar

Corrige un bloqueo de la recuperación cuando el guardián WMI conserva abierto su registro de control. El inventario puede leer ese registro mientras el guardián sigue trabajando y distinguirlo de uno inválido. La recuperación sigue comprobando el propietario, la finalización de llamadas y la liberación confirmada.

El guardián reutiliza su conexión HP/WMI al liberar los ventiladores y devolver los ajustes al firmware. Esto evita repetir la búsqueda de la instancia WMI durante la restauración.

La prueba de inventario reproduce el registro abierto por un guardián. Verifica lectura, exclusión de borrado mientras el propietario lo mantiene abierto y retirada al cerrar el propietario. La integración de recuperación y cTGP se prueba con datos simulados.

El retraso de telemetría que inició la restauración del 9 de noviembre y la falta temporal del menú de bandeja requieren observación adicional en el equipo.
