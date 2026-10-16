# Recuperación sin intervención: alcance y barreras

La opción existente **Recuperar sesiones automáticamente**, activada por defecto,
permite gestionar las sesiones reconocidas sin pedir otra confirmación. Se respetan
las preferencias guardadas y la política existente de reanudación de Automático.
Recuperar una sesión no equivale a obtener autoridad nueva para controlar ventiladores.

## Comportamiento

| Caso | Acción del programa |
|---|---|
| Sesión anterior reconocida y estable | Espera inicial de 10 s y recuperación con el cliente y Guardian existentes. |
| Error de E/S o permisos al leer la selección, antes de liberar o ejecutar un recuperador | Hasta dos reintentos de lectura, con espera de 30 y 60 s. No se actúa sobre hardware con una selección no validada. |
| Recuperación necesaria al pulsar Instalar | Tras la confirmación normal de instalación y verificar el instalador, recupera la selección exacta y continúa sin pedir Recuperar sesiones por separado. |
| Recuperación completada para instalar | No se crea otra instancia de telemetría/controlador entre recuperación e instalación. |
| Recuperación o instalación fallida | El monitor vuelve a funcionar. Se conserva la descarga válida y la evidencia. |
| Respuesta WMI nula después del retorno de InvokeMethod | Se registra la finalización antes de validar el contenido; la respuesta sigue rechazándose. |
| Excepción durante InvokeMethod o mutex abandonado | Se conserva la incertidumbre y se mantienen los bloqueos existentes. |
| Registro inválido, cambiado, de otro equipo o liberación no confirmada | No se repite una operación física para intentar forzar el éxito. Se conserva el diagnóstico. |
| Suspensión/cierre durante recuperación para actualizar | Se espera el recuperador en curso; no se abre el instalador ni otro controlador tras la interrupción. |

La protección existente de un intento de recuperación por inventario y un máximo de
tres episodios hasta un minuto de Automático saludable se conserva. Los intentos
manuales y los de instalación también cuentan para impedir que el monitor repita
una liberación fallida. Sólo el fallo de lectura anterior a cualquier operación
puede devolver su intento al planificador, con el límite indicado.
Se admiten como máximo seis aplazamientos de lectura en ese período, incluso si
el inventario cambia, y volver a un inventario anterior conserva su espera pendiente.

Si se desactiva la recuperación automática, la instalación conserva la vía manual.
Una instalación sigue requiriendo la confirmación normal del usuario y, cuando
corresponda, el diálogo de permisos de Windows. No se reinicia Windows por iniciativa
del programa. Una transacción nativa realmente incierta puede seguir necesitando
un arranque nuevo y la recuperación posterior; no se borran registros para simularlo.

## Diagnóstico y pruebas

El ZIP incluye estado del actualizador, versión disponible/descargada y actividad
del monitor de recuperación. No se incluyen claves de emparejamiento.

Prueba concentrada sin hardware:

```powershell
dotnet run --project src/VictusFanControl.App -c Release -- --product-recovery-self-test
```

La misma cobertura forma parte de `--product-gui-self-test`: inventario exacto,
reintentos de lectura, registros malformados, intentos manuales, recuperación e
instalación únicas, verificación del instalador previa a la recuperación,
desactivación por preferencias, suspensión, cierre y retención de descargas.
El fixture WMI comprueba respuesta nula retornada y excepción nativa con marca
conservada, sin invocar HP WMI real. Las pruebas de GUI mantienen el bombeo de
mensajes al esperar eventos de energía asíncronos, con plazo de 10 s.

No se modifican las solicitudes HP, límites, curvas, SafetyGate, criterios de
propiedad ni rutinas físicas del Guardian. El ajuste WMI se limita a registrar el
retorno antes de validar la respuesta. Estas pruebas no acreditan un PASS físico
ni demuestran que la marca original procediera de una respuesta nula.
