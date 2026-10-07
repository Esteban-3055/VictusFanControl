# Ventilación estable y silenciosa — 8C40 / F.18

Propuesta para el Victus 15-fa1xxx, i7-13700H y RTX 4060 Laptop nominal 70 W.
El control mantiene una curva Demanda % → Nivel por fuente, seis influencias
independientes y el máximo de sus contribuciones. No cambia límites CPU/GPU.

## Evidencia y diagnóstico

Se revisaron 5020 muestras recientes de siete procesos, 7244 timestamps únicos
históricos en CSV y 221 muestras de dos capturas HP automático. Los exports que
se solapan se unen por sesión/timestamp y los comandos por ID de Automático;
no se cuentan como nuevas pruebas. Las lecturas históricas incluyen manual,
otros motores y actividad de carga: orientan el rango, no prueban estos presets.

El replay con el motor C# de producción reproduce 3349/3349 niveles históricos
usando curvas, influencias, ajustes en vivo y adquisiciones intermedias originales.
No filtra la temperatura cruda. Los hashes de código comparan UTF-8 con saltos LF
para conservar el vínculo de contenido también en checkouts Windows CRLF; los
hashes de archivos de evidencia siguen vinculando los bytes originales. El informe está en
`release/product-fan-stability-replay-8c40-2026-10-06.json`.

| Intervalo | Cambios de nivel observados | Inversiones observadas | Cambios propuestos en replay | Inversiones propuestas |
|---|---:|---:|---:|---:|
| Prueba prolongada, 35 min 37 s | 123 | 65 | 81 | 17 |
| Última activación, 22 min 18 s | 145 | 37 | 83 | 20 |

Una inversión es un cambio de sentido entre dos cambios no nulos; no equivale a
un ciclo completo. Se mide consigna, no oscilación de RPM ni ruido. Las referencias
HP muestran CPU 2500–2600 / 2500–2700 RPM y GPU 2300–2400 RPM. La primera incluye
95 °C y 87.208 W CPU: no es reposo limpio ni una comparación con límites iguales.
Los CSV no identifican AC/Batería; no se atribuye estabilidad independiente a BATT.

Causas que explican los registros:

- La bajada breve anterior podía empezar tras 4 s y repetirse un nivel por ventana,
  mientras los picos crudos volvían a forzar subidas de hasta 4 niveles.
- No había banda de histéresis adicional: la demanda cerca de un límite podía
  alternar una bajada y una subida normal.
- La última sesión comenzó con influencias térmicas 120 %, después 135 %; GPU
  potencia llegó a 100 % y carga a 81 %. Son ganancias, no pesos de un promedio:
  135 % CPU satura su contribución a aproximadamente 77 °C de temperatura efectiva.
- El criterio de carga se modificó de 1200 a 120 s durante esa sesión; no se deben
  comparar sus descensos como si todos usaran el mismo preset.

## Presets propuestos

| Demanda | AC | Batería |
|---:|---:|---:|
| 0 % | 12 | 10 |
| 40 % | 12 | 10 |
| 50 % | 20 | 14 |
| 65 % | 30 | 26 |
| 76 % | 36 | 35 |
| 90 % | 44 | 44 |
| 100 % | 50 | 50 |

Interpolación lineal entre puntos. Influencias AC: temperatura CPU/GPU 100/100,
potencia CPU/GPU 40/60, utilización CPU/GPU 20/20 %. Batería: 100/100, 20/35, 10/10 %.
La meseta cubre CPU efectiva 40–60 °C y GPU 35–53.4 °C si ninguna otra contribución
supera 40 %. Es una meseta de DEMANDA, no una garantía de ventilación mínima con
cualquier temperatura CPU. Temperatura efectiva por defecto: media de los tres
P-Cores más calientes; protección: paquete o núcleo más caliente sin suavizar.

Se conservan las referencias absolutas 60 W CPU / 75 W GPU. 75 W permite representar
lecturas algo superiores a los 70 W nominales; cambiar PL1/PL2 o MHz no remapea
los watts medidos. El calor crudo mantiene pisos CPU ≥85→44, ≥90→50; GPU ≥78→44,
≥81→50, incluso con una curva editada baja.

