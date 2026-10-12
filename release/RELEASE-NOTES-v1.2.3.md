# VictusFanControl v1.2.3 · estable

- **Giro visual tres veces más rápido en Inicio**: el máximo aumenta de 24 a 72 grados por segundo. Una vez estabilizada la inercia, da una vuelta cada 5 segundos a 100 % de demanda, cada 10 segundos a 50 % y cada 25 segundos a 20 %.
- Conserva la demanda automática aceptada como referencia, la aceleración gradual y la desaceleración más prolongada. Los retrasos de RPM no reinician la inercia.
- Mantiene las pausas al ocultar/minimizar, cambiar de página, interrumpir Automático o caducar la demanda; cero demanda desacelera hasta reposo. Las cifras centrales siguen mostrando las RPM informadas.

La actualización conserva perfiles, curvas, filtros térmicos, protecciones, retención opcional TZ01/DTT3 y recuperación automática. El control físico sigue sincronizado; el cambio de velocidad afecta únicamente a la animación.

Verificación: compilación y fixtures de velocidad visual, arranque suave, cambios de demanda, parada finita, intervalos irregulares y pausa/reanudación; interfaz y renders Windows, regresiones y prueba prolongada de recursos. El instalador y el ZIP se verifican contra el manifiesto y se publican con SHA-256 y procedencia.

Detalles: [Inicio y velocidad visual](../docs/HOME_DASHBOARD.md).
