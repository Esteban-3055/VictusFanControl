# Consignas nominales 54/54 — versión 1.3.15

Manual y Automático convierten el nivel lógico 10–54 en el mismo nivel para CPU y GPU. La solicitud HP WMI en 54 es `36 36 00 00` hexadecimal; no se usa acceso EC directo. Una unidad equivale nominalmente a 100 RPM. La lectura real HP WMI conserva su resolución y puede diferir de la consigna.

Las curvas guardadas no cambian: si terminan en 50, continúan solicitando hasta 50. El usuario puede editar el último punto hasta 54. Se mantienen las protecciones, el guardián y la liberación a Firmware existentes.

La evidencia histórica de CPU 54/GPU 52 permanece en FAN_LIMITS_54_52.md y en su archivo original; no valida la nueva GPU 54. Las pruebas simuladas verifican conversión, admisión, controlador real con transporte falso y restauración. La respuesta física 54/54 y su uso prolongado permanecen pendientes. No se afirma un límite mecánico ni 5400 RPM exactas sostenidas.
