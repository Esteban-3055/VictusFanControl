# Curva Externa: rango 0–255 y punto actual

## Análisis antes de pruebas

Se revisaron editor numérico/gráfico, esquema C#, protocolo firmado, simulador,
controlador ESP32 y presentación LCD. El mínimo estaba impuesto en varias capas:
cambiar solo el eje o la validación de Windows no permitiría detener la base.
El piso ambiental fijo de 30 impedía cero incluso con una curva aceptada; además,
EMA e histéresis podían conservar una salida residual. Se habilita el rango
completo únicamente para la base externa: los ventiladores internos HP no
cambian sus límites ni su supervisor de Windows.

## Decisiones

- Rango de puntos, Manual y salida de arranque: 0–255. Se conserva el perfil
  inicial existente (primer punto 30); el usuario elige qué tramos llevar a cero.
- Se elimina el piso de 30 en Remote con ambiente ≤35 °C. Por encima de 35 °C
  se conserva el piso térmico anterior; NTC inválida o ≥45 °C fuerza 255.
  Local mantiene mínimo 89. El lease y HMAC siguen siendo obligatorios.
- La petición cero llega a parada exacta tras filtro, confirmación y rampa.
  Salir de parada permite asistencia de arranque; renovar red no la reinicia.
- Capacidad opcional firmada `externalMinimumOutput:0`. No se envían valores
  menores de 30 ni autoridad remota a firmware antiguo cuando la curva los exige.
  La aplicación indica que hace falta actualizar ESP32. NVS mantiene su formato.
- ○ objetivo amarillo y ● aplicado verde, con el estilo de la curva interna.
  El objetivo usa interpolación para vista previa; PWM se sigue calculando en ESP32.
  La fuente se correlaciona por ACK, revisión y frescura. La salida aplicada
  puede diferir del objetivo por EMA, rampas o temperatura ambiental.

## Pruebas preparadas para CI remoto

No se ejecutan pruebas en el PC del usuario, no se inicia la aplicación contra
su configuración real y no se envían órdenes a hardware. Se ejecutan en runners
remotos mediante los workflows existentes de la rama/PR:

- ESP32: parada exacta con EMA/histéresis, rampa monotónica, salida cero invertida,
  protección NTC/térmica, retirada de autoridad, lease, reanudación, pulso de
  arranque, valores positivos menores que histéresis y arranque deshabilitado.
- Parser firmado: rango completo, parámetros cero, rechazo de negativos y >255,
  conservación de HMAC/autoridad y capacidad anunciada.
- Windows: validación 0–255, compatibilidad con firmware anterior, cero en estado,
  editor y guardado, marcadores separados, datos caducados/nulos/desconectados,
  Demanda=0 y normalización de Solicitud. Render de la interfaz en varias escalas.
- Compilaciones: Windows Release, PlatformIO monitorización/PWM y exportación
  Arduino-ESP32 3.3.2 con ArduinoJson 6.21.5.

Resultados y enlaces del commit actual se documentan en el PR #44. La revisión
física de parada/arranque y señal invertida queda pendiente; no se deduce de los
tests host ni del simulador.
