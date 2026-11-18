# Consolidación Work 0.4 — auditoría inicial (2026-10-16)

Base: `6d86c92b7a921b87e74a8de324ef6045b05cca5e`, rama nueva
`feature/external-curve-firmware`. Se conservan los PR pendientes #41, #42 y #43.
GitHub consultado: estable v1.2.9; preliminar v1.2.12 (código 5c1411a).
La rama de publicación `release/external-cooling-preview-4` apunta a 91eb1db.
El árbol de firmware de referencia coincide con el de la base de trabajo.

Paquete recibido: `VictusBaseESP32C3_Work_0_4_Consolidado.zip`, SHA256
`e542835e94dea0a0eedac0b320c756f15489521049741f7fa85eebbfcd6e6ff1`.
Se leyeron primero README, INSTRUCCIONES_WORK y VALIDACIONES y después sus
nueve archivos. Las ocho entradas del manifiesto coinciden. Sus seis pruebas
estáticas pasan; no representan compilación ni funcionamiento físico.

Base comprobada antes de modificar: Release Windows sin errores/advertencias;
33/33 casos del motor C++ de referencia (vectores HMAC, JSON hostil, caducidad,
calibración e inversión). Ninguna escritura al hardware del notebook.

Decisiones:

- Una sola implementación en `firmware/esp32-c3`, conservando el parser estricto,
  HMAC sobre bytes exactos, lease independiente de 2 s y tarea FreeRTOS de control.
  El sketch del ZIP reduce las validaciones y comparte control con red/LCD.
- Incorporar Wi-Fi 8,5 dBm y evitar repetidos `WiFi.begin`, GPIO 3/6/7/0,
  detección PCF8574, LCD 1602, NTC 10k B3950 y salida invertida 2N7000/BC337.
- La instrucción actual del usuario **18 kHz y 30–255** sustituye los 18 Hz y
  14 bits del paquete. Resolución lógica de 8 bits. Validación eléctrica pendiente.
- No hay fuentes del antiguo controlador de tres NTC en el ZIP. El usuario
  autoriza adaptar un perfil configurable: subida 12 %/s, bajada 6 %/s,
  EMA, histéresis, confirmación de descenso y arranque temporizado.
- Curva Externa independiente de los perfiles, SafetyGate, control WMI y
  Performance Control. Exportar datos existentes; el ESP32 aplica su curva.
  Configuración adicional versionada, autenticada y confirmada por revisión.
- `slot1`/`slot2` permanecen reservados e inertes. Una curva nunca concede
  autoridad: requiere Solicitud aceptada, vigente, modo permitido y lease.

Puntos de integración Windows: TelemetryExport/Esp32TcpClient, configuración
DPAPI existente, ProductForm/ProductCanvas y sección lateral propia.
Puntos firmware: VictusProtocol, controlador portátil, BoardConfig y main.

Riesgos físicos registrados: arranque de motor a duty 30 no demostrado; BC337
con poco margen frente a los ~700 mA informados de arranque; disipación de base,
transitorios y 18 kHz sin medir. GPIO flotante con pull-down puede significar
ventilador al máximo incluso en imagen de monitorización. No confundir ACK I²C
con caracteres visibles ni PWM aplicado con giro real. No se realizaron cargas
en la placa ni mediciones eléctricas durante esta auditoría.

## Nota posterior (2026-10-16): selección física de 18 Hz

Esta auditoría describe históricamente la decisión inicial de 18 kHz/8 bits
en el PR #44; no debe tomarse como la configuración vigente. El usuario
restauró 18 Hz, pero una combinación inicial 18 Hz/8 bits hizo fallar LEDC
y reiniciar el dispositivo antes de establecer Wi-Fi. La revisión posterior
mantiene la curva externa de 8 bits lógicos, configura LEDC a 18 Hz/14 bits
y escala el duty invertido al rango físico 0–16383. La validación física
del ventilador y la electrónica continúa pendiente.
