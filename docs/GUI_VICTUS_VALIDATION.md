# GUI: paquete y recorrido agrupado pendiente

Este documento corresponde a la GUI normal `ProductForm`. Los PASS de software y
las capturas de CI no prueban el comportamiento físico del Victus. Bloques 2/3,
Automatic normal y clocks GPU personalizados permanecen pendientes/cerrados.

## Preparación

Descargar el artefacto `product-gui-review-<HEAD>` del workflow build que terminó
correctamente y extraerlo en una carpeta nueva. Contiene app, Guardian, watchdog,
módulos PawnIO fijados, manifiestos y este recorrido. Requiere Windows x64,
.NET 8 Desktop Runtime x64 y el driver PawnIO ya instalado; no instala drivers.

El `README-RC.txt` y los JSON P14 del payload son contratos históricos conservados
para reproducibilidad. Para la fase actual usar este documento y
`GUI_PRODUCT_PHASE.md`: Manual está sujeto al gate vigente del destino exacto;
Automatic normal y clocks GPU personalizados siguen cerrados. El manifiesto
`PRODUCT-GUI-MANIFEST.json` identifica el HEAD y los hashes de esta compilación.

Desde PowerShell, en la carpeta extraída:

```powershell
.\Start-ProductGui.ps1 -Mode Verify
.\Start-ProductGui.ps1 -Mode SelfTest
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

Para el grupo 4 no aplicar máximos GPU distintos de los dos valores cualificados.
Los valores personalizados se pueden editar/guardar, pero Aplicar los rechaza.
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
