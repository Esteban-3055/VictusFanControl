# PL1 hasta el valor de PL2

Desde 1.3.12, los perfiles del producto permiten 8 ≤ PL1 ≤ PL2 ≤ 114 W. Deslizadores, botones, entrada numérica, teclado y accesibilidad usan el mismo rango. Subir PL2 conserva PL1; bajar PL2 reduce PL1 solo cuando lo supera. La edición no escribe hardware hasta Aplicar o activar explícitamente la sesión con esos ajustes.

El Guardian del producto selecciona una política de ejecución explícita, tras verificar destino, procesador, módulo y referencia 45/115 W desbloqueada. El codificador y el limitador vuelven a validar el rango. Los límites mínimo/máximo que informe el hardware, la propiedad de campos y los bits Enable/Clamp/Tau/Lock se conservan. Las protecciones térmicas existentes no cambian.

Las instancias históricas del backend y limitador mantienen por defecto la política de reducción respecto a su referencia. La recuperación no necesita una autorización de aumento: restaura únicamente los campos aún propios desde el registro duradero y no reanuda el ajuste solicitado. No cambia el esquema del registro.

La prueba de software usa el codificador y limitador reales con registros JSON y un backend de memoria: verifica 80/80, cambio a 114/114, restauración de la referencia, metadatos, recuperación, rechazo de otro destino, bloqueo y límites informados. El editor comprueba ambos perfiles, valores exactos y extremos 8/8 y 114/114. Los ensayos físicos históricos no califican estos nuevos valores sostenidos; esa observación sigue pendiente.
