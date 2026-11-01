# cTGP optativo — HP 8C40/F.18

La opción está desactivada por defecto, se guarda con los perfiles y sólo se edita en Firmware. Al entrar en Automático, después de preparar los dominios CPU/GPU seleccionados, el guardián WMI separado puede activar cTGP si hay AC. En batería queda inactivo. No depende de activar el control de clocks GPU NVML.

## Calificación y alcance

Identidad exacta HP 8C40/F.18 y SKU calificado por el resolver existente; DSDT de 678970 bytes, SHA-256 `e4ab0da54f9e18b50f68d6f38eca5e7b95db04fcc9435b8b8e732871fcb8ed39`; PCI 10DE:28A0/subsystem 103C:8C40; VBIOS 95.07.2B.00.54; driver 621.01; NVML máximo 75000 mW; adaptador HP reconocido de 200 W. Una actualización que cambie la configuración exige nueva revisión. La calificación NVML y DSDT es de lectura; no aplica límites.

La interfaz admite una selección booleana, sin entrada de vatios. Sólo se autorizan HP command 20008h: GET 21h (4 bytes), GET 28h (128 bytes) y SET 22h (4 bytes), con payload fijo. Se exige estado inicial `00 01 01 00`; el estado activo es `01 01 01 00`. El cuarto byte permanece cero. El SET coescribe los bytes AC/DC y ejecuta la notificación OEM; por eso no se admite una política inicial diferente. No cambia VBIOS ni habilita 80–85 W.

## Propiedad y recuperación

El guardián persiste intención antes del SET y conserva la obligación de restaurar aunque la llamada falle. Todas las solicitudes usan el mutex y los marcadores de llamadas nativas WMI existentes. La GUI carece de permiso para enviar estas solicitudes. El guardián comprueba la fuente y la identidad del propietario antes del SET de activación y observa la política aproximadamente cada dos segundos; no la reescribe periódicamente.

Al salir de Automático, perder AC, caducar el heartbeat, finalizar el propietario o fallar la observación, se restaura `00 01 01 00` y se exigen dos lecturas coincidentes. La restauración de ventiladores se intenta también si cTGP falla. Una operación nativa incierta impide declarar liberación limpia y conserva los registros. La recuperación explícita de una sesión huérfana con lease incluye la política cTGP registrada; una sesión sin prueba de propiedad no recibe escrituras. Un cambio externo impide reactivar en esa sesión y cancela la reanudación automática tras el fallo.

El retorno a AC permite activar de nuevo la opción seleccionada sólo si la sesión continúa sana y la transición de perfiles queda confirmada. Firmware y Manual no solicitan cTGP. Un inicio normal mantiene Firmware hasta las acciones de inicio ya configuradas por el usuario.

## Evidencia

El ensayo físico previo de tres minutos informó potencia media bajo carga aproximada de 69,01 → 74,76 → 69,62 W y límite efectivo 70 → 75 → 70 W, sin anomalías comunicadas. Hubo pausa entre pasadas; no son tres minutos de carga continua, ni un resultado de FPS o de estabilidad prolongada. La integración nueva no se activa en el equipo durante sus pruebas de software. El hotspot del ensayo prolongado no está confirmado numéricamente.

`--ctgp-self-test` verifica políticas con transporte falso, fallos de activación/restauración, persistencia y compatibilidad de perfiles, interfaz dibujada sin abrir ventanas y comunicación con un guardián separado de fixture. `--ctgp-readonly-preflight` sólo verifica identidad/firmware/NVML, sin SET HP. La CI ejecuta las pruebas de producto y de recuperación contra el mismo commit. La observación física de esta versión completa permanece pendiente.
