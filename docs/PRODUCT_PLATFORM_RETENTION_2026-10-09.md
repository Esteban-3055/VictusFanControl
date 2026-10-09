# Retención opcional TZ01/DTT3 y candidata AC

La meta es temperatura comparable con menos ruido. La GUI implementa retención
**experimental, desactivada por defecto**, en Avanzado → TZ01 / DTT3. Guardar
persiste la preferencia; nunca la calificación, el plazo ni la autoridad. Los
perfiles anteriores siguen con ella desactivada. Se aplica en la próxima
activación explícita desde Firmware, solo con AC y en revisión autorizada del
8C40/F.18. El gate normal de Automatic permanece cerrado.

La candidata silenciosa se prepara por separado: conserva la curva existente,
influencias, fuente de temperatura, protecciones, límites CPU/GPU y Batería.
Reduce hasta dos niveles en puntos estrictamente entre 40 y 90 % de demanda,
respetando el orden creciente y los extremos calientes. Acorta el descenso solo
si supera 10/4 s para carga breve y 25/12 s para prolongada. Conserva subida,
histéresis y espera térmica. Repetir el botón con la misma curva preparada no
acumula reducciones.

| Elemento | Contrato |
|---|---|
| Fuentes | TZ01 ACPI RTMP y DTT3; sin ubicación física inventada |
| Calificación | Dos adquisiciones distintas, separadas al menos 1 s |
| Frescura | Edad estrictamente menor de 3 s, también al despachar |
| Historia | Hasta ocho adquisiciones por fuente; sin valores futuros ni puentes fabricados |
| Piso retenido | `min(auxiliar, último nivel aceptado, 44, demanda base + 2)` |
| Plazo | Aporte crudo hasta 60 s; después continúa la inercia normal |
| Rearme del episodio | La base debe alcanzar de nuevo el techo inicial; una fuente tibia no renueva el plazo |
| Subida | La memoria del filtro no convierte la retención en una nueva subida; base y protección térmica conservan su subida |
| Filtro | Una sola inercia de producción, sin reiniciar el filtro al sumar el piso |
| Fallo en control | Fuente inválida/caduca, discontinuidad o pérdida de AC interrumpen y solicitan Firmware; sin reentrada espontánea |

Antes de aplicar CPU/GPU se espera hasta ocho segundos por la calificación.
Las lecturas mantienen una llamada nativa pendiente por grupo; un timeout no
libera esa admisión. Los diagnósticos incluyen historias congeladas por muestra
y la demanda/plazo de retención. Fecha de consulta no demuestra actualización
del sensor físico. Desactivada no se evalúa la extensión ni se añaden requisitos
auxiliares. CPU/GPU crudos, emergencias, watchdog y lifecycle siguen vigentes.

## Estudio reproducible

Fuente: `Victus-Platform-physical-20261009T001126Z-5af2aa80.zip`, SHA-256
`09a665dc4938730884752a239ea6fef72507694f7b1403c5a1fe9105e59ff283`.
Trace SHA-256 `bfd5599759844440820fff747c3c19bf7942379b9731b3bedb1756fd62a22f2a`.
Se reconciliaron 390 decisiones durante 593,238 s. La base numérica coincide
con la captura original; solo se excluye el texto localizado coma/punto.

| Variante simulada | Nivel medio por tiempo | Cambios | Decisiones inferiores a la base |
|---|---:|---:|---:|
| Curva registrada | 21,58 | 18 | — |
| Candidata AC | 20,80 | 16 | 220/390 |
| Candidata con retención | 21,01 | 16 | 204/390 |

Retención elevó el objetivo respecto a la candidata sola en 51/390 decisiones.
Con retención, el promedio de nivel disminuye alrededor del 2,65 %. Es una
señal favorable para una candidata conservadora, **no una reducción de ruido
medida**. Todas las variantes comparten temperaturas/potencias grabadas y
suponen aceptación inmediata. Nivel HP-WMI y RPM nominales no equivalen a dBA.
El replay no predice la temperatura que habría producido la otra curva.

[Resumen](evidence/product-quiet-curve-001126-2026-10-09/summary.json) ·
[Decisiones](evidence/product-quiet-curve-001126-2026-10-09/decisions.csv).

```powershell
dotnet run --project tools/PlatformThermalReplay -- --self-test
dotnet run --project tools/PlatformThermalReplay -- --product-study RUTA\experiment.jsonl SALIDA_NUEVA
```

## Error del Guardian

El diagnóstico `20261008T035709866Z` muestra error 99 por journals CPU/GPU
existentes. El usuario ya recuperó esas sesiones: CPU estaba liberada y no se
escribió; GPU aceptó Reset NVML sin lectura independiente exacta del rango.
No era un fallo térmico. Ahora un preflight de solo lectura detecta registros
pendientes **antes** de iniciar Guardian. Rendimiento → Guardián ofrece
instrucciones con los IDs exactos y las copia al portapapeles.

No ejecuta recuperación, sobrescribe registros ni inventa IDs faltantes. JSON
inválido bloquea la entrada. Se conserva el guard final y mutex del backend y
la explicación específica si el journal aparece durante el arranque. Tras
recuperación externa limpia, la siguiente lectura permite nueva entrada.

El diagnóstico `20261008T060755319Z` muestra bloqueo por pantalla apagada y
reanudación. Mantenerlo hasta reiniciar una sesión limpia es intencional.

## Cierre de versión

Pruebas puras cubren plazo, frescura, pérdida de AC, memoria del filtro,
preferencias anteriores y protección cruda; los fixtures de recuperación
comprueban IDs, archivos parciales/inválidos y conservación exacta de bytes.
La base histórica sigue conciliándose independientemente en 323876 filas.
Windows debe verificar GUI, soak, IPC/Guardian y distribución del mismo commit.

La evidencia de estabilidad de octubre 6 conserva sus resultados, hashes y
fuentes históricas congeladas. La comparación nueva enlaza por separado las
fuentes actuales; no se sustituye el código histórico al añadir la retención.

Queda una comparación física breve con igual carga, límites y condiciones,
seguida de reposo, suspensión/reanudación y salida limpia. No se necesitan
repeticiones largas ni eliminar protecciones para forzar un benchmark. La
retención sigue opcional y la versión sigue candidata hasta cerrar esa evidencia.
