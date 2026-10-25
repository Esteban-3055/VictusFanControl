# Estabilidad, arranque y recuperación: bloques 2–9

Fecha de trabajo: 25 de octubre de 2026. Base publicada: 1.3.2 (`0a51c8e`). El bloque 1 está documentado en [STABILITY-BLOCK1-LIFECYCLE.md](STABILITY-BLOCK1-LIFECYCLE.md). Este informe describe desarrollo y pruebas de software; no certifica comportamiento físico ni modifica la instalación del usuario.

## Cambios de producto

| Bloque | Implementación | Prueba principal |
|---|---|---|
| 0, observación | Historial circular de 256 fases en memoria; reloj monotónico, sesión/generación y fecha original de adquisición separadas | Límite del historial y conservación de fechas |
| 2, publicación | Escritor opcional único, 256 entradas y 4 MiB de reservas; pérdidas contabilizadas. Entrega del último estado y un callback UI pendiente; descarte de secuencias anteriores | Disco/presentación retenidos; 10.000 estados dejan un callback con el más nuevo |
| 2, configuración y ZIP | Curvas aplicadas inmutables, sin clonar/serializar dos perfiles en cada publicación. Exportación única de mejor esfuerzo con entradas separadas del runtime | Mutación del borrador rechazada en lo aplicado; ZIP retenido no impide liberar y abrir Firmware |
| 3, arranque | Preferencias, inventario, opciones visuales y configuración externa se leen fuera de UI. Una operación activa por recurso. Comprobaciones previas solo de lectura pueden terminar tarde sin abrir un controlador después del cierre | Preflight retenido: ventana visible y cierre posible sin soltar la lectura |
| 3, intención | Comprobando, No disponible, Inválido, Pendiente y Ausente se distinguen. Una indisponibilidad temporal pausa la petición de Automático; no concede autoridad ni la borra | Caída transitoria del inventario seguida de datos válidos; registros inválidos conservados |
| 3, relecturas | Ráfaga inicial con esperas 10/30/60 s; después comprobaciones de solo lectura cada cinco minutos. Una lectura fallida antes del drenaje no se registra como reset nativo | Lectura retenida, error, recuperación posterior, cierre y suspensión |
| 4, sensores | Fases de proceso/IPC/biblioteca/exportaciones/init/dispositivo/primera muestra; progreso limitado y ligado a sesión/secuencia. Prioridad Alta explícita del host NVIDIA de producción | Host real detenido antes de pedir GPU confirma High; auxiliares ficticios prueban fases y plazos |
| 4, disponibilidad | En el producto se pueden publicar CPU/RPM válidas aunque otro backend esté ausente. El lector recupera el componente que falta sin reconstruir los sanos por tres muestras incompletas | Admisión completa y fresca conservada; ausencia no sustituida por cero ni por otro sensor |
| 4, limpieza | Los constructores PawnIO/Intel liberan lo adquirido si falla otra fase; la limpieza de backends intenta los recursos independientes aun si otro arroja error | Fallos de inicialización y errores originales/limpieza separados |
| 5, episodios | Presupuesto compartido para recuperación, reemplazo y preparación: tres intentos con esperas 10/30/60 s. La preparación perteneciente a un reemplazo no se cuenta dos veces | Reentradas, errores de lectura, inventario cambiado y reservas simultáneas |
| 5, estabilidad | Reentrada desatendida: 30 s de adquisiciones completas, distintas y vigentes. Después de agotarse: pausa de cinco minutos y un nuevo intento solo con estabilidad y propietario confirmado. Renovación tras diez minutos de Automático real y fresco, no por estar en Firmware | Firmware no renueva presupuesto; reloj/datos duplicados no borran incertidumbre |
| 6, energía | PBT_APMSUSPEND cierra admisión y retorna sin esperar al driver. Drenaje retenido, liberación fallida impide resume, notificaciones duplicadas se combinan y estados anteriores a la frontera se descartan | WndProc de ventana real con liberación ficticia retenida: retorno menor a 200 ms y ninguna liberación inventada |
| 7, Windows | Consulta tipada: tarea ausente, deshabilitada, habilitada, contrato distinto o estado desconocido. XML anterior respaldado y resultado/restauración verificados. Una modificación pendiente conserva su exclusión | Consultas denegadas/no disponibles; cambios XML inválidos rechazados sin registrar tareas reales |
| 7, relanzamiento | XML con hasta tres reintentos, separados un minuto. Salida voluntaria no pide relanzamiento por un error de limpieza. Aplicación/instalador comparten exclusión; la GUI adquiere ese permiso después de mostrarse | Exclusión real de archivos/mutex con identidades ficticias; instalador y app no se solapan |

Las reservas de bytes del escritor son un presupuesto de carga retenida, no una medición exacta del heap de .NET. Cada sobre de telemetría/estructura reserva 64 KiB; los registros JSON demasiado grandes se omiten en lugar de escribir filas truncadas. Los journals, respaldos obligatorios y confirmaciones de liberación **no usan** este escritor opcional.

