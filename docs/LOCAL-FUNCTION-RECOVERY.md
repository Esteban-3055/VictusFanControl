# Recuperación por función — 1.15.2 preliminar

La 1.15.2 preliminar incorpora recuperación por función. Las comprobaciones de
software utilizan datos y procesos de prueba separados; la instalación y las
observaciones físicas se verifican por separado.

## Comportamiento previsto

Un fallo debe afectar al menor conjunto de recursos que realmente lo comparten.
La selección del usuario, el estado aplicado y la salud del controlador son
estados distintos. Una caída temporal de ventiladores a Firmware conserva la
selección lógica de Automático y las solicitudes de las funciones vinculadas.
Una orden explícita del usuario para pasar a Firmware sí cambia esa selección.
Los apagados individuales, la suspensión y el cierre siguen teniendo prioridad.

| Función afectada | Recuperación | Funciones sanas |
| --- | --- | --- |
| Límites CPU | Retirar y confirmar solo el dominio CPU antes de reactivarlo | Conservar sesión GPU, cTGP y ventiladores |
| Límites GPU | Retirar y confirmar solo el dominio GPU antes de reactivarlo | Conservar sesión CPU, cTGP y ventiladores |
| Observación cTGP | Confirmar el cierre y la restauración de la sesión WMI exacta; después reactivar solo cTGP | CPU/GPU continúan; ventiladores pueden necesitar liberar el recurso WMI compartido |
| Ventiladores | Mantener su mecanismo propio de recuperación y las protecciones térmicas | Conservar las solicitudes CPU/GPU/cTGP y la base que tenga entradas propias válidas |
| Base externa | Mantener su reconexión y confirmación propias | No depender de un fallo ajeno para retirar CPU/GPU/cTGP |

La base en salida fija o con curva por temperatura puede continuar con sus
propias entradas válidas. El modo que sigue a los ventiladores internos necesita
una orden interna realmente aceptada y vigente; no se inventa ni se prolonga
una lectura antigua para aparentar continuidad.

Las preferencias heredadas sin una configuración explícita de Funciones usan
el aislamiento de fallos, conservando los valores y enlaces seleccionados.
El preset explícito `Legacy` conserva el comportamiento anterior de acoplamiento.
Las preferencias explícitas existentes siguen siendo respetadas.

## Reintentos CPU/GPU/cTGP

- Cada función mantiene su propio contador y reloj monotónico.
- Esperas mínimas: 10, 30 y 60 segundos, con tres intentos por episodio.
- Solo se intenta cuando están confirmados alimentación, sensores y propiedad
  necesarios para esa función. El vencimiento del tiempo no demuestra permiso.
- Una recuperación breve seguida de otro fallo no repone el presupuesto.
  Se requieren 60 segundos de funcionamiento saludable observado para reponerlo.
- Una orden de apagado cancela el reintento. Una recuperación ya en cola no
  puede convertirse en una nueva solicitud de encendido después de esa orden.
- Agotar los intentos deja esa función pendiente de revisión, sin reiniciar
  ni retirar automáticamente las sesiones sanas de las otras funciones.

Las actualizaciones combinadas CPU/GPU siguen confirmándose por dominio. Si CPU
acepta el nuevo valor y GPU lo rechaza, se conserva el valor aplicado de CPU y
solo GPU entra en recuperación; no se anuncia una reversión que no ocurrió.

## Límite del recurso compartido HP WMI

Ventiladores y cTGP comparten un supervisor y la exclusión de órdenes nativas.
Una operación cuyo resultado sea desconocido sigue bloqueando ese recurso.
No basta con que desaparezca un PID o exista un archivo de informe.

La confirmación del cierre exige el proceso real terminado, su identidad de
inicio, propietario, directorio y plataforma, un informe válido de restauración,
la retirada de la concesión y ausencia de operaciones nativas pendientes o
inciertas. El cierre ya confirmado se puede observar más de una vez sin volver
a enviar las órdenes de restauración. Una nueva marca de incertidumbre invalida
el uso de esa confirmación almacenada.

Los fallos tipados de lectura de observación cTGP y los vencimientos de admisión
sin envío pueden volver a intentarse después de esa confirmación. Un cambio de
política por otro controlador, evidencia dañada o una operación nativa incierta
requieren revisión. El fallo de cTGP se conserva por separado de la prueba de que
el acceso compartido ya fue liberado.

El diagnóstico cTGP conserva tipo de excepción, HRESULT, operación y detalle
acotado, incluso cuando el mensaje original está vacío. Esto permite investigar
el disparador sin atribuirlo sin pruebas al driver o al hardware.

## Verificación

`--local-recovery-self-test` verifica conservación de sesiones sanas, recuperación
CPU/GPU separada, actualizaciones parciales, espera por limpieza WMI, prioridad
de apagado incluso con un reintento en cola, clasificación de fallos y presupuesto
ante fallos repetidos. `--shared-wmi-self-test` comprueba la aceptación tardía del
recibo exacto y que la restauración no se repita ni se ignore nueva incertidumbre.

También se ejecutan las pruebas existentes de rendimiento, vida de sesiones,
funciones independientes, reglas, coordinación de Automático, telemetría y
HyperX, además de la aceptación de ciclo de vida y mutaciones de perfiles.
Los registros de esta revisión se conservan en
`output/scoped-recovery-20261125/`, fuera del código instalado.

Estas pruebas verifican lógica y comunicación con procesos simulados. No prueban
todavía un ciclo real de suspensión/reanudación con este desarrollo, fallos de un
driver real, temperaturas bajo carga ni estabilidad prolongada del hardware.
