# Cambios v1.1.6

El diagnóstico de la sesión del 11 de octubre de 2026 contenía dos interrupciones por GPU >75 W (81.251 y 83.016 W) y dos rechazos genéricos de entrada. Las muestras registradas alrededor de esos rechazos no permiten identificar retrospectivamente el estado exacto que falló. No se atribuyen a un sensor perdido sin evidencia.

## Consumo GPU

Se retira el corte de 75 W en la admisión al inicio y en el guardián de la aplicación, incluidos los modos de revisión corta y extendida. La potencia GPU sigue formando parte de la demanda: el punto de normalización de la curva no es un corte de control. Los procedimientos históricos de cualificación conservan sus envolventes originales y no se reescribe su evidencia.

`PowerEnvelopeHandoff` conserva el nombre serializado para importar preferencias y ahora activa únicamente el corte CPU >60 W. La interfaz lo identifica como consumo de CPU. La coherencia del sensor GPU sigue requiriendo un valor finito entre 0 y 300 W; no se admiten lecturas ausentes o inválidas. Las protecciones térmicas, el watchdog y los controles de adquisición mantienen su contrato.

## Causas de admisión

El inicio y la entrada manual comparten una explicación de las comprobaciones de telemetría. Se distinguen temperatura, potencia y RPM CPU/GPU, núcleos incompletos o inválidos, campos conservados, fechas futuras y antigüedad. En WMI se suma el tiempo transcurrido desde la captura de la edad del ventilador; entrar requiere RPM <3 s, aunque el margen de una sesión establecida sea 10 s. Los sensores críticos del producto mantienen <5 s.

`PRODUCT AUTOMATIC ADMISSION REJECTED` incluye códigos y detalles, la muestra evaluada, la evaluación SafetyGate y su instante. `PRODUCT AUTOMATIC WAIT` incluye fase (inicio, reintento o reanudación), estado, fuente, equipo y muestra. Se registra al cambiar el conjunto de causas y, mientras persiste, cada 10 s. La serialización admite valores no finitos para conservar el disparo; un fallo de disco o serialización no modifica la decisión de control.

## Verificación

La regresión ejecuta GPU 75/81.251/83.016/115/300 W en inicio y en los tres modos del guardián; comprueba que CPU 61 W y GPU 83 °C siguen bloqueadas con sus protecciones activas. También prueba potencia GPU ausente/no finita/fuera del rango, importación de preferencias, recuperación tras lectura ausente, tres adquisiciones distintas, límite de RPM de entrada, límite de sensores críticos y causas concretas. Los fixtures de sesión comprueban el registro JSON de la muestra inválida y su instante exacto.

Se vuelve a ejecutar la traza original de 390 decisiones/593.2 s para comprobar que la política de demanda y retención no cambia. La vinculación de fuentes se actualiza después de comparar el CSV y las métricas con el estudio archivado. La admisión al hardware se valida mediante regresiones sintéticas, sin afirmar una nueva cualificación física.

El pipeline Windows ejecuta las suites existentes, renders GUI, compilación y verificación del instalador. La publicación preliminar exige las verificaciones OEM, WMI y CPU/GPU del mismo commit y conserva las releases anteriores.
