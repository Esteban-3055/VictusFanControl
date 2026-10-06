# Cierre de la versión final

Destino: HP 8C40 / 9D0R1LA / BIOS F.18. El paquete actual sigue siendo de revisión;
la etiqueta de versión final requiere cerrar las rutas pendientes, sin repetir
las pruebas de subsistemas ya aprobadas en PRODUCT_VALIDATION_STATUS.md.

## 1. Consolidación de la GUI

Entrada lateral «Avanzado» y título coherente. El gráfico de Ventiladores reserva
filas distintas para números del eje X, «Demanda (%)», plazo de revisión y carga
acumulada. Los renders de Windows incluyen descenso breve y prolongado a tamaño
nativo y mínimo. No se cambia el motor por estas correcciones visuales.

## 2. Actualización de límites durante el uso

Implementar una acción «Aplicar cambios» que distinga borrador y límites realmente
aplicados. Mantener una acción separada «Desactivar límites CPU / GPU».
La sesión actual del Guardian todavía no admite reemplazo en vivo.

La nueva ruta debe conservar el baseline original y la identidad de los journals,
serializar solicitudes y comprobar lectura CPU / aceptación GPU. Debe publicar
estado por dominio si falla uno, en vez de anunciar éxito conjunto. Cambiar AC o
Batería solo edita esa fuente; la aplicación debe usar la alimentación real.
En Automático se requiere confirmación vigente de ambos dominios para seguir;
una actualización no renueva el plazo ni adquiere ventiladores por su cuenta.
Fallos, cambios de fuente y suspensión durante la actualización necesitan
fixtures y una regresión física breve propia antes de aprobar esta ruta.

## 3. Regresión de ciclo de vida de la GUI actual

Con actividad ligera: suspender/reanudar, exportar y salir normalmente desde
bandeja. Confirmar Firmware sin reentrada espontánea, release CPU/GPU y siguiente
apertura en Firmware. Conservar evidencia de cualquier fallo de release.
Las pruebas históricas del Guardian siguen aprobadas; esta revisión cubre su
integración con la GUI y Automático actuales.

## 4. Uso continuo y distribución

Después de cerrar los puntos 2–3, definir explícitamente el contrato de Automático
normal, pérdida de telemetría, interrupción y reentrada. El arranque con Windows
no debe activar hardware como efecto incidental de una preferencia.
Mantener las protecciones crudas y la liberación; verificar el paquete exacto,
sus ejecutables y manifiesto antes de etiquetar y publicar una versión final.
La aceptación NVIDIA y WMI seguirá identificada como tal cuando no exista lectura
independiente del rango o de propiedad.
