# VictusFanControl v1.1.4

Para HP 8C40 / BIOS F.18. Versión preliminar con correcciones de telemetría, inicio y presentación.

- La solicitud de Automático al iniciar ya no caduca a los 30 s. Espera tres adquisiciones frescas; conserva las barreras de recuperación y cancelación del usuario. La preparación puede reintentarse hasta tres veces tras liberar los límites.
- En uso habitual, las temperaturas CPU/GPU se adquieren sin esperar una consulta fresca de RPM. La lectura periódica WMI conserva una única llamada pendiente, sin consultas nativas superpuestas.
- RPM admite una antigüedad inferior a 10 s. Temperaturas y potencia pueden conservarse por menos de 5 s con sus fechas originales. Mientras faltan datos críticos no se envían nuevas órdenes ni descensos; con CPU ≥85 °C o GPU ≥78 °C se solicita Firmware antes de agotar la tolerancia. Las protecciones de calor mantienen prioridad, incluido el plazo CPU de 2 s supervisado por separado.
- La pérdida del porcentaje de uso se muestra como dato ausente y se evalúa conservadoramente como 100 % para la curva; no se presenta como una medición.
- La retención opcional TZ01/DTT3 se suspende si faltan auxiliares y continúa la curva base. Se recalifica con adquisiciones válidas sin renovar el episodio de 60 s. Las pruebas físicas históricas conservan sus límites estrictos.
- Acentos azules en AC, amarillos en batería y rojos ante error. Se conserva el fondo oscuro y el texto de estado.
- Al cambiar de fuente se selecciona el preset correspondiente para rendimiento y curvas, conservando los borradores. Una edición numérica abierta permanece vinculada al preset original.
- Escalado inicial con base de 96 DPI, PerMonitorV2 y ajuste al área útil de la pantalla. Conserva los cuatro iconos de v1.1.3.
- Los diagnósticos incluyen fechas de las lecturas retenidas y el estado de TZ01/DTT3.

Los límites nuevos se verifican con pruebas de software. Queda observarlos en el equipo real bajo carga y comprobar 4K al 200 %; no se declara una nueva cualificación física ni mejora acústica medida. La aceptación WMI sigue sin presentarse como prueba independiente de velocidad o propiedad del firmware.
