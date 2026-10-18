# LCD y revisión de Respuesta y arranque

La pantalla de temperaturas conserva 30 s: CPU y GPU en la primera fila y
`Ambiente:21.2C` en la segunda. La tercera pantalla conserva 5 s y pasa a mostrar
`DEMANDA: 35%` y `PWM: 38% (96)`. Uso CPU/GPU/RAM y conexión PC/Wi-Fi conservan
5 s cada una. Ciclo completo: 45 s, filas de exactamente 16 caracteres.

Ambiente viene exclusivamente de GPIO0/NTC y vence a los 3 s. Las temperaturas
CPU/GPU y DEMANDA proceden de la telemetría firmada, con vencimiento de transporte
a los 2 s y de origen a los 5 s. Los nulos se muestran `--`. Un fallo NTC no
sustituye temperaturas del PC ni DEMANDA; oculta PWM anterior y mantiene el
control de seguridad a 255. Perder el PC no oculta una lectura NTC vigente.

## Recorrido de la configuración

Se inspeccionaron `ExternalCurvePage`, `ProductCanvas.ExternalCurve`,
`ProductForm.ExternalCooling`, `ExternalCoolingSettingsStore`, `Esp32TcpClient`,
el parser `VictusProtocol`, `controlTask()` y `ExternalController`.
No se detectó un cruce de campos ni conversión incorrecta de unidades.

| Control Windows | Campo firmado | Uso en ESP32 |
|---|---|---|
| Subida | `rise` | Límite ascendente: valor × 2,55 × dt, en salida lógica |
| Bajada | `fall` | Límite descendente independiente: valor × 2,55 × dt |
| Filtro EMA | `filterMs` | Constante temporal en ms; cero desactiva |
| Histéresis | `hysteresis` | Banda en niveles lógicos externos 0–255 |
| Confirmar descenso | `downHoldMs` | Tiempo sostenido antes de bajar el objetivo |
| Duración de arranque | `startMs` | Duración del pulso en ms |
| Salida de arranque | `startOutput` | Salida lógica del pulso 0–255 |
| Salida externa en Manual | `manualOutput` | Salida fija en Manual con entrada Demanda |

Editar modifica el borrador. **Guardar y enviar curva** valida, genera una
revisión nueva, guarda las preferencias y reconfigura la conexión. Tras negociar
capacidad, Windows envía la configuración completa cada segundo dentro de la
telemetría autenticada. El ESP32 valida todos los campos antes de aceptarlos,
los copia por cola y aplica en la tarea de control de 20 ms. La respuesta firmada
confirma `appliedConfigRevision` en una trama posterior: recibir una trama o
guardar Windows no equivale a confirmar aplicación. Monitorización puede guardar
configuración sin conceder Remote.

EMA, histéresis, confirmación de descenso, pulso y rampas de **Curva Externa se
ejecutan en ESP32**. Windows configura los parámetros. La curva interna HP tiene
su propio control en Windows y no se modifica. El EMA ambiental de NTC (factor
0,25 por muestra) es independiente de `filterMs`. El pulso se aplica al iniciar
el controlador y al salir de parada real; guardar o reconectar no lo repite sobre
un ventilador en marcha. Al encender sin NTC válida prevalece máximo por seguridad.

Se conserva la NVS `victus-base`/`curve-v1` y su formato. El guardado NVS puede
demorarse por el espaciado mínimo de 30 s; un corte antes de persistir puede cargar
la revisión anterior, que Windows vuelve a enviar al reconectar.

## Pruebas reproducibles

- `presentation.h`: dos ciclos y rollover, límites exactos 30/5/5/5, 16×2,
  ambiente independiente del PC, DEMANDA independiente de NTC, nulos/cero,
  fallo NTC y fallo/recuperación de cada transacción I2C.
- `response.h`: valores no predeterminados de subida, bajada, EMA, histéresis,
  confirmación y duración/salida de arranque, y ausencia de pulso por reconfigurar.
- `ProductGuiSelfTest.ExternalCurve`: los ocho controles llegan al campo correcto,
  persisten con unidades intactas y no escriben autoridad del notebook.
- `FirmwareInterop.Tests`: cliente TCP de producción contra parser/controlador
  C++ real, con configuración predeterminada, valores distintos, ceros y máximos.
  Compara todos los campos y puntos recibidos por C++, y verifica la revisión
  aplicada del ACK firmado posterior. El informe de valores es un canal exclusivo
  del ejecutable de pruebas; no cambia el protocolo de producción.
- `test_protocol.py`, `test_external_config.py`, `test_presentation.py`: firmas,
  rechazo de tramas/configuraciones inválidas, frescura y exportación Arduino
  idéntica a las fuentes canónicas, sin `config.local.h`.

Estas pruebas son de software. La recepción física en la placa, señal GPIO,
NTC real, LCD y giro requieren prueba en el ESP32. No se programa la placa ni se
modifica la instancia instalada del programa durante esta revisión.

Validación local: compilación C++ con advertencias tratadas como errores;
34/34 vectores de protocolo; configuraciones host y presentación/exportación;
cuatro intercambios completos Windows↔C++ con confirmación y retirada de Remote;
suite ExternalCooling; compilación Windows sin errores/advertencias y prueba de
los ocho controles en la interfaz. La compilación de placa se verifica mediante
el workflow `esp32-firmware`, fijado a Arduino-ESP32 3.3.2/ArduinoJson 6.21.5 y
PlatformIO 6.1.18 (imágenes de monitorización y PWM); la instalación local del
core Arduino está incompleta y la herramienta RISC-V local no pudo ejecutarse.
