# Primera ejecución física completa: GPU / HP 8C40

## Conclusión

La ejecución completó el protocolo ABBA de 43 minutos y liberó los controles
sin fallos registrados. La extensión TZ01/DTT3 funcionó en hardware durante los
dos bloques B, sin pérdida de fuentes ni discontinuidad de telemetría. No hay
una ventaja térmica demostrada frente a la curva actual: las medias GPU durante
la carga estable difieren menos de 0,5 °C y las duraciones de carga no fueron iguales.
La curva actual ya retiene niveles altos durante el enfriamiento de esta carga.
Se mantiene el suplemento experimental y opt-in; no se promueve Automatic normal.

## Procedencia e integridad

- Archivo original: `Victus-Platform-physical-20261008T214821Z-1b1a9170.zip`.
- SHA-256 ZIP: `0a7185d7242b99a8123bfb6680261a8d7c83004578291e46ac73186e397f16ba`.
- SHA-256 JSONL: `8b67d31e4374ee3a1606a090f719cf8f1bfa89c57b6675fc5e82c3d2453415cf`.
- Build: `2de48a17d780b70693b017a2af9b9de94005aefc`.
- Destino: HP-8C40-9D0R1LA-F18; i7-13700H; RTX 4060 Laptop.
- Límites elegidos: CPU PL1 30 W / PL2 40 W; GPU máxima solicitada 1900 MHz.
  No fueron los valores iniciales 25/30 W ni 1950 MHz sugeridos antes.
- Primer snapshot: 2026-10-08 21:48:38.1519377 UTC.
- Último snapshot: 2026-10-08 22:31:36.0703145 UTC.
- 1946 snapshots completos de 14 núcleos; 1945 filas con auxiliares cualificados.
  La primera adquisición no está cualificada por diseño.
- 1531 decisiones y 1531 dispatch results; 84 cambios de request aceptados.
- Ningún gap >3 s; mayor intervalo entre snapshots 2,033 s.
- Ningún error TZ01/DTT en los registros de salud.
- En el diagnóstico final: Firmware/Firmware; CPU Disabled; GPU Disabled;
  AppliedPerformance null; Failure null. Cleanup succeeded; proceso terminó 0.
  Aceptación de release no prueba ownership físico independiente ni rango exacto NVML.
- Máximas CPU de control 91 °C y GPU 68 °C. No es validación de carga CPU sostenida.
- TZ01 máxima 66,05 °C; DTT3 máxima 57 °C. Sus ubicaciones físicas no se infieren.

Se verificaron CRC, tamaños y SHA-256 de todos los archivos del manifest de evidencia,
hashes de perfiles iniciales/efectivos, límites elegidos y preservación de curva/Batería.
Los nombres Windows del ZIP se normalizaron al extraer en Linux; no se alteró el archivo
original ni el contenido de sus registros.

## Cobertura de carga

Las ventanas programadas son reposo 120 s, carga 240 s y enfriamiento 180 s.
La tabla identifica la carga GPU real con GPU load >=20 %. Los límites de cada
intervalo dependen del muestreo (~1–2 s), no de una señal del proceso de benchmark.
El ZIP no identifica de forma independiente el programa ni su intensidad configurada.

| Bloque | Política física | Inicio carga dentro del bloque | Fin carga dentro del bloque | Duración aproximada |
|---|---|---:|---:|---:|
| A1 | Actual | 182 s | 422 s | 239 s |
| B1 | Retención | 177 s | 385 s | 208 s |
| B2 | Retención | 125 s | 420 s | 295 s |
| A2 | Actual | 128 s | 363 s | 235 s |

Así, en A1 y B2 quedó carga GPU durante ~60 s del enfriamiento programado;
en B1 durante ~25 s; en A2 durante ~3 s. Comparar las medias de esos tres minutos
sin considerar la carga restante confundiría potencia residual con capacidad de enfriar.
No se atribuye esa diferencia a una acción concreta del operador: hay desfase entre
iniciar un programa y generar carga efectiva.

## Comparación descriptiva con carga estable

