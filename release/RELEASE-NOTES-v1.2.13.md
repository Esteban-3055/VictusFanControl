# VictusFanControl v1.2.13 · Preliminar 5 · Curva Externa y firmware Work 0.4

## Cambios

- Nueva pestaña lateral **Curva Externa**, integrada con el estilo del programa: puntos editables, arrastre, valores exactos, añadir/quitar puntos, restaurar, guardar y descartar. La curva de la base es independiente de las curvas del notebook.
- Fuente configurable entre **Demanda** y **Solicitud aceptada**; calibración explícita del rango de Solicitud y salida externa de **30 a 255**. Demanda y Solicitud conservan significados y validez distintos.
- Ajustes de suavizado, histéresis, inercia, rampas y pulso de arranque enviados al dispositivo autenticado. Se confirma la revisión de configuración aplicada; un dispositivo anterior conserva el protocolo de telemetría sin interpretar la extensión nueva.
- Firmware consolidado para **ESP32-C3 SuperMini**: Wi-Fi **8,5 dBm**, GPIO **3 PWM / 6 SDA / 7 SCL / 0 NTC**, LCD 1602 I²C, NTC ambiental y PWM invertido de **18 kHz y 8 bits** para la etapa 2N7000 + BC337. Pérdida de comunicación y sensores inválidos se gestionan localmente; las pruebas físicas siguen pendientes.
- Se conserva la clave de emparejamiento guardada. Guardar preferencias desde otra pestaña ya no puede sobrescribir una modificación reciente de la base o su curva.
- Recuperación automática acotada de sesiones reconocidas y continuación de la instalación tras recuperar una sesión válida, respetando las preferencias y las barreras existentes. Los fallos de instalación conservan la descarga y restauran el monitor. Una transacción nativa incierta sigue bloqueando una recuperación no demostrada.

## Actualizar desde el programa

1. En **Actualizaciones**, activar **Incluir versiones preliminares**.
2. Ir a **Configuración → Guardar preferencias** para conservar esa elección.
3. Volver a **Actualizaciones → Buscar actualizaciones → Descargar → Instalar**.

La etiqueta, aplicación e instalador usan **1.2.13**, compatible con el actualizador de 1.2.9 y las preliminares anteriores. Se conservan las preferencias y el emparejamiento. Requiere **.NET Desktop Runtime 8 x64**. La versión estable **1.2.9** permanece en el canal estable.

Si el actualizador de la versión instalada ya estaba bloqueado por una transacción nativa incierta, este paquete no puede ejecutar su recuperación antes de instalarse. Se conserva ese bloqueo y la evidencia; no se borran registros para forzar la instalación.

## Firmware y código desde otro dispositivo

- [Código Windows de esta versión](https://github.com/Esteban-3055/VictusFanControl/tree/v1.2.13/src).
- [Firmware, conexiones e instalación con Arduino IDE o PlatformIO](https://github.com/Esteban-3055/VictusFanControl/tree/v1.2.13/firmware/esp32-c3).
- [Contrato de Curva Externa](https://github.com/Esteban-3055/VictusFanControl/blob/v1.2.13/docs/EXTERNAL-CURVE-PROTOCOL.md).
- [Descargar el código completo](https://github.com/Esteban-3055/VictusFanControl/archive/refs/tags/v1.2.13.zip).

El simulador Windows es una herramienta separada y requiere .NET 8 Runtime x64. Actualizar VictusFanControl **no carga firmware** en la placa. El firmware anterior no incorpora automáticamente la nueva curva: hay que compilar y cargar la consolidación según su README.

## Validación y límites

Publicación condicionada a CI satisfactorio del SHA exacto: compilación Release, instalador y paquete íntegros, regresiones internas, TCP bidireccional, ciclo de vida WinForms, interoperabilidad C#/C++, protocolo, curvas y compilaciones de firmware con Arduino 2 y 3. Se verifica la detección y descarga pública con el código original del actualizador 1.2.9, sin ejecutar el instalador.

Estas verificaciones no acreditan funcionamiento físico del ESP32. Quedan pendientes la forma de onda de 18 kHz, arranque del motor, etapa de potencia y temperatura, LCD/NTC y la secuencia real batería → suspensión → AC. La implementación externa no cambia las curvas, límites ni protecciones de los ventiladores internos; los detalles de recuperación y sus límites figuran en `docs/UNATTENDED-RECOVERY.md`.
