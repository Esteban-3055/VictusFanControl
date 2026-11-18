# VictusFanControl 1.14.1 preliminar

- El guardián cuenta el intervalo de observación de cTGP desde el inicio de la comprobación. Dos llamadas lentas consecutivas ya no acumulan una espera adicional de dos segundos tras cada retorno.
- La pérdida temporal de confirmación de cTGP libera el control y conserva la intención de reanudar Automático después de confirmar el cierre completo del propietario. Los errores explícitos de política mantienen su bloqueo de reactivación.
- Una publicación antigua o un error transitorio de lectura conservan la última observación válida hasta su vencimiento original. Las respuestas de cTGP comprueban fecha, estado activo y propiedad antes de confirmar una selección.
- Reintentar Automático permite verificar una lectura WMI que regresó tarde. Un reintento que termina de forma inmediata ya no bloquea la siguiente interrupción.
- El diagnóstico de una interrupción incluye la observación cTGP disponible, su edad, el error de lectura y la necesidad de intervención manual.

Se comprobaron llamadas lentas consecutivas, respuestas caducadas o incoherentes, devolución tardía de WMI, dos reintentos seguidos y reanudación tras una liberación confirmada. Una observación vencida sigue impidiendo nuevas órdenes de control. Las pruebas usan controles simulados y no constituyen una prueba física prolongada del portátil.
