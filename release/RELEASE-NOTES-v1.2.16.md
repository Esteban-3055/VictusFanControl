# VictusFanControl 1.2.16

Release estable para uso habitual en HP 8C40/F.18.

- Automático habitual devuelve el control al firmware cuando la GPU supera **95 °C**. A 95 °C continúa; entrada y reanudación usan el mismo criterio. No se trata de apagar el equipo ni los ventiladores. La CPU mantiene sus protecciones térmicas.
- Cambios AC/batería: cada cambio de fuente exige una confirmación nueva del supervisor. Corrige la aceptación de una respuesta anterior al último rebote. La espera total pasa a 8 s sin renovar el plazo por cambios repetidos y sin ampliar la frescura de sensores ni permitir nuevas órdenes durante la espera.
- Se elimina el corte habitual por consumo CPU >60 W y su interruptor antiguo; GPU configurable hasta **2535 MHz**, conservando perfiles y PL1/PL2. Las revisiones físicas explícitas conservan sus márgenes históricos.
- Incluye **Recuperar sesión definitiva** y la recuperación automática acotada de fallos persistentes. Conserva la evidencia de llamadas nativas inciertas y no promete recuperar cualquier bloqueo del driver sin reiniciar.
- Incluye Curva Externa 0–255, referencia verde de demanda y firmware ESP32-C3 con ciclo LCD 30/5/5/5 s, lecturas frescas y `--` para valores ausentes. El firmware no cambia respecto de 1.2.15; no es necesario reflashear por los cambios de GPU/batería.

Instala `VictusFanControl-1.2.16-Setup-win-x64.exe`, o busca actualizaciones desde la aplicación. El canal estable permite descubrir esta versión sin activar preliminares. El instalador conserva perfiles y verifica que la sesión anterior haya liberado sus controles.

Los paquetes incluyen hashes y procedencia del commit exacto. CI valida el programa y el firmware (PlatformIO y Arduino ESP32 3.3.2 con ArduinoJson 6.21.5). Las comprobaciones nuevas no escriben al hardware: la observación física bajo carga del umbral GPU y de la reconciliación AC/batería sigue pendiente. No se afirma un nuevo PASS físico ni se incluyen claves, SSID o contraseñas.

Detalles: [GPU y alimentación](../docs/HABITUAL-GPU95-AND-POWER-SOURCE.md), [recuperación](../docs/FORCED-SESSION-RECOVERY.md), [CPU y rango GPU](../docs/CPU-CONSUMPTION-AND-GPU-RANGE.md).
