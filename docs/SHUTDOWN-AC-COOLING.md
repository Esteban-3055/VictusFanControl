# Refrigeración previa al apagado con corriente

## Comprobación física del 27 de octubre de 2026

Se ejecutó un apagado normal con AC en el Victus HP 8C40 usando una copia local
corregida, sin sustituir la release instalada ni su tarea de inicio. La GUI y ambos
guardianes confirmaron sus niveles de cierre. La fase duró **20047 ms**; la orden
50/50 fue aceptada por WMI y la preparación terminó correctamente. Después comenzó
la liberación: resultado de sesión `RanToCompletion`, código 0, **521 ms**.

La telemetría durante la fase registró CPU y GPU alcanzando nominalmente 5000 RPM
(lecturas WMI de resolución 100 RPM). La primera adquisición con ambos a ese nivel
ocurrió aproximadamente 15,6 s después del inicio: los veinte segundos incluyen
aceleración, no son veinte segundos sostenidos a 5000 RPM. Las lecturas térmicas
pasaron de CPU 51/GPU 45 °C a CPU 48/GPU 43 °C; este único ensayo no cuantifica
por sí solo el calor residual del chasis.

El recibo WMI, ligado al PID e inicio del propietario de prueba, confirma
`CLIENT_RELEASE`, aceptación de las solicitudes de liberación y LegacyDefault,
lease retirado y ausencia de fallo. El informe Performance del mismo propietario
confirma `CLIENT_SHUTDOWN`, `Stopped`, dominios CPU/GPU `Disabled`, fuente detenida,
una liberación y ausencia de fallo. Esto no convierte la aceptación WMI en una
comprobación independiente de propiedad física del firmware.

La evidencia se conserva privadamente junto a la copia local de prueba. No se
suben los registros completos. Es validación física de este candidato y este
apagado AC; batería, cancelación, apagado forzado y otros equipos conservan su
alcance de pruebas aisladas. La copia local usa la versión de ensamblado de
desarrollo 0.4.0.0 y se identifica por hash, no como una nueva release pública.

Incluido en la preliminar 1.3.7. Su preparación original tuvo pruebas de software;
el ensayo físico posterior de la corrección local se detalla arriba.

## Secuencia

- **Corriente AC confirmada:** al recibir una solicitud normal de fin de sesión de Windows, el programa registra «Refrigerando el equipo antes de apagar (20 segundos)» y dispone de una fase adicional de hasta **20 000 ms** para solicitar el nivel **50/50**, nominalmente **5000 RPM** CPU/GPU. Mantiene la telemetría y la supervisión de seguridad existentes. La ventana deja de admitir nuevas operaciones, recuperación automática o cambios de perfil durante esa fase.
- Termina esa fase y responde a Windows. Cuando Windows confirma el cierre, ejecuta la liberación habitual con su presupuesto separado de hasta **20 000 ms**. Ese presupuesto no se consume esperando la fase de refrigeración. Una liberación rápida no añade una espera artificial de otros veinte segundos.
- **Batería, fuente desconocida o consulta fallida al inicio:** no se añade refrigeración ni se envía un nivel nuevo por esta función; se conserva la liberación normal. Seleccionar la pestaña AC/Batería del editor no altera esta decisión. La señal utilizada es la consulta canónica de alimentación de Windows, con AC confirmado; no una etiqueta de perfil guardada.

### Continuidad al desconectar el cargador (corrección local del 29 de octubre)

Si AC estaba confirmado al admitir la fase, desconectar después el cargador o perder la lectura de alimentación no la cancela. Se conserva el plazo original de veinte segundos desde el inicio: el cambio de fuente no reinicia ni prolonga el contador. Las condiciones de seguridad, propietario, autoridad y liberación siguen vigentes. Esta corrección se valida con fuentes inyectadas y reloj virtual/real; todavía no acredita un nuevo apagado físico con desconexión del cargador.

El plazo de refrigeración incluye la preparación y aplicación del comando: no se prolonga para compensar una cola o llamada nativa lenta ni para esperar que los rotores alcancen una velocidad. El nivel HP es una solicitud nominal. La aceptación WMI y los intervalos de lectura de RPM no prueban que ambos ventiladores sostengan exactamente 5000 RPM.