## Inercia común

- Subida normal: EMA 8 s, confirmación 3 s, un nivel por paso.
- Bajada breve: EMA 15 s, confirmación 6 s, un nivel por paso.
- Bajada prolongada: EMA 35 s, confirmación 18 s, un nivel por paso; criterio de
  carga acumulada 1200 s y reposo de recuperación 120 s, sin cambios.
- Histéresis de bajada: 1 nivel adicional. Se mantiene el nivel si la demanda
  oscila cerca del límite; al alcanzar el mínimo real de la curva se libera la
  banda para evitar que el ventilador quede permanentemente por encima del mínimo.
- Espera tras calor crudo: 30 s desde la última adquisición caliente, también
  durante ACK. Una nueva lectura caliente renueva esa espera; después comienza
  la confirmación normal de bajada. No retrasa subidas ni cambia plazos críticos.
- Los picos no se conservan en la EMA normal por defecto; la espera de enfriamiento
  es explícita y separada. Aplicar curva/ajustes conserva el nivel, la EMA y la
  última lectura térmica, y reinicia únicamente confirmaciones.

Una alternativa más lenta (EMA prolongada 45 s, confirmación breve/prolongada
10/25 s) reducía inversiones a 11/16, pero terminaba la traza prolongada en nivel
16 frente al 13 observado. La elección equilibrada acaba en 14 y evita prolongar
innecesariamente el ruido al terminar la carga. Ninguna cifra predice temperatura
resultante: se mantiene fija la telemetría de origen, sin modelo térmico del equipo.

## Avanzado y uso

Avanzado incorpora Estabilidad. Filtro de subida: 0.5–60 s; confirmación: 0–30 s.
Filtros de bajada: 1–300 s; confirmaciones: 2–180 s. Carga acumulada: 60–7200 s,
pausa tolerada 0–300 s, reposo 10–1800 s. Histéresis 0–5 niveles; espera térmica
0–300 s. Umbrales de respuesta adelantada CPU 60–85 °C / GPU 50–78 °C.
El polling 500–1500 ms, la subida térmica 4, la bajada protegida 1 y las emergencias
se conservan. El porcentaje físico de utilización sigue limitado a 100 %.

Los archivos antiguos mantienen sus preferencias: los campos nuevos ausentes
valen cero. No se reemplazan curvas personalizadas al abrir. Para probar todo el
preset: desde Firmware, ir a Avanzado → Estabilidad → **Preparar presets estables
AC/Batería**, guardar y activar Automático. El botón solo prepara el borrador y
preserva PL1, PL2, MHz y habilitación CPU/GPU. Si se usa durante Automático, aplicar
la curva activa en Curvas y los ajustes en Avanzado por separado; preparar/guardar
no aplica hardware. La otra fuente se usa al cambiar la alimentación.

Primera comparación: reposo, la misma carga habitual, terminar la carga y observar
el retorno a reposo. Mantener los parámetros fijos durante esta comparación y
exportar el diagnóstico de esa sesión; después comparar Batería con su uso normal.
No se sustituye el control térmico por una estimación de temperatura de chasis.

Automático normal continúa cerrado. La revisión explícita conserva entrada CPU
≤90 °C, confirmación 95–98.x de 2000 ms, CPU ≥99 inmediata y límites GPU/potencia,
telemetría y lifecycle. Esta es la propuesta para el preset final; requiere una
observación física representativa antes de afirmar superioridad acústica o térmica
sobre Firmware y promover la versión estable.

## Reproducir el estudio sin hardware

```powershell
python tools/FanStabilityReplay/prepare-evidence.py --upload-dir RUTA_EXPORTS --historical-dir RUTA_LOGS --output-dir RUTA_RESULTADOS
dotnet run --project tools/FanStabilityReplay -- RUTA_RESULTADOS/dataset.json RUTA_RESULTADOS/replay.json
```

`--additional-dir` admite otro directorio de exports. Las trazas y el replay detallado
permanecen locales; solo se publican agregados y hashes. El programa no crea un
backend ni ejecuta comandos de ventilación/rendimiento. Compara el preset anterior,
la inercia nueva sola y el preset completo; reconstruye también los ajustes reales.
