# v1.1.8: picos CPU y demanda de ventiladores

La ruta habitual usaba la temperatura CPU cruda —el mayor valor entre package y núcleo más caliente— para activar el suelo térmico y omitir la inercia normal: desde 85 °C elevaba la contribución CPU al 90 %; desde 90 °C, al 100 %. Cada nueva lectura caliente renovaba además la espera térmica de descenso. Seleccionar la media de P-Cores no eliminaba ese mecanismo.

Automático habitual del destino exacto incorpora ahora un filtro anterior al cálculo de demanda. Usa el mínimo de las lecturas de los últimos dos segundos, conservando la adquisición inmediatamente anterior al inicio de la ventana para evitar una confirmación anticipada cuando el sondeo es irregular. Filtra por separado la temperatura de demanda seleccionada y el máximo crudo usado por el suelo de demanda. Una caída pasa inmediatamente al filtro; la inercia posterior sigue decidiendo cuánto baja el nivel.

Una subida breve puede aparecer en la telemetría, pero no alcanza por sí sola el suelo CPU de demanda, la respuesta térmica rápida ni la renovación de la espera de descenso. Si persiste durante dos segundos, se incorpora. Temperaturas sucesivas distintas pueden producir una incorporación gradual: se confirma el mínimo sostenido, no el máximo aislado de la ventana.

El primer dato caliente de una sesión no dispone de una referencia fría y se acepta sin inventarla. Un máximo crudo CPU ≥99 °C omite la ventana inmediatamente. Los datos inválidos, duplicados, fuera de orden o discontinuos no se convierten en adquisiciones válidas. Las adquisiciones frescas durante un ACK también alimentan la ventana. Una lectura CPU retenida rompe la confirmación y mantiene la demanda previamente aceptada; al volver datos frescos comienza una nueva ventana. Un máximo extremo en telemetría parcialmente retenida sigue omitiendo el filtro.

## Alcance

- Se activa en Automático habitual y en su simulador. Las rutas de cualificación histórica y el adaptador de investigación conservan su comportamiento predeterminado.
- La telemetría real, el máximo de package/núcleo, SafetyGate, la admisión térmica, las devoluciones a Firmware y la protección del propio equipo siguen usando las temperaturas crudas. Sus preferencias y umbrales no se modifican.
- La temperatura GPU y las contribuciones de potencia y carga CPU/GPU no se filtran con esta ventana. Una subida de demanda por esos factores puede seguir ocurriendo.
- Se conservan la EMA posterior, los pasos de subida/bajada, el suelo térmico cuando está confirmado y los tiempos de descenso. El filtro reduce el efecto de picos breves; no elimina toda respuesta a calor real.
- No cambia el formato de los perfiles ni requiere migrar curvas personalizadas.

Las decisiones exponen `CpuThermalDemand`: fecha, temperatura seleccionada y máximo crudo originales, temperaturas filtradas, supresión y bypass extremo. El marcador aplicado usa la observación de demanda aceptada por el controlador. La vista previa de una curva con la misma fuente CPU usa el filtro de la misma adquisición; un borrador con otra fuente o un dato aún no procesado se calcula a partir de sus sensores. La gráfica señala la supresión cuando existe una solicitud aplicada vigente.

## Verificación

Las pruebas deterministas cubren un pico aislado, picos alternos que no se acumulan, calor sostenido, sondeo irregular, caída inmediata, inicio caliente, 99–100 °C, épocas repetidas, NaN, huecos de telemetría, lecturas retenidas, recuperación fresca, adquisiciones durante ACK, no renovación de la espera de descenso y conservación de GPU/potencia. Comprueban también la correspondencia entre demanda aceptada y marcadores, y el aislamiento de otra fuente o fecha del borrador.

El adaptador de investigación se regenera desde la fuente revisada y fijada por SHA-256; el filtro permanece desactivado por defecto en esa ruta. La publicación conserva las verificaciones Windows del mismo commit y la comprobación del payload e instalador antes de crear una release estable nueva.

Estas pruebas no miden refrigeración, ruido ni el comportamiento térmico contrafactual del equipo. La observación física del filtro permanece pendiente en `PRODUCT-RELEASE.json`.
