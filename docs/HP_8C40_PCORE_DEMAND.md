# Temperatura para demanda: P-Cores

Se añaden dos fuentes al selector de Ajustes:

- CPU Average — sólo P-Cores: media de todos los núcleos físicos Performance.
- Promedio de los N P-Cores más calientes: ordenar las lecturas Performance por temperatura descendente y promediar las primeras N. N inicial es 3, configurable; en el i7-13700H puede utilizarse 1–6.

El lector existente usa CPUID 0x1A y topología x2APIC para identificar tipo y núcleo físico; no se cuentan dos veces los hilos SMT. Se exige telemetría completa, índices físicos únicos, temperaturas finitas en rango y tipos Performance/Efficiency conocidos. Sin P-Cores o con N mayor que los disponibles, la selección devuelve valor ausente y no admite una orden normal. No se sustituye por cero, por un subconjunto disponible ni por otra fuente silenciosamente. La frescura sigue comprobándose en las rutas de control existentes.

La función compartida se utiliza en controlador final, vista previa, editor de curvas y experimento supervisado. JSON conserva fuente y N; el diagnóstico del experimento registra ambos y la temperatura calculada. Los archivos antiguos conservan su fuente y reciben N=3 cuando el campo no existe. Guardar ajustes no inicia control. No se añade otro filtro temporal ni se cambia la curva o la autorización de Automatic.

Package y el máximo de **todos** los núcleos, incluidos los E-Cores, permanecen en la admisión de emergencia: 95 °C con confirmación y 99 °C inmediata. Las nuevas fuentes representan agregados para control, no una temperatura físicamente única o más exacta.

## Uso

Seleccionar Ajustes → Promedio de los N P-Cores más calientes → N=3 → Aplicar y guardar, en Firmware. Exportar de nuevo la prueba 30–50 y cerrar la GUI antes de utilizar el supervisor. El perfil nuevo requiere validación física de respuesta, temperatura y ruido.

## Verificación

Pruebas deterministas cubren tipos intercalados, exclusión de E-Cores, N=1/2/3/6, rechazo de N inválido, muestras incompletas/duplicadas/NaN/tipos desconocidos, persistencia y compatibilidad. Las pruebas del controlador comparan N=2 en la vista previa y en la ruta final y comprueban recuperación inmediata con un E-Core a 99 °C. La prueba WinForms cubre el selector, N editable sólo para la fuente correspondiente, guardado en borrador y ausencia de órdenes al aplicar.
