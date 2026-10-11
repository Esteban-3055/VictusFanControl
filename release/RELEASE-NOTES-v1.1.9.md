# VictusFanControl v1.1.9 · estable

- **Recuperación automática de sesiones en segundo plano**, también al iniciar y mientras la aplicación está minimizada. Espera diez segundos con registros válidos y estables, libera el controlador anterior y conserva los respaldos.
- Después de una recuperación confirmada, vuelve a Automático con el último perfil aplicado y tres adquisiciones nuevas completas cuando corresponde. Respeta las preferencias de inicio y reanudación; una orden de Firmware, suspensión o cierre cancela la reactivación.
- Nueva opción **Recuperar sesiones automáticamente** en Configuración, activada por defecto. Guarda las preferencias para conservar cambios. La recuperación manual continúa disponible y termina en Firmware.
- Un único intento automático por inventario y hasta tres episodios antes de un minuto continuo de Automático sano. Un fallo o reset no confirmado deja aviso y no repite continuamente la operación.
- Diagnósticos CPU con contadores de adquisiciones frescas, lecturas suavizadas y bypass extremo. Se conserva la confirmación de **2 s** y la respuesta inmediata desde **99 °C**.
- La retención opcional **TZ01/DTT3** muestra su aporte y el motivo de retener o dejar continuar la curva base. Conserva el máximo de **+2 niveles crudos / 60 s** y sigue desactivada por defecto.

Conserva curvas, perfiles, preferencias, el ajuste de resolución y el actualizador estable. La supervisión requiere que el proceso de la aplicación siga vivo. Los registros inválidos y las llamadas nativas inciertas mantienen los bloqueos existentes y pueden requerir reiniciar Windows; la recuperación no fuerza cierres.

Validación: fixtures de recuperación e interfaz en Windows, suspensión/salida, cancelación, arranque y prueba prolongada de recursos. Los 84 escenarios de reproducción térmica mantienen sus resultados anteriores; la traza de 390 decisiones conserva el CSV y el resumen. Esto no acredita una nueva prueba física de refrigeración o ruido. El reset GPU mantiene el requisito de uso exclusivo y los límites de verificación NVML existentes.

Detalle: [recuperación automática](../docs/AUTOMATIC_SESSION_RECOVERY.md).
