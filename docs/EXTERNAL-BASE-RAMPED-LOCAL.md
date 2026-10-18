# Base externa: ambiente informativo y desconexión a 130

La base sigue la curva externa sin aumentos automáticos por temperatura ambiente,
NTC inválida o muestra vencida. Se retiran máximo forzado y piso ambiental.
El NTC conserva conversión, validación, caducidad y diagnóstico, pero solo informa
en LCD/TCP/USB. Un NTC ausente se transmite como `ambientTempC:null`; una sesión
autenticada sigue indicando `communication:Ready`, sin inventar un fallo de TCP.

## Al perder una orden válida

Desconexión del PC/Wi-Fi, lease vencido, Firmware/monitorización o petición no
admitida dejan Remote y pasan a Local. **El objetivo Local es 130/255**, de acuerdo
con la petición del usuario, no un porcentaje de 130 ni el valor Manual editable.
Se llega a él con el mismo EMA, histéresis, confirmación de descenso y rampas,
sin elevar instantáneamente una salida baja ni bajar de golpe una salida alta.
Un anclaje al asentarse elimina el residuo de histéresis: llega a 130 exacto.
No se renuevan órdenes remotas caducadas ni se retiene autoridad por desconexión.

## Arranque y salida cero

El software inicializa salida lógica cero (señal invertida a máximo eléctrico),
y se dirige a 130 por rampa hasta recibir una orden válida. No depende de que
llegue la primera lectura NTC. No se ejecuta un pulso de arranque en Local.
Esto no prueba que la etapa física no tenga transitorios durante reset/boot ROM.

El pulso configurado por el usuario se conserva únicamente para una orden remota
positiva al partir de cero. Para eliminar también ese pulso, guardar Máxima
suavidad: `rise=fall=1`, `filterMs=10000`, `hysteresis=3`, `downHoldMs=30000`,
`startMs=startOutput=0`. No se alteran automáticamente los ajustes ya guardados.
La salida remota cero se permite independientemente del estado NTC.

## Alcance y compatibilidad

- Solo cambia el controlador de la base. Los ventiladores internos HP conservan
  sus propias protecciones, autoridad y adquisición; no se añade acceso WMI/EC.
- HMAC direccional, emparejamiento, validación completa, secuencias, antigüedad,
  límite de trama y lease 2000 ms permanecen activos.
- GPIO3 invertido 18 Hz/14 bits, GPIO0 NTC, GPIO6/7 I2C y Wi-Fi 8.5 dBm se conservan.
- No cambia el payload de configuración ni el formato/namespace NVS. Las salidas
  lógicas permanecen en 0–255. El LCD mantiene 30/5/5/5 s y PWM visible sin NTC.
- Requiere cargar este firmware en ESP32. Actualizar Windows o guardar ajustes
  en una placa con firmware anterior no elimina los límites ambientales antiguos.

## Evidencia

`external_only.h` prueba el controlador real con ambientes -100, -40, 24, 35,01,
40, 45, 100, 101, NaN e infinito: ninguna diferencia de salida/autoridad frente
a 24 °C, tanto durante Remote como al vencer el lease. Comprueba arranque sin
orden/NTC, cero sin pulso, rampas de 1 %/s y asentamiento exacto en 130 desde
ambos lados. `smoothing.h` conserva escalones, retorno a cero y el cruce 47→48 %.

El parser autenticado se prueba sin NTC, con nulidad ambiental, comunicación
Ready, Remote y sin máximo automático. Se mantienen pruebas de MAC/secuencias,
tramas hostiles, configuración y exportación Arduino. Windows↔C++ compara cinco
configuraciones y ACK aplicado. Son pruebas de software; señal, LCD, NTC física
y arranque/giro a baja salida siguen sin comprobarse en la placa.
