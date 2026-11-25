# Validación de 1.15.2 preliminar

La recuperación por función pasó las once suites locales, incluidos los casos
de CPU/GPU separados, rechazo parcial de actualización, cTGP tras recibo final
WMI, reintentos en cola frente a apagado y conservación de dominios sanos.
También pasaron 74 resultados de ciclo de vida, 1816 mutaciones de perfiles y
23040 combinaciones de admisión. Las pruebas usan procesos simulados y archivos
temporales, sin tomar el escritorio ni activar sensores o controles físicos.

La revisión del recibo WMI comprueba identidad exacta, terminación del proceso,
restauración y retirada de concesión; rechaza nueva incertidumbre nativa. La
restauración completada no se vuelve a enviar. Se conserva la política anterior
como preset explícito y los fallos no confirmados permanecen bloqueados para su
recurso. Véase LOCAL-FUNCTION-RECOVERY.md para los límites funcionales.

La instalación local precede a la publicación por petición del usuario. El
paquete conserva trazabilidad del commit, integridad por archivo y copias de
los ajustes. El estado de CI y la observación física se informan por separado.
No se afirma validación física de suspensión, ventiladores, límites o cTGP con
esta versión, ni se sustituye esa observación por pruebas de software.