## Autoridad y límites

### Corrección de orden de cierre tras el incidente de 1.3.8

La sesión instalada de 1.3.8 registró AC confirmado, inicio de refrigeración y
cancelación unos 104 ms después, al pasar la autoridad de Custom a Restoring.
Después aparecieron Faulted, ausencia de informe de liberación WMI y terminación
del guardián de rendimiento con código 1073807364. No hubo una fase de 20 segundos.
La evidencia del incidente se conserva localmente; no se incluyen logs privados
en el repositorio. El registro antiguo no identifica cuál de las condiciones de
cancelación fue la primera ni prueba por sí solo el mecanismo de terminación.

La aplicación y sus guardianes usaban el nivel de cierre predeterminado de Windows
(0x280). Se establece ahora 0x3FF para la GUI y 0x100 para ambos roles de guardián
de producción, mediante `SetProcessShutdownParameters`, con verificación de lectura.
Windows notifica primero los niveles altos: la GUI debe poder refrigerar y pedir
liberación por IPC antes de que el sistema termine sus dependencias. No es prioridad
de CPU; se conservan flags cero, sin SHUTDOWN_NORETRY, sin ignorar señales, sin
servicios nuevos ni cambios globales de Windows. Un fallo de registro se informa
y no cambia la autoridad o los journals. Los fixtures no registran roles de
producción; su ensayo nativo restablece el nivel y los flags originales.

Se añaden motivo preciso de cancelación (plazo, propietario, admisión inicial AC o estado de control),
flags de consulta y duración real/preparación al terminar la fase. La regresión
simula pérdida del guardián: aborta la refrigeración y conserva el cierre normal.
No se permite seguir escribiendo si la autoridad está Restoring o Faulted.

