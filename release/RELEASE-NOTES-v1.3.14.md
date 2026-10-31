# VictusFanControl 1.3.14 preliminar

Corrige el porcentaje vacío de los HyperX Cloud Flight con batería muy baja. La tabla incorpora un tramo provisional de 3200 mV → 0 % a 3584 mV → 10 %, manteniendo los valores superiores. Los avisos de batería baja y crítica pueden activarse también en este tramo con los umbrales guardados.

- La información de batería indica «estimación provisional» por debajo de 3584 mV. Los registros distinguen la curva nueva; no se presenta como calibración física ni como medición de autonomía.
- Los valores ≤10 % no esperan al suavizado. Un 0 % estimado válido se transmite a la base externa; una consulta fallida, un apagado explícito o un dato vencido siguen siendo desconocidos.
- La LCD conserva el último porcentaje conocido con `*` aunque los audífonos dejen de responder y el PC continúe conectado. Conserva el cero válido, no inventa datos al arrancar y no renueva la antigüedad ni rearma los avisos por retener una lectura.
- Conserva los límites CPU/GPU independientes, PL1 hasta PL2 y todas las preferencias. El decodificador admite de 3000 a 4500 mV, conservando longitud y cabecera estrictas. Conserva consultas HID, frecuencia de lectura, audio y protecciones térmicas.

Validación de software: voltajes registrados hasta 3308 mV, recorrido monótono de 0–100 %, avisos con el umbral crítico de 5 %, cero en exportación autenticada, caducidad y apagado. Una consulta real sin cargador obtuvo 3280 mV; la curva nueva indica aproximadamente 2 %. El extremo de 3200 mV es provisional y la guarda de 3000 mV no es un voltaje de apagado medido. No se ha demostrado precisión física del porcentaje.

Preliminar optativa: activar «Incluir versiones preliminares». 1.3.6 permanece como última estable. La retención de batería con el PC conectado requiere el sketch ESP32 de esta versión; el instalador Windows no lo flashea. El cambio del firmware afecta a la presentación de batería; conserva NVS, pines, HMAC y control de ventiladores. No se publican registros privados ni credenciales.
