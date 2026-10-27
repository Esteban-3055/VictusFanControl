# Refrigeración previa al apagado con corriente

Incluido en la preliminar 1.3.7. Las pruebas son de software: no se instaló en el equipo ni se realizó un apagado físico durante la preparación.

## Secuencia

- **Corriente AC confirmada:** al recibir una solicitud normal de fin de sesión de Windows, el programa registra «Refrigerando el equipo antes de apagar (20 segundos)» y dispone de una fase adicional de hasta **20 000 ms** para solicitar el nivel **50/50**, nominalmente **5000 RPM** CPU/GPU. Mantiene la telemetría y la supervisión de seguridad existentes. La ventana deja de admitir nuevas operaciones, recuperación automática o cambios de perfil durante esa fase.
- Termina esa fase y responde a Windows. Cuando Windows confirma el cierre, ejecuta la liberación habitual con su presupuesto separado de hasta **20 000 ms**. Ese presupuesto no se consume esperando la fase de refrigeración. Una liberación rápida no añade una espera artificial de otros veinte segundos.
- **Batería, fuente desconocida o consulta fallida:** no se añade refrigeración ni se envía un nivel nuevo por esta función; se conserva la liberación normal. Seleccionar la pestaña AC/Batería del editor no altera esta decisión. La señal utilizada es la consulta canónica de alimentación de Windows, con AC confirmado; no una etiqueta de perfil guardada.

El plazo de refrigeración incluye la preparación y aplicación del comando: no se prolonga para compensar una cola o llamada nativa lenta ni para esperar que los rotores alcancen una velocidad. El nivel HP es una solicitud nominal. La aceptación WMI y los intervalos de lectura de RPM no prueban que ambos ventiladores sostengan exactamente 5000 RPM.

## Autoridad y límites

Solo se permite en el destino HP ya autorizado, con runtime Healthy, autorización Manual, sin recuperación/lifecycle pendiente y con autoridad Firmware o Custom válida. Se reutilizan `SelectFanModeAsync(Manual)` y `ApplyManualAsync(50)`, el coordinador de ventiladores, SafetyGate y el guardián WMI existentes. No se envían comandos EC ni comandos HP directos desde el cierre, no se cambian curvas o presets CPU/GPU y no se altera el ESP32.

Un ámbito de admisión acompaña la orden por sus colas asíncronas y vuelve a comprobar AC, plazo y propietario en el coordinador y en el setter nativo. Las órdenes pendientes no pueden comenzar una escritura después de expirar o cancelarse la fase. Una llamada nativa que ya comenzó sigue perteneciendo a su propietario hasta que realmente retorna: el timeout no la declara terminada ni borra journals.

La última comprobación se repite después de crear el registro duradero de la llamada, por si ese paso fue lento. Si se rechaza antes de entrar en la llamada nativa, se conserva una confirmación directa de «no ejecutada»; no se confunde con una llamada que entró y terminó de forma desconocida. La liberación FF/FF y LegacyDefault permanece disponible aunque haya vencido el ámbito de refrigeración.

Se omite o interrumpe la fase ante batería/desconexión, fuente desconocida, fallo de control, suspensión, cierre forzado, cierre de sesión de usuario o peticiones del Restart Manager/instalador. Si el apagado se cancela después de intentar el cambio de ventiladores, se cierra la admisión y se retira el nivel temporal mediante la liberación habitual a Firmware. No se reactiva Automático implícitamente; requiere un nuevo inicio explícito y las comprobaciones normales. Un fallo conserva su evidencia y no declara una liberación correcta.

Windows no distingue apagar de reiniciar en la solicitud normal `WM_QUERYENDSESSION` con flags cero. Por ello esta refrigeración también se aplica al **reinicio normal** con AC. No se llama a ninguna API para iniciar o repetir el apagado: si el usuario lo cancela, el programa no lo vuelve a solicitar.

La fase AC sucede mientras se procesa `WM_QUERYENDSESSION`, bombeando mensajes STA para que continúen las operaciones existentes y la cancelación. La liberación sigue en `WM_ENDSESSION`, tras confirmar Windows el cierre. No se coloca una espera conjunta de cuarenta segundos dentro de `WM_ENDSESSION`, cuyo tiempo disponible puede ser menor. «Apagar de todos modos», los cierres críticos y los cortes físicos no pueden garantizar estas esperas; se da prioridad a la liberación existente.

Referencias oficiales: [WM_QUERYENDSESSION](https://learn.microsoft.com/en-us/windows/win32/shutdown/wm-queryendsession), [registro del motivo](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-shutdownblockreasoncreate) y [tratamiento de ambas fases](https://learn.microsoft.com/en-us/previous-versions/windows/desktop/mpc/application-shutdown-changes-in-windows-vista).

## Validación reproducible

`--session-end-self-test` ejecuta únicamente ventanas y runtimes aislados: inyecta la alimentación y un reloj virtual; envía los mensajes solo a la ventana de prueba. No llama a apagar/reiniciar Windows, no consulta hardware y no abre controladores físicos. También forma parte de `--product-gui-self-test`.

Comprueba AC frente a pestaña del editor, batería/desconocido, destinos y autoridades cerrados, fase completa de veinte segundos virtuales, un único par 50/50, consultas duplicadas, liberación posterior, cancelación, fuente perdida, suspensión, flags críticos/logoff/instalador, fallo de aplicación y trabajo que termina tarde por la cola o tras entrar en la preparación nativa. Los tests previos conservan la espera STA, el timeout de liberación sin falsa confirmación y la disposición única.

Las pruebas físicas de las RPM durante un apagado real y la pantalla de bloqueo real de Windows quedan pendientes. Los resultados de compilación y de las suites aisladas se conservan por separado; no constituyen validación física.

La primera ejecución general detectó además una carrera previa al cerrar el simulador: `AcceptTcpClientAsync` podía informar `ObjectDisposedException` tras cancelar y cerrar el listener. Se trata como cierre normal exclusivamente si el token ya está cancelado; una detención inesperada sigue siendo un fallo. Se añaden cuarenta cierres de servidores loopback y la comprobación del fallo sin cancelación. Esto solo afecta al simulador de pruebas, no al protocolo o firmware del ESP32.
