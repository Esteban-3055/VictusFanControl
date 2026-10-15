# VictusFanControl v1.2.9

- Retira las lecturas TZ01/DTT3 y la retención térmica experimental. CPU usa Intel MSR/PawnIO y GPU usa NVML; el programa de uso habitual prohíbe el acceso directo al EC.
- Comparte una única adquisición entre solicitudes de RPM de control que esperan antes de comenzar la llamada nativa. Conserva la exclusión de WMI, los límites de edad, la cancelación independiente y las protecciones térmicas.
- Añade al diagnóstico los solicitantes de RPM, las solicitudes combinadas y las adquisiciones nativas. Mantiene la frecuencia de muestreo existente.
- Aísla de los registros reales de Windows las pruebas de recuperación y actualización.

La verificación de software no establece la causa del cambio de alimentación observado ni sustituye la observación del equipo. La captura de WMI y la comparación bajo carga quedan para una etapa posterior.
