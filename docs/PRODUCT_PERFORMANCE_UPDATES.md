# Aplicar límites CPU/GPU durante el uso

El panel Rendimiento distingue valores editados y valores aplicados. «Aplicar
cambios» se habilita cuando los presets AC/Batería difieren de la configuración
confirmada de una sesión activa. Guardar solo conserva preferencias. Cambiar una
curva o Avanzado no aplica límites de potencia ni de clock.

La actualización conserva el proceso del Guardian, los IDs de journals, el
baseline CPU original y las curvas/inercia/plazo de la activación Automático.
CPU utiliza SwitchOwnedPreset con intent durable y readback de campos propios;
GPU usa SwitchPreset (Set directo), aceptado por NVML sin lectura independiente
del rango completo. No hay Reset ni restauración intermedia en una actualización.
Los presets actualizados también se usan al cambiar alimentación posteriormente.

La selección de dominios se mantiene durante la sesión. Los checkbox indican esa
selección y quedan bloqueados mientras exista el Guardian; Desactivar límites
CPU/GPU conserva su acción independiente. Automático requiere ambos dominios.
Los valores de un dominio inactivo pueden prepararse sin escritura a ese dominio.

## Confirmación, concurrencia y fallos

UPDATE_CONFIGURATION lleva únicamente presets validados del producto. HELLO,
nonce, PID/start del propietario, destino y límites de trama siguen obligatorios.
El Guardian serializa la actualización con alimentación y display/suspensión:
consulta directa de fuente conocida/estable, actualización y nueva consulta.
Display Off, suspensión, sesión liberada, ownership degradado o fuente pendiente
rechazan el cambio. No se agrega una ruta de recuperación automática.

STATUS y la respuesta de Apply incluyen la configuración comprometida por cada
dominio. CPU y GPU no son una transacción atómica: se intentan independientemente
y un fallo no inventa éxito conjunto ni rollback. La GUI conserva la información
anterior o parcial y el estado de cada dominio. Si se pierde la respuesta, STATUS
puede recuperar la configuración comprometida; no se retransmite Apply a ciegas.

Durante Automático la espera tiene 4000 ms monotónicos, sin renovar el plazo de
revisión. Continúan las adquisiciones y protecciones; se conserva el nivel actual
sin nuevas escrituras de ventiladores mientras se espera. CPU ≥85 °C, GPU ≥78 °C,
cambio de fuente, pérdida de telemetría, expiry o falta de confirmación interrumpen
la espera y solicitan Firmware. Las protecciones térmicas crudas siguen activas.
Un fallo de actualización bloquea esa activación; se conservan los journals.

Diagnósticos registran UPDATE STARTED / APPLIED / FAILED, valores solicitados,
configuración comprometida, estados del Guardian, ID Automático y tiempo restante.
PerformanceUpdating diferencia una actualización en curso.

## Regresión física breve aprobada en AC

En HEAD 34388b8, sesión 5e6ef5a8: CPU 25/40 → 25/38 W y respuesta
CONFIGURATION_UPDATED, misma activación y continuidad posterior sin bloqueo.
En sesión bf445702: GPU 1800 → 1750 MHz, CONFIGURATION_UPDATED, misma activación,
CPU 28/38 W conservada y continuidad posterior Healthy. GPU sigue ActiveUnverified:
la aceptación NVML no verifica de forma independiente el rango completo.
Resumen ligado a SHA256 de ambos ZIP en
release/product-gui-live-performance-8c40-2026-10-06.json.

Procedimiento conservado como referencia:

1. Salir normalmente de la versión anterior, extraer el nuevo ZIP y Verify.
2. Con AC y actividad ligera, preparar CPU 25/40 W; iniciar Automático y esperar
   Healthy/CPU Active/GPU ActiveUnverified. No activar otro controlador de límites.
3. Editar PL2 a 38 W: CPU aplicada debe seguir mostrando 25/40 y Apply habilitarse.
   Pulsar Aplicar cambios: esperar CPU 25/38, Apply deshabilitado y Automático
   conservado. Exportar el diagnóstico sin repetir carga prolongada.

Los fixtures cubren journals/baseline, presets siguientes, Reset final, commits
parciales, bloqueo por fuente/display, límites temporales, IPC con mismo proceso
y edición/confirmación del botón. El alcance físico observado aquí corresponde a AC;
no acredita por sí solo display/suspensión/salida posteriores ni fallos deliberados.