La fecha de un mensaje de progreso, dibujo o escritura de log nunca se convierte en fecha de adquisición. Una cancelación, un plazo vencido o la salida de un proceso no certifican retorno nativo. Permanecen equipo/BIOS exactos, exclusión de controladores, fechas originales, snapshots completos para entrar y tres adquisiciones nuevas con al menos dos segundos para inicio/manual. La reentrada automática añade el periodo de estabilidad indicado.

El drenaje y la estabilidad son fases distintas. Se puede cerrar pronto una sesión averiada sin esperar temperaturas sanas; la siguiente autoridad sigue esperando pruebas nuevas después de esa frontera. Una operación incierta no se repite por enfriarse el equipo, renovarse una ventana o vencer un reloj.

## Validación por alcance

La integración local pasó **19/19 grupos generales y 5/5 grupos nuevos** sobre `e965e35`, con fuentes limpias. La candidata local `1d2235b` solo añade dos mensajes del instalador: identificación de preliminar y explicación de incertidumbre sin prescribir un reinicio. Los binarios publicados del paquete pasaron además GUI, 30 ciclos/924 renders y ambas recuperaciones ficticias. El instalador pasó 14 casos y la verificación de su payload incrustado mediante entrada DLL. [Resultados, procedencia y hashes](RELEASE-1.3.3-VALIDATION.md). Los registros privados permanecen bajo `output/`, fuera de Git.

No se han reiniciado Windows ni ESP32, registrado tareas de prueba en el equipo ni escrito ventiladores/límites físicos. El ajuste High de un hijo de producción se prueba cerrando su entrada **antes** de enviar initialize: comprueba prioridad, no NVML ni GPU física.

## Bloque 9: prototipo separado de aislamiento

[ControlIsolationPrototype](../tools/ControlIsolationPrototype/) es un ejecutable de pruebas independiente. No referencia el producto, drivers, WMI, PawnIO ni NVML. Su autoridad sobre hardware siempre es falsa. No forma parte del paquete de uso normal.

Prueba procesos distintos para motor y visor, reapertura del visor conservando el motor, un propietario por archivo, canal local CurrentUserOnly, HMAC con desafío nuevo por conexión y rechazo de reenvío. Un estado ficticio pendiente permanece bloqueado después de salir el motor. Solo una señal de retorno **simulada y exacta** permite cambiar ese estado durante la prueba; no es evidencia de un retorno de driver real.

La integración de ese diseño con hardware queda condicionada a un contrato completo, no a cambiar el nombre del proceso:

- Motor, no GUI, como dueño de journals, límites, confirmaciones y exclusión. Nueva identidad de ejecutable admitida de forma explícita por guardianes y recuperadores.
- Un único propietario/serializador HP WMI real para lectura y escritura. Copiar un broker estático a dos procesos no mantiene exclusión.
- Lector CPU aislado compatible con el propietario de límites y una sola instancia hasta confirmar su retorno; timestamps originales y ninguna renovación por IPC.
- Comandos de UI sujetos a identidad, generación, permisos, configuración validada y estado nativo del host. El prototipo actual es solo de lectura y no demuestra esa superficie de comandos.
- Cerrar/reabrir visor no es cerrar control. Salida explícita requiere confirmación de drenaje del motor; caída de motor conserva incertidumbre y evidencia.
- Empezar por usuario/sesión actuales. No copiar claves externas a LocalSystem ni cambiar DPAPI/HMAC como atajo para convertirlo en servicio.

Este bloque aporta una prueba de arquitectura y las condiciones para promoverla. La aplicación de producción aún contiene las lecturas CPU síncronas: un driver que no retorna continúa reteniendo su exclusión. Las mejoras evitan que publicación, disco o el manejador de suspensión añadan bloqueos evitables; no garantizan reparar un kernel averiado.

## Bloque 8: observación y candidata

Antes de una versión estable quedan observaciones del commit candidato: uso normal en AC/batería, retorno a AC, recuperación con sensores reales, cierre y próximo inicio habitual de Windows. No se provoca un reinicio para obtener una prueba de arranque ni se identifica una desconexión física solo porque Windows informe Battery.

Los primeros paquetes deben seguir siendo preliminares mientras falten esas observaciones. Firmware, NVS, credenciales, pines ESP32-C3, PWM invertido 18 Hz/14 bits, HMAC TCP 8765 y curva externa no requieren cambios por estas reparaciones Windows.

Referencias del contrato Windows: [PBT_APMSUSPEND](https://learn.microsoft.com/en-us/windows/win32/power/pbt-apmsuspend) y [RestartOnFailure](https://learn.microsoft.com/en-us/windows/win32/taskschd/taskschedulerschema-restartonfailure-settingstype-element). Son contratos de notificación/relanzamiento; no prueban liberación nativa.
