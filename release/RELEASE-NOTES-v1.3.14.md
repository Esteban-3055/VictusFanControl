# VictusFanControl 1.3.14 preliminar

Corrige el porcentaje vacío de los HyperX Cloud Flight con batería muy baja. La tabla incorpora un tramo provisional de 3300 mV → 0 % a 3584 mV → 10 %, manteniendo los valores superiores. Los avisos de batería baja y crítica pueden activarse también en este tramo con los umbrales guardados.

- La información de batería indica «estimación provisional» por debajo de 3584 mV. Los registros distinguen la curva nueva; no se presenta como calibración física ni como medición de autonomía.
- Los valores ≤10 % no esperan al suavizado. Un 0 % estimado válido se transmite a la base externa; una consulta fallida, un apagado explícito o un dato vencido siguen siendo desconocidos.
- Conserva los límites CPU/GPU independientes, PL1 hasta PL2 y todas las preferencias. No cambia firmware, consultas HID, frecuencia de lectura, audio ni protecciones térmicas.

Validación de software: voltajes registrados hasta 3308 mV, recorrido monótono de 0–100 %, avisos con el umbral crítico de 5 %, cero en exportación autenticada, caducidad y apagado. El extremo de 3300 mV es una referencia provisional elegida dentro del rango previo del decodificador, no un voltaje de apagado medido. No se ha demostrado precisión física del porcentaje.

Preliminar optativa: activar «Incluir versiones preliminares». 1.3.6 permanece como última estable. El firmware ESP32 no cambia; quien tenga 1.3.7 no necesita reflashear. No se publican registros privados ni credenciales.
