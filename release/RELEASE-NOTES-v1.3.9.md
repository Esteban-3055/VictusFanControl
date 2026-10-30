# VictusFanControl 1.3.9 preliminar

Corrige la refrigeración previa al apagado: Windows podía terminar los guardianes antes de que la aplicación completase sus 20 segundos. La aplicación y los guardianes configuran y verifican un orden de cierre separado; se conservan las comprobaciones de autoridad y la liberación supervisada.

- AC confirmado: fase de hasta 20 segundos con solicitud nominal 5000 RPM, seguida de liberación con presupuesto independiente de hasta 20 segundos. Una liberación rápida no espera artificialmente otros veinte segundos.
- Diagnóstico preciso de cancelación, duración y resultado final; vaciado opcional de registros dentro del plazo existente.
- Nuevo ensayo reproducible de veinte segundos reales, ventanas y auxiliares con IPC, sin hardware ni apagado del sistema.
- Una prueba física AC de la corrección local sostuvo 20047 ms, alcanzó lecturas nominales de 5000 RPM CPU/GPU y completó la liberación en 521 ms, con informes ligados al propietario y sin errores. Es evidencia del candidato local probado; la versión empaquetada se valida además por CI, sin atribuirle una segunda prueba física.

Los veinte segundos incluyen aceleración: en el ensayo ambos alcanzaron nominalmente 5000 RPM aproximadamente a los 15,6 segundos. WMI tiene resolución de 100 RPM; la aceptación de liberación no prueba independientemente la propiedad física del firmware. Cierre forzado o pérdida de autoridad pueden interrumpir la fase.

Se conserva HyperX de 1.3.8. Sin cambios funcionales de firmware: si el ESP32 ya tiene 1.3.7, no hace falta reflashearlo. No cambia NVS, protocolo, pines, PWM ni credenciales.

Preliminar optativa; activar «Incluir versiones preliminares». 1.3.6 sigue como última estable. La publicación no instala el programa ni flashea el ESP32. No se incluyen logs privados, claves, SSID o contraseñas.
