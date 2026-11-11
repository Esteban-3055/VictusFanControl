# VictusFanControl 1.13.6 preliminar

- El guardián cierra el acceso a nuevas órdenes si una comprobación periódica de cTGP tarda más de cinco segundos. Espera la finalización real de WMI antes de restaurar el control.
- La recuperación y el reinicio de sesión pueden volver a comprobar una lectura WMI que terminó tarde. Si la lectura sigue pendiente o quedan registros sin resolver, el control continúa bloqueado.
- El guardián restaura los ventiladores y registra el resultado aunque Windows impida reemplazar el archivo de estado de cTGP.

Las pruebas simulan una lectura tardía, reintentos antes y después de su retorno y un archivo de estado ocupado. La latencia del proveedor HP/WMI en el equipo sigue siendo variable.
