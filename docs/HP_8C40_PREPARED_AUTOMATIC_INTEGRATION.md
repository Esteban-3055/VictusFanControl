# HP 8C40/F.18: integración de Automatic con activación restringida

La GUI y el controlador final incorporan la lógica estudiada en el experimento WMI. `AutomaticExecutionAuthorized` sigue en `false`; el modo inicial continúa siendo Firmware. La integración de software no constituye una promoción física de la curva ni del modo Automatic.

## Componentes compartidos

- `AdaptiveFanInertiaPolicy` y `AdaptiveFinalDemandFilter`: máximo de las seis demandas, una EMA final (subida 6 s, bajada 20 s), décimas con empate hacia abajo, techo entero HP; subida normal confirmada durante 2 s y bajada durante 12 s. La excepción CPU ≥85 °C o GPU ≥78 °C usa demanda original y sube hasta cuatro niveles por muestra nueva.
- `Hp8C40AutomaticPolicy`: envolvente preparada 30–50, niveles CPU/GPU iguales. La selección Manual y los perfiles almacenados conservan su contrato 10–50; aplicar un perfil en el editor sólo modifica su vista previa.
- `Hp8C40AutomaticThermalAdmission`: inicio completo y sano por debajo de CPU 95 °C/GPU 87 °C; CPU 95–98.x confirma cinco adquisiciones consecutivas o un máximo de 2000 ms monotónicos, lo que ocurra primero. CPU ≥99 °C y GPU ≥87 °C cierran inmediatamente. CPU usa el máximo entre Package y núcleo físico más caliente.

Las clases `WmiFan*` del experimento son fachadas de estos componentes, sin copias independientes del algoritmo. SafetyGate sigue siendo el detector original sin estado. Manual conserva su confirmación y sus verificaciones existentes.

## Ruta final preparada

El worker espera cada procesamiento Automatic en su propio bucle; no se lanza una tarea de control por muestra ni se controla desde el hilo gráfico. La adquisición Automatic abierta espera una lectura RPM nueva y serializada, luego lee CPU/GPU sin programar otra consulta periódica. Durante confirmación pendiente omite la pausa artificial y limita la espera al presupuesto restante. Firmware y Manual mantienen su adquisición habitual. Un vencimiento lógico no cancela el firmware ni libera el slot de una llamada nativa aún pendiente.

El supervisor, las comprobaciones de pantalla y el controlador usan la misma admisión de la sesión Automatic. Sólo observar una adquisición nueva puede avanzar la confirmación. La pantalla de Automatic muestra el resultado del controlador; los perfiles de vista previa no sustituyen su política activa.

Cada comando Automatic lleva una comprobación que fluye por la preparación asincrónica: el coordinador la aplica después de esperar su exclusión, el backend antes del setter y el cliente HP inmediatamente antes de `InvokeMethod`. Se vuelve a comprobar estado/frescura y el plazo sin consumir otra secuencia de control. Las lecturas y `FF/FF → LegacyDefault` permanecen disponibles tras el cierre. Ningún resultado frío tardío ni reinicio de la política reabre la admisión: hace falta una transición explícita Firmware → Automatic y un nuevo inicio sano.

El backend final conserva sus verificaciones EC de propiedad, ACK y watchdog. Compartir política/admisión con el experimento no transfiere automáticamente la evidencia de su transporte sin EC a esa ruta final.

## Verificación y siguiente evidencia

`--adaptive-policy-self-test` incluye pruebas del controlador con backend sintético: gate cerrado, Manual 10, piso Automatic 30, subida térmica 31→35, vistas repetidas sin doble conteo ni consumo de secuencia, cierre por cinco muestras o plazo, lectura fría tardía, vencimiento tras preparación y justo antes del transporte, recuperación disponible y aislamiento respecto de Manual. `--wmi-fan-experiment-self-test` conserva las pruebas de adquisición, mutex, llamada nativa pendiente, recuperación, EMA y redondeo.

La captura 5712/856146 del 2026-10-04 completó cinco minutos en simulación, pero CPU llegó sólo a 87 °C. No ejercitó físicamente la confirmación CPU ≥95 °C. La captura activa anterior tampoco demuestra la combinación de esta versión y el backend final.

La siguiente captura autorizada es el experimento supervisado actual con `-Control`, cinco minutos de uso habitual y sin provocar deliberadamente temperaturas altas. Volver a Firmware, cerrar la GUI y abrir PowerShell como administrador desde el repositorio:

```powershell
git switch feature/victus-8c40-wmi-broker-5sample
git pull --ff-only
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\Start-Victus-WmiFanExperiment.ps1 -CaptureMinutes 5 -Control
```

Conservar ZIP y SHA256, incluidos fallos o recuperaciones. Revisar consignas, RPM frescas, curva, decisiones térmicas, plazo, recuperación, llamadas pendientes y nuevos ACPI 13/15 antes de otra fase. Una captura sin CPU ≥95 °C valida sólo la operación normal. Esta prueba aún usa el transporte experimental; la validación supervisada del backend final queda pendiente y Automatic en la GUI continúa cerrado.
