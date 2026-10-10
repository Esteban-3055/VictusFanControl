# VictusFanControl v1.1.1 · Protecciones

Entrega preliminar para el destino validado **HP 8C40 / 9D0R1LA / BIOS F.18**, i7-13700H y RTX 4060 Laptop.

- Nueva pestaña **Protecciones**: cortes por temperatura CPU, temperatura GPU y margen de potencia configurables por separado. Activados de fábrica.
- Desactivar un corte térmico permite continuar Automático con la curva y el enfriamiento máximo ante temperatura alta. No cambia el throttling ni las protecciones del firmware.
- **Reanudar Automático** activado de fábrica para una sesión que ya estaba funcionando: exige liberación completa del controlador anterior y CPU/GPU, ausencia de recuperación pendiente y tres lecturas nuevas. Esperas de 10/30/60 s, máximo tres intentos hasta recuperar un minuto de funcionamiento estable. Una operación WMI incierta o una liberación fallida bloquea la reentrada.
- Reanuda los ajustes que estaban aplicados; conserva el borrador sin aplicar cambios pendientes. Elegir Firmware o Manual cancela la reanudación.
- **Guardar** conserva las preferencias. **Aplicar y reanudar** libera el control actual y prepara Automático con los nuevos ajustes.
- Corrige la escritura concurrente de `stop.signal` entre GUI y guardián, observada en un diagnóstico que no completó la recuperación.
- Incluye las correcciones anteriores de recuperación desde programa/instalador, reloj de arranque Windows, iconos y Actualizaciones.

Descarga **VictusFanControl-1.1.1-Setup-win-x64.exe**. También se adjuntan el paquete ZIP, sus SHA-256 y la procedencia de la compilación. Se conservan perfiles existentes; requiere los mismos componentes PawnIO/NVIDIA de la versión anterior.

Verificación de software Windows/Linux y replay de 390 decisiones, sin comandos de hardware. La página está comprobada por render a resolución mínima 1040×660. El comportamiento físico de los nuevos ajustes y de la reanudación queda pendiente de observar en el portátil; por eso esta entrega figura como preliminar. Las pruebas físicas anteriores conservan su alcance original.
