# VictusFanControl 1.3.16 preliminar

Corrige una carrera al publicar `heartbeat.json` cuando coinciden la supervisión de ventiladores y las consultas cTGP. Esa colisión podía iniciar la restauración de seguridad y cancelar el enfriamiento de apagado antes de completar sus 20 segundos.

La captura de la hora y el reemplazo atómico se serializan por sesión. No se ocultan errores de acceso, no se amplía la caducidad del supervisor y no se desactivan las protecciones de restauración. Las órdenes y registros de intención conservan su comportamiento.

Validación aislada con publicaciones concurrentes y lector, monotonía, fallo real de archivo, IPC cTGP y temporización de apagado sin apagar Windows. El apagado físico con esta corrección queda pendiente; no se afirma una garantía de RPM físicas.

Conserva los ajustes, las funciones de 1.3.15 y el firmware ESP32 existente. Preliminar optativa; no es una nueva calificación térmica del equipo.
