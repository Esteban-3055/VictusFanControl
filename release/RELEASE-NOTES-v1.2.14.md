# VictusFanControl v1.2.14 · Preliminar 6 · Parada externa y marcadores en vivo

## Cambios

- **Curva Externa 0–255**: permite configurar tramos de parada y valores de Manual/arranque desde cero. Conserva las curvas guardadas y el perfil inicial con mínimo 30; cada usuario decide qué puntos llevar a cero.
- **Referencia verde en vivo** en Curva Externa, con el mismo dibujo que la curva interna CPU/GPU. El objetivo amarillo y la salida aplicada verde siguen visibles aunque coincidan. Los datos se correlacionan por respuesta autenticada y caducan; no se muestra una salida aplicada inventada.
- **Corrección de la curva interna en 1.2.13**: el marcador de solicitud ya no desaparece en los niveles 51–54. Este cambio es de presentación; los límites CPU 54/GPU 52 y el control HP se mantienen.
- **Parada y reanudación en ESP32**: llega a cero exacto tras suavizado, confirmación y rampa. Al salir de parada usa el arranque configurado, también con EMA/histéresis desactivadas. El programa comprueba compatibilidad antes de enviar valores menores de 30 a un firmware anterior.
- **LCD1602**: ciclo de temperaturas CPU/GPU + DEMANDA durante 30 s; uso CPU/GPU/RAM durante 5 s; ambiente + PWM durante 5 s; PC + Wi-Fi durante 5 s. Filas de 16 caracteres, refresco 250 ms, datos nulos/caducados `--`, sin mezclar NTC ambiental con CPU/GPU.
- **Recuperación LCD/NTC**: publicación coherente de valor y fecha del NTC entre tareas; resincronización del LCD tras error I²C y reescritura de datos actuales. Se conservan los comandos USB `ntc` y `lcd`.
- Firmware ESP32-C3 con GPIO3 PWM invertido **18 Hz / 14 bits**, GPIO0 NTC, GPIO6/7 I²C, Wi-Fi **8,5 dBm**, HMAC TCP **8765** y el mismo almacenamiento NVS.

## Actualizar desde el programa

1. En **Actualizaciones**, activar **Incluir versiones preliminares**.
2. Ir a **Configuración → Guardar preferencias**.
3. Volver a **Actualizaciones → Buscar actualizaciones → Descargar → Instalar**.

Etiqueta, aplicación e instalador usan **1.2.14**, para mantener compatibilidad con el actualizador original 1.2.9. La publicación está marcada **Pre-release**. La estable **1.2.9** sigue en el canal estable. Se conservan preferencias y emparejamiento. La aplicación requiere **.NET Desktop Runtime 8 x64**.

## Actualización ESP32 necesaria para usar cero

Actualizar Windows **no carga firmware** en la placa. Para aplicar el rango 0–255 hay que actualizar también el ESP32 con las fuentes o el sketch Arduino incluidos. El firmware conserva la clave/SSID/contraseña en NVS si se actualiza sin borrar toda la flash. Los archivos publicados no contienen credenciales privadas.

La parada requiere control remoto autorizado y NTC válida/vigente con ambiente **≤35 °C**. Por encima se mantiene la protección ambiental. A **≥45 °C** o fallo de NTC se fuerza 255; al perder autoridad/conexión se vuelve a Local, mínimo 89. Cero es una orden eléctrica: sin tacómetro no se demuestra parada/giro mecánicos.

- [Firmware e instrucciones Arduino IDE / PlatformIO](https://github.com/Esteban-3055/VictusFanControl/tree/v1.2.14/firmware/esp32-c3).
- [Análisis de parada y marcadores](https://github.com/Esteban-3055/VictusFanControl/blob/v1.2.14/docs/EXTERNAL-CURVE-ZERO-OUTPUT.md).
- [Protocolo de Curva Externa](https://github.com/Esteban-3055/VictusFanControl/blob/v1.2.14/docs/EXTERNAL-CURVE-PROTOCOL.md).
- [Código completo](https://github.com/Esteban-3055/VictusFanControl/archive/refs/tags/v1.2.14.zip).

## Verificación y límites

Publicación condicionada al CI del SHA exacto: build Windows y empaquetado, comprobación de instalador/ZIP y SHA-256, regresiones internas, interfaz renderizada, TCP/HMAC y configuración, interoperabilidad Windows↔C++, layout/timing/frescura LCD y compilaciones **Arduino-ESP32 3.3.2 + ArduinoJson 6.21.5** y **PlatformIO**. Se verifica detección y descarga pública con el actualizador original 1.2.9, sin ejecutar el instalador.

Las pruebas se realizan en CI remoto, sin ejecutar pruebas ni accionar ventiladores en el PC del usuario. Quedan pendientes pruebas físicas del ESP32: señal 18 Hz/inversión, parada y arranque, LCD/NTC y disipación/corriente de la etapa. No se afirma PASS físico. Las barreras de recuperación y la transacción nativa incierta se conservan.
