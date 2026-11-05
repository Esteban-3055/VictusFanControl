# Automático sin corte por consumo y GPU hasta 3105 MHz

Desde la preliminar 1.3.4 el máximo configurable es 3105 MHz. El tope anterior era 2535 MHz; los valores guardados no se aumentan al actualizar. [Detalles y compatibilidad](GPU-CLOCK-3105.md).

El uso habitual deja de rechazar la entrada o cancelar Automático porque el paquete
CPU supera 60 W. El consumo sigue siendo entrada de las curvas y se conserva la
validación de sensores: lecturas ausentes, no finitas, negativas o incoherentes se
rechazan. Los límites de coherencia (CPU 0–500 W, GPU 0–300 W) no son objetivos de
potencia ni autorización para cambiar PL1/PL2 a esos valores.

Se retira el interruptor «Volver a Firmware por consumo de CPU». El campo JSON
`powerEnvelopeHandoff` se acepta por compatibilidad y se limpia a false al importar,
copiar o guardar. Aunque un objeto antiguo lo conserve a true, el control habitual
no lo usa. El archivo original no se sobrescribe al leerlo. Los ajustes PL1/PL2,
curvas, temperaturas y límites guardados no cambian con esta migración.

Las revisiones físicas explícitas de 5/45 minutos conservan su margen histórico;
no pertenecen al modo habitual. Continúan las protecciones térmicas configuradas,
las comprobaciones de equipo, frescura, propiedad y exclusión WMI.

GPU admite un máximo configurable de **3105 MHz** en AC y batería (mínimo 210).
Deslizador, entrada numérica, importación/guardado y configuración del controlador
comparten ese máximo. Se conservan valores existentes y predeterminados 1850/1200.
3105 es un límite solicitado; no se fuerza una frecuencia constante ni se demuestra
que el driver la acepte o que la GPU la alcance. Un rechazo de NVML sigue siendo un
fallo con el mismo procedimiento de liberación; no se afirma propiedad independiente.

Pruebas: admisión habitual a 60, 60.001, 61, 75, 115 y 500 W con el campo legado a
true/false; entrada, ejecución y despacho; migración sin alterar ajustes; rechazo de
potencia nula/no finita/negativa/fuera de coherencia; revisiones físicas y protección
térmica conservadas. GPU 3105 y rechazo de 3106, conservación de 2535/2536, inferior 209, AC/batería, presets NVML, archivos y
edición gráfica/numérica. Fixtures sin acceso físico, sin instalación ni aplicación
de frecuencias al equipo.
