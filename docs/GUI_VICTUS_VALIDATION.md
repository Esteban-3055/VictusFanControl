# GUI: paquete y recorrido agrupado pendiente

Este documento corresponde a la GUI normal `ProductForm`. Los PASS de software y
las capturas de CI no prueban el comportamiento físico del Victus. Bloques 2/3,
Automatic normal permanece cerrado. Los clocks GPU se ajustan dentro del rango
conservador de producto; la estabilidad física de cada ajuste sigue pendiente.

## Preparación

Descargar el artefacto `product-gui-review-<HEAD>` del workflow build que terminó
correctamente y extraerlo en una carpeta nueva. Contiene app, Guardian, watchdog,
módulos PawnIO fijados, manifiestos y este recorrido. Requiere Windows x64,
.NET 8 Desktop Runtime x64 y el driver PawnIO ya instalado; no instala drivers.

El `README-RC.txt` y los JSON P14 del payload son contratos históricos conservados
para reproducibilidad. Para la fase actual usar este documento y
`GUI_PRODUCT_PHASE.md`: Manual está sujeto al gate vigente del destino exacto;
Automatic normal sigue cerrado; GPU admite ajustes conservadores. El manifiesto
`PRODUCT-GUI-MANIFEST.json` identifica el HEAD y los hashes de esta compilación.

Desde PowerShell, en la carpeta extraída:

```powershell
.\Start-ProductGui.ps1 -Mode Verify
.\Start-ProductGui.ps1 -Mode SelfTest
.\Start-ProductGui.ps1 -Mode Soak
.\Start-ProductGui.ps1 -Mode Open
```

Verify comprueba archivos. SelfTest usa puertos simulados y genera PNG en
`app/logs/product-gui-self-test`; no construye lectores de hardware. Open inicia
la aplicación normal en Firmware sin aplicar límites. El script no cambia la
política de ejecución de PowerShell. Reutilizar la instalación de PawnIO existente.

## Recorrido físico, en una sesión

Trabajar con carga ligera y guardar capturas con hora. Registrar el HEAD completo
y cada resultado como PASS/FAIL/PENDIENTE, sin interpretar un mensaje WMI aceptado
o un cambio de RPM como prueba de ownership. No ejecutar stress simultáneo.

| Grupo | Acciones | Evidencia esperada |
|---|---|---|
| 1. Inicio, edición y persistencia | Abrir en AC; recorrer siete páginas; editar curvas y límites AC/Batería; usar Descartar y Guardar; salir desde bandeja y abrir otra vez | Inicio Firmware y CPU/GPU sin sesión; perfiles independientes; Guardar no escribe hardware; solo valores guardados sobreviven |
| 2. Presentación y bandeja | Redimensionar; probar teclado, mínimo, maximizar, ocultar/abrir; mover entre monitores si hay; probar inicio con Windows y minimizar, luego dejar la preferencia deseada | Controles utilizables; sin solapamientos; DPI correcto; iniciar nunca aplica límites |
| 3. Ventiladores Manual | Con telemetría Healthy, seleccionar Manual y aplicar 30; cambiar a 31 explícitamente; volver a Firmware | Estados y logs coherentes, comando cambiado separado del modo; restauración por backend; no reclamar setpoint físico a partir de RPM |
| 4. CPU/GPU y fuente | En Firmware, conservar GPU AC 1850/Batería 1200 y mínimo 210; Aplicar CPU/GPU; desconectar/conectar cargador; Liberar | Estado CPU y GPU por separado, GPU ActiveUnverified cuando corresponde; fuente Windows separada del perfil confirmado por Guardian; reset/release documentado |
| 5. Lifecycle y salida | Con sesión apropiada, probar pantalla Off/On y suspensión/reanudación; observar recuperación; salir desde bandeja y volver a abrir | Liberaciones y revalidación; ventiladores interrumpidos no se rearman automáticamente; salida limpia o fallo explícito con journals conservados |

Para el primer ensayo del grupo 4 conservar los defaults GPU 1850/1200 MHz.
Los valores personalizados dentro del rango conservador se admiten; la aceptación
NVML no demuestra una lectura independiente del rango.
Probar CPU y GPU juntas ahorra una sesión; si hay fallo parcial, registrar ambos
dominios y liberar mediante la GUI antes de repetir. Si falla restauración,
detener ese grupo y conservar los informes; continuar solo con revisión visual.

## Registro que se necesita devolver

Anotar inicio/fin de cada grupo, HEAD, fuente, estado de los tres subsistemas y
fallos observados. Adjuntar capturas antes/después de transiciones y los logs de
`%LOCALAPPDATA%\VictusFanControl\logs`, además de los informes de la sesión
Performance en `%LOCALAPPDATA%\VictusFanControl\Performance\gui` cuando existan.
Copiar registros después de salir; no borrar journals, leases ni terminar Guardian.
La prueba no promueve gates ni sustituye la cualificación pendiente de Automatic.

El software ya cubre con simulación: aislamiento de perfiles y seis ejes, 64 nodos,
sliders, teclado/accesibilidad, guardado atómico/corrupción, descarte, fallos de
Aplicar/Liberar, datos obsoletos/incompletos, historial con huecos, cola lifecycle,
salida durante inicio/operación y estados parciales. La tabla identifica el trabajo
que requiere Windows y el equipo real.

## Herramientas que no necesitan el Victus