La compilación y pruebas aisladas de esta corrección no prueban por sí solas el
orden observado en un apagado físico. El ensayo AC posterior con la copia local
verificó intervalo y liberación según los resultados anteriores. Los ensayos
aislados siguen sin reiniciar/apagar el equipo ni sustituir la instalación.
[Orden de cierre documentado por Microsoft](https://learn.microsoft.com/en-us/windows/win32/api/processthreadsapi/nf-processthreadsapi-setprocessshutdownparameters).

Solo se permite en el destino HP ya autorizado, con runtime Healthy, autorización Manual, sin recuperación/lifecycle pendiente y con autoridad Firmware o Custom válida. Se reutilizan `SelectFanModeAsync(Manual)` y `ApplyManualAsync(50)`, el coordinador de ventiladores, SafetyGate y el guardián WMI existentes. No se envían comandos EC ni comandos HP directos desde el cierre, no se cambian curvas o presets CPU/GPU y no se altera el ESP32.

Un ámbito de admisión acompaña la orden por sus colas asíncronas y vuelve a comprobar la admisión inicial AC, plazo, propietario y condiciones de control en el coordinador y en el setter nativo. Las órdenes pendientes no pueden comenzar una escritura después de expirar o cancelarse la fase. Una llamada nativa que ya comenzó sigue perteneciendo a su propietario hasta que realmente retorna: el timeout no la declara terminada ni borra journals.

La última comprobación se repite después de crear el registro duradero de la llamada, por si ese paso fue lento. Si se rechaza antes de entrar en la llamada nativa, se conserva una confirmación directa de «no ejecutada»; no se confunde con una llamada que entró y terminó de forma desconocida. La liberación FF/FF y LegacyDefault permanece disponible aunque haya vencido el ámbito de refrigeración.

Se omite la fase si empieza en batería o con fuente desconocida. Una fase ya admitida se interrumpe ante fallo de control, suspensión, cierre forzado, cierre de sesión de usuario o peticiones del Restart Manager/instalador. Si el apagado se cancela después de intentar el cambio de ventiladores, se cierra la admisión y se retira el nivel temporal mediante la liberación habitual a Firmware. No se reactiva Automático implícitamente; requiere un nuevo inicio explícito y las comprobaciones normales. Un fallo conserva su evidencia y no declara una liberación correcta.

Windows no distingue apagar de reiniciar en la solicitud normal `WM_QUERYENDSESSION` con flags cero. Por ello esta refrigeración también se aplica al **reinicio normal** con AC. No se llama a ninguna API para iniciar o repetir el apagado: si el usuario lo cancela, el programa no lo vuelve a solicitar.

La fase AC sucede mientras se procesa `WM_QUERYENDSESSION`, bombeando mensajes STA para que continúen las operaciones existentes y la cancelación. La liberación sigue en `WM_ENDSESSION`, tras confirmar Windows el cierre. No se coloca una espera conjunta de cuarenta segundos dentro de `WM_ENDSESSION`, cuyo tiempo disponible puede ser menor. «Apagar de todos modos», los cierres críticos y los cortes físicos no pueden garantizar estas esperas; se da prioridad a la liberación existente.

Referencias oficiales: [WM_QUERYENDSESSION](https://learn.microsoft.com/en-us/windows/win32/shutdown/wm-queryendsession), [registro del motivo](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-shutdownblockreasoncreate) y [tratamiento de ambas fases](https://learn.microsoft.com/en-us/previous-versions/windows/desktop/mpc/application-shutdown-changes-in-windows-vista).

## Validación reproducible

`--session-end-self-test` ejecuta únicamente ventanas y runtimes aislados: inyecta la alimentación y un reloj virtual; envía los mensajes solo a la ventana de prueba. No llama a apagar/reiniciar Windows, no consulta hardware y no abre controladores físicos. También forma parte de `--product-gui-self-test`.

Comprueba AC frente a pestaña del editor, batería/desconocido al inicio, destinos y autoridades cerrados, fase completa de veinte segundos virtuales incluso al pasar de AC a batería o fuente desconocida, un único par 50/50, consultas duplicadas sin prolongación, liberación posterior, cancelación, suspensión, flags críticos/logoff/instalador, fallo de aplicación y trabajo que termina tarde por la cola o tras entrar en la preparación nativa. Los tests previos conservan la espera STA, el timeout de liberación sin falsa confirmación y la disposición única.

`--session-end-timing-self-test` amplía esa comprobación con veinte segundos de
reloj real, mensajes nativos dirigidos exclusivamente a una ventana aislada,
registro/retirada real del motivo de bloqueo y dos procesos auxiliares separados.
Usa los clientes WMI/Performance reales con hosts explícitos de fixture, nonce,
IPC y recibos de liberación; ningún dominio abre hardware. Mantiene heartbeat y
STATUS durante la fase; inyecta desconexión AC a los tres segundos y exige cero disposiciones antes de 20000 ms, valida el
recibo de restauración WMI, la retirada de su lease y el cierre Performance.
Una confirmación adicional retrasada 1500 ms debe terminar antes de devolver
WM_ENDSESSION; una segunda petición de salida no puede repetir la disposición.
El nivel nominal 50 se comprueba como intención, no como RPM físicas.

El resultado de fin de sesión registra estado de la tarea, código de salida y
tiempo; el vaciado opcional del log consume hasta un segundo del presupuesto de
liberación restante. No amplía el plazo ni acredita liberación por un timeout.
El primer ensayo del harness temporal detectó un observador Process ya dispuesto
por el cliente, no un fallo del producto: se corrigió conservando un handle de
observación independiente. Se conserva el resultado inicial junto al ensayo final.

Las RPM de un apagado AC real se comprobaron en el ensayo descrito arriba. No se
capturó visualmente la pantalla de bloqueo de Windows. Los resultados de
compilación y suites aisladas se conservan por separado de esa evidencia física.

La primera ejecución general detectó además una carrera previa al cerrar el simulador: `AcceptTcpClientAsync` podía informar `ObjectDisposedException` tras cancelar y cerrar el listener. Se trata como cierre normal exclusivamente si el token ya está cancelado; una detención inesperada sigue siendo un fallo. Se añaden cuarenta cierres de servidores loopback y la comprobación del fallo sin cancelación. Esto solo afecta al simulador de pruebas, no al protocolo o firmware del ESP32.