Selección: GPU load >=20 % y tiempo >=30 s desde la primera muestra cargada de cada
bloque. Medias por muestra; no son resultados emparejados por energía o estado inicial.

| Bloque | GPU potencia media | GPU carga media | GPU temperatura media | CPU potencia media | CPU temperatura de control media | Nivel GPU HP-WMI medio |
|---|---:|---:|---:|---:|---:|---:|
| A1 | 38,33 W | 50,60 % | 61,24 °C | 13,01 W | 57,84 °C | 30,28 |
| B1 | 38,20 W | 50,50 % | 60,75 °C | 13,12 W | 57,65 °C | 29,90 |
| B2 | 38,29 W | 50,52 % | 61,17 °C | 13,06 W | 57,76 °C | 31,02 |
| A2 | 38,25 W | 50,38 % | 60,97 °C | 12,84 W | 57,76 °C | 30,50 |

Las potencias estables son similares, pero los tiempos, los estados iniciales y las
historias de filtro difieren. No hay base para atribuir diferencias subgrado al
suplemento, ni para afirmar equivalencia formal o una mejora acústica. HP-WMI da
niveles con conversión nominal nivel*100 RPM; no se trata de tacómetro exacto.

## Qué aportaron los sensores

En los dos bloques B, el suplemento elevó la demanda raw sobre la base en 407
adquisiciones (235 en B1 y 172 en B2). En el enfriamiento programado la elevó en
196/256 decisiones (76,6 %). Solo 6/256 decisiones de esa fase (2,3 %) pidieron un
nivel mayor que la referencia paralela: delta medio +0,023 niveles. La inercia
existente mantuvo ventilación alta, por lo que elevar una demanda todavía inferior
al nivel mantenido casi no produjo una diferencia de actuación.

La referencia paralela no es un resultado térmico contrafactual: ambas políticas
leen las temperaturas producidas por el controlador físico. La historia física
se comparte entre los bloques y el objetivo se alinea al cambiar de bloque. Incluso
en A2 hay 124 decisiones diferentes de la referencia paralela, sin suplemento activo;
por eso contar diferencias de targets contra esa referencia no prueba causalidad.

Desde el cese real de carga, las temperaturas GPU medias en los primeros 60 s fueron
47,95 / 47,84 / 48,23 / 48,44 °C (A1/B1/B2/A2). Son muy próximas; los niveles iniciales
y el tiempo anterior de carga también varían. No se interpreta como ventaja de B.

## Fallo de cierre del registro y corrección local

La versión 2de48a1 escribió `closed: Cierre de ventana` 19 s después del registro
`completed`, al cerrar el operador la ventana ya terminada. Por ello el checker
original rechazó que completed no fuese literalmente la última línea. No hubo
telemetría, decisiones o dispatch posteriores a completed, y su contenido coincidió
exactamente con summary.json. La evidencia original y sus SHA permanecen intactos.

El checker ahora admite solo ese único evento legacy de cierre posterior al cleanup;
rechaza otros eventos, múltiples terminales y timestamps incompatibles. La generación
nueva sella la sesión tras Complete, impidiendo append por callbacks tardíos y cierres
posteriores. La corrección no toca sensores, curva, límites ni las protecciones térmicas.

Validación local: 8661 checks puros del experimento; auditoría de 2580 snapshots
sintéticos y 2160 decisiones/dispatch; auditoría completa del ZIP real; nueve
controles de corrupción rechazados en ambos. Esta validación es local y sin hardware;
no declara un nuevo paquete Windows publicado ni una promoción física general.

## Próxima decisión

Conservar la curva actual como base y no aumentar la retención solo para forzar una
diferencia en esta carga. La candidata es operativa, pero el beneficio todavía no
está demostrado. El siguiente dominio útil es carga CPU moderada y reproducible,
con tiempos efectivos iguales en A/B. Una repetición GPU sería necesaria si se
quiere cuantificar causalmente diferencias pequeñas con cargas y estados emparejados.
La ejecución presente ya sirve para confirmar funcionamiento de los sensores,
integración física del suplemento, continuidad y cierre bajo esta carga GPU.
