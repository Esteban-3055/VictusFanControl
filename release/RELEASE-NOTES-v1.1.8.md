# VictusFanControl v1.1.8 · estable

- Automático habitual confirma las subidas de temperatura CPU durante **2 segundos** antes de incorporarlas a la demanda. Un pico breve no dispara por sí solo el suelo térmico ni renueva la espera de descenso.
- Una lectura CPU de **99 °C o más** responde de inmediato. Las temperaturas mostradas y las protecciones conservan las lecturas crudas y sus límites anteriores.
- Las lecturas CPU retenidas durante un fallo breve del sensor no cuentan como nuevas adquisiciones calientes. El filtro vuelve a confirmar con datos frescos.
- La curva y el diagnóstico distinguen la demanda filtrada de la temperatura real; el simulador incorpora el mismo filtro. Potencia, carga y temperatura GPU mantienen su influencia.

Conserva las preferencias, el ajuste de resolución de v1.1.7 y el actualizador estable con descarga persistente e instalación separada. No requiere editar las curvas existentes.

Verificación y alcance: `docs/PRODUCT_1_1_8_CPU_SPIKES.md`. Las pruebas automatizadas comprueban la lógica; el efecto real sobre refrigeración y ruido debe observarse en el equipo.