En Curvas, abrir Simulador: los seis valores son entradas sintéticas. El tiempo
virtual avanza cada segundo mientras está visible; Pausar/Reanudar y saltos de
60 s o 20 min controlan la reproducción. Cambiar las entradas
conserva la historia; cambiar perfil/configuración o Reiniciar limpia el modelo.
El gráfico representa demanda cruda, EMA y nivel calculado, no RPM. No valida
SafetyGate ni respuesta física y no habilita Automatic.

En Configuración, Exportar perfiles crea un respaldo JSON. Importar carga ambos
perfiles solo en edición; necesita Guardar para persistir y Aplicar sigue siendo
un contrato separado. Un archivo inválido deja el borrador anterior intacto.
Exportar diagnóstico genera un ZIP de estado, borrador y últimos 2 MiB del log;
revisar rutas locales antes de compartir. No copia ni elimina journals/leases.

Soak no requiere hardware: realiza 30 ciclos de apertura/salida tras 3 de
calentamiento y 924 renderizados con navegación, cambios de tamaño y simulación.
Su informe queda en `app/logs/product-gui-soak/report.json`. Es una prueba acelerada
de recursos de Windows, no una prueba física de ventiladores.


## Prueba supervisada de las curvas Automatic en la GUI nueva

Entrada separada: `Start-ProductGui.ps1 -Mode AutomaticReview`. `Open` y el inicio
con Windows conservan Automatic cerrado. El modo de prueba exige el destino
exacto 8C40/F.18 mediante el gate de cualificación existente; no abre el gate normal.
Arranca en Firmware sin límites aplicados. Seleccionar Automatic explícitamente
aplica ambos límites CPU/GPU antes de configurar la curva.
No admite otra entrada de cualificación simultánea. Performance utiliza su propio
Guardian y puede convivir con esta revisión; validar primero cada dominio por separado.

Cada activación toma una copia de la curva del perfil de la fuente real y espera
3 adquisiciones únicas Healthy antes de controlar. El motor proyecta esa curva
en 10–50 en esta entrada explícita producto. La ruta preparada histórica conserva
30–50. Manual conserva 10–50. Los defaults silenciosos nuevos requieren aceptación
física y están documentados en QUIET_PRODUCT_PRESETS.md.
La revisión comprueba CPU ≤90 °C/60 W y GPU ≤82 °C/75 W, incluida la temperatura
del núcleo más caliente. Un dato inválido, cambio de fuente, lifecycle o error
interrumpe la sesión y solicita Firmware; no rearma automáticamente.

El plazo monotónico de 300 s se inicia tras confirmar límites y seleccionar el
modo Automatic; no consume tiempo durante la preparación. Se verifica antes
del despacho. El supervisor solicita Firmware al vencer el plazo, con hasta 2 s
entre comprobaciones. El plazo no cancela una llamada de firmware ya despachada.
La finalización bloquea una nueva activación hasta reiniciar tras liberación limpia.
También puede seleccionarse Firmware antes del plazo.

Primer ensayo, con cargador conectado y sin carga artificial:
1. Salir de la GUI anterior desde la bandeja; conservar logs/journals.
2. Abrir el paquete nuevo con `-Mode AutomaticReview`; esperar Healthy y fuente Ac.
3. Seleccionar Automatic una vez; esperar CPU Active y GPU ActiveUnverified
   antes de observar la curva. Automatic habilita ambos dominios.
4. Observar 30–60 s y exportar diagnóstico mientras Automatic esté activo.
5. Volver a Firmware, esperar 20 s y exportar otro diagnóstico.

El diagnóstico incluye la configuración congelada aplicada, última decisión,
nivel solicitado, demanda cruda/filtrada y segundos restantes; el log conserva
cada decisión y solicitudes WMI. Las RPM y solicitudes aceptadas no prueban
ownership ni setpoint exacto. Cambiar/guardar un borrador no modifica la curva
en ejecución. Para otro ensayo: Firmware, editar, y seleccionar Automatic de nuevo
antes del vencimiento. No desenchufar, suspender ni añadir carga en el primer ensayo.

## Automatic con límites CPU/GPU

La entrada `AutomaticReview` ahora aplica ambos límites CPU/GPU al seleccionar
Automatic, aunque sus interruptores individuales estén apagados. Usa los valores
en edición, comienza en Firmware y espera confirmación de Performance Guardian
antes de iniciar la curva. Repetir el clic no reescribe límites ni renueva la
revisión de cinco minutos. Volver a Firmware conserva la sesión Performance;
Liberar CPU/GPU vuelve primero a Firmware si Automatic está activo.
Consultar [AUTOMATIC_PRODUCT_SESSION.md](AUTOMATIC_PRODUCT_SESSION.md) para
transiciones, fallos y la siguiente prueba combinada. Las curvas por cambio de
fuente y la reentrada tras interrupciones siguen sin reaplicación automática.


## Actualización: diagnósticos por sesión y demanda en gráficos

Ver [AUTOMATIC_PRODUCT_SESSION.md](AUTOMATIC_PRODUCT_SESSION.md) para el cierre
parcial basado en diagnósticos (7)–(9) y la prueba siguiente del paquete con IDs
por proceso/activación, telemetría JSONL y puntos solicitado/vista previa.
La interrupción de CPU 97 °C ocurrió con CPU/GPU activos; no es una validación
PASS de estabilidad. Automatic normal sigue cerrado. GPU configurable 210–2500
MHz en ambos perfiles, defaults históricos conservados. El punto verde es nivel
solicitado, no RPM ni ownership; amarillo es interpolación del borrador.
