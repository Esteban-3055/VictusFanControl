# Inicio: animación por RPM medidas

Versión: v1.2.5, basada en v1.2.4. Este ajuste afecta únicamente a la presentación.

- Cada ventilador usa su propia última RPM válida (0–10000), sin depender de demanda, solicitud ni modo automático/manual/firmware.
- Escala visual: 3000 RPM = 1 vuelta/s; 1000 RPM = 1 vuelta/3 s; 5000 RPM = 1 vuelta/0,6 s. La velocidad se aproxima gradualmente, con 1,2 s de constante de aceleración y 1,8 s de desaceleración.
- Los retrasos, muestras inválidas y estados de telemetría no disponibles conservan el número y el objetivo visual. No reinician fase ni inercia. Un cero medido es válido y provoca desaceleración hasta detenerse.
- Las 28 aspas son iguales. Se eliminan el aspa y punto blancos, el texto «última lectura» y los cambios de color por lecturas retenidas.
- Otra pestaña, ventana oculta o minimizada: temporizador detenido, fase y velocidad conservadas. Al volver no se integra tiempo oculto. La restauración de ventana sincroniza el temporizador inmediatamente.
- Antes de la primera lectura se muestra «Esperando», sin fabricar RPM ni giro.

La caché ya existente sigue siendo exclusiva de presentación: no modifica snapshots, reglas de seguridad, control físico ni historial/gráfico de RPM medidas. Si la adquisición se pierde por completo, no es posible detectar una parada física nueva hasta recibir otra muestra; se conserva la última observación.

Validación: fixtures matemáticos de animación y de retención independiente; fixtures WinForms de publicaciones repetidas, adquisición vencida, modos firmware/manual, parada de un ventilador, parada de ambos, pausa/restauración y tamaños de pantalla. La suite Windows también genera capturas de Inicio y verifica ausencia de escrituras de hardware/preferencias.

## Estado de comprobación de este cambio

- PASS: compilación directa con Roslyn y ejecución real de `ProductFanAnimationSelfTest` sobre .NET 8.0.25 en Linux; escala, inercia, muestras ausentes/invalidas, cero medido, temporización irregular y límite de recuperación de cuadros.
- PASS: revisión del diff y `git diff --check`.
- Pendiente: compilación de la solución y ejecución WinForms en Windows. El MSBuild local falla al consultar metadatos del proceso en este entorno; no constituye un resultado de compilación del código. La subida y publicación de v1.2.5 fueron autorizadas por el usuario; la verificación completa se ejecuta en GitHub Actions Windows.
