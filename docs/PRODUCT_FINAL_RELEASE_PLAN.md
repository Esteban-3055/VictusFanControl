# Cierre de la versión final

Destino: HP 8C40 / 9D0R1LA / BIOS F.18. El paquete actual sigue siendo de revisión;
La candidata incluye `PRODUCT_FINAL_CANDIDATE.md`, evidencia consolidada y
`FinalCheck` sobre los ejecutables distribuidos. La etiqueta estable requiere cerrar las rutas pendientes, sin repetir
las pruebas de subsistemas ya aprobadas en PRODUCT_VALIDATION_STATUS.md.

## 1. Consolidación de la GUI

Entrada lateral «Avanzado» y título coherente. El gráfico de Ventiladores reserva
filas distintas para números del eje X, «Demanda (%)», plazo de revisión y carga
acumulada. Los renders de Windows incluyen descenso breve y prolongado a tamaño
nativo y mínimo. No se cambia el motor por estas correcciones visuales.
El bloqueo dispone de «Ver motivo completo», con causa original, muestra del
disparo, estado actual y salida limpia. Reglas y seguridad muestra el rango
vigente 10–50 y el contrato térmico de esta candidata.

## 2. Actualización de límites durante el uso

Implementada la acción «Aplicar cambios» para distinguir borrador y límites
aplicados, con «Desactivar límites CPU / GPU» separada. El Guardian permite
actualizar presets en la misma sesión; la regresión física breve en AC está aprobada
en PRODUCT_PERFORMANCE_UPDATES.md (CPU 25/40 → 25/38 W; GPU 1800 → 1750 MHz).

La nueva ruta debe conservar el baseline original y la identidad de los journals,
serializar solicitudes y comprobar lectura CPU / aceptación GPU. Debe publicar
estado por dominio si falla uno, en vez de anunciar éxito conjunto. Cambiar AC o
Batería solo edita esa fuente; la aplicación debe usar la alimentación real.
En Automático se requiere confirmación vigente de ambos dominios para seguir;
una actualización no renueva el plazo ni adquiere ventiladores por su cuenta.
Fallos parciales, fuente y display/suspensión durante la actualización tienen
fixtures sin hardware. La regresión de ciclo de vida integrada sigue en el punto 3;
los dos Apply observados en AC no la sustituyen.

## 3. Regresión de ciclo de vida de la GUI actual

Con actividad ligera: suspender/reanudar, exportar y salir normalmente desde
bandeja. Confirmar Firmware sin reentrada espontánea, release CPU/GPU y siguiente
apertura en Firmware. Conservar evidencia de cualquier fallo de release.
Las pruebas históricas del Guardian siguen aprobadas; esta revisión cubre su
integración con la GUI y Automático actuales.

## 4. Uso continuo y distribución

El contrato de interrupción y reentrada está definido: se mantiene Firmware
tras un fallo y se requiere salida limpia/nueva apertura, sin rearmado incidental.
Se elimina el plazo adicional de CPU >90 °C de la GUI ya establecida. Desde 95 °C
se conservan los 2000 ms y cinco muestras de la admisión del controlador; desde
99 °C el retorno es inmediato. Inicio ≤90 °C, fuente, potencia, GPU y telemetría
siguen protegidos. Hace falta una regresión breve de este cambio y del punto 3,
sin repetir la carga prolongada anterior, antes de promover Automático normal.
El arranque con Windows
no debe activar hardware como efecto incidental de una preferencia.
Mantener las protecciones crudas y la liberación; verificar el paquete exacto,
sus ejecutables y manifiesto antes de etiquetar y publicar una versión final.
La aceptación NVIDIA y WMI seguirá identificada como tal cuando no exista lectura
independiente del rango o de propiedad.
