# VictusFanControl v1.2.15 · Preliminar 7 · Recuperación de sesión

Nueva preliminar para HP Victus 8C40 / BIOS F.18. Sustituye la preliminar 1.2.14;
la versión estable 1.2.9 permanece en el canal estable.

- Una sola opción **Recuperar sesión definitiva** sustituye Recuperar sesiones y
  Recuperación forzosa. Funciona con una sesión abierta o registros pendientes.
- Recuperación automática de fallos persistentes de telemetría incluso sin registros
  pendientes, con esperas de 15/30/60 s y límite de tres episodios. Conserva diagnóstico,
  perfiles y cambios sin guardar; abre una sesión nueva sólo tras liberar la anterior.
- Reanuda Automático únicamente con intención previa, permiso de la configuración y
  tres observaciones nuevas válidas. Una petición Firmware cancela esa reanudación.
  La recuperación manual finaliza en Firmware.
- Corrige el bloqueo del marcador WMI causado por lectores de diagnóstico y espera
  automáticamente los bloqueos transitorios de archivos tras un retorno confirmado.
- Las RPM retenidas se identifican como **Última RPM**; no autorizan control.
- Conserva curva externa 0–255, punto verde, LCD1602 y protecciones ESP32 de 1.2.14.

**Límite importante:** no se garantiza recuperación sin reiniciar Windows ante un
proveedor/controlador bloqueado permanentemente ni se pueden validar retrospectivamente
marcadores inciertos de 1.2.13. Se conservan esas barreras y no se reinicia Windows
ni se terminan procesos/servicios del sistema automáticamente. El nombre de la opción
no garantiza resolver cualquier fallo de hardware o firmware.

Validación: compilación, instalador y fixtures remotos de Windows sin operaciones
físicas, incluyendo recuperación y reanudación, incertidumbre, suspensión, archivos
bloqueados y aislamiento de resultados tardíos. No se ha instalado ni probado esta
preliminar sobre el hardware del usuario. La observación física sigue pendiente.

Instalar desde el actualizador activando versiones preliminares o mediante el EXE
adjunto. Requiere .NET Desktop Runtime 8 x64. Mantener la versión anterior para volver
atrás. No es necesario actualizar firmware ESP32 para estos cambios de recuperación.

[Detalles y límites de recuperación](https://github.com/Esteban-3055/VictusFanControl/blob/v1.2.15/docs/FORCED-SESSION-RECOVERY.md).
