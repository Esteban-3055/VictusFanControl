# Revisión funcional de v1.1.5

Base revisada: `4a9efbe0db80ef22d4ab147ffae328c6c90e7082` (v1.1.4). Se revisaron las diez pantallas del producto y sus rutas de comandos, preparación, aplicación, liberación, persistencia, actualización y recuperación, junto con las dependencias de telemetría, guardianes y control. Es una revisión de software: no demuestra ausencia de todos los bugs ni añade cualificación del equipo físico.

## Fallos confirmados y correcciones

| Hallazgo | Comportamiento corregido | Regresión |
| --- | --- | --- |
| Cada descarga usaba otra carpeta y la GUI perdía el archivo ante un bloqueo | Caché por versión y digest; recibo durable; Descarga e Instalación separados | Un solo transporte para descarga inicial/reintento; reapertura sin red |
| Descargar cerraba el controlador como parte de una sola acción | Descargar sólo escribe el archivo y su recibo; instalar requiere acción explícita | Borrador/navegación/suspensión durante descarga sin instalación ni disposal |
| Un journal previo bloqueaba la actualización sin conservar una selección reutilizable | Recuperación visible desde Actualizaciones; mismo instalador después de recuperar | Pendiente → recuperar → instalar, una descarga y un cierre por runtime |
| Cancelar UAC o fallar al abrir obligaba a usar otra descarga | Reintentar Instalar con el archivo conservado | Dos aperturas, una descarga, un único disposal |
| La casilla del instalador activaba inicio/Automático al actualizar | Desmarcada si ya hay perfiles; preservar por defecto | Formulario real de instalación nueva/actualización y planes de preferencias |
| El código de salida seguía en error después de un reintento exitoso del instalador | Se vuelve a cero al completar | Inspección del flujo de reintento; pruebas del paquete/instalador |
| Un fallo en FenceLifecycle saltaba el drain y perdía la referencia del runtime | Se intenta siempre ShutdownRuntime; no se abre instalador tras ese fallo | Falla de fence y de disposal, sin apertura ni doble disposal |
| El fallo de handoff dejaba estados CPU/GPU activos en pantalla | Se muestran Disabled o Recovering según la liberación; autoridad incierta permanece visible | Ninguna presentación activa tras el handoff fallido |
| Sin runtime, se ignoraban los eventos de pantalla | WndProc observa energía y conserva display-off antes del retorno | Tras cancelar UAC, pantalla apagada bloquea otro intento |
| La actualización de límites podía usar una muestra reciente con temperaturas/potencias conservadas | Admisión común para GUI/runtime exige datos completos, actuales y no conservados | Campo conservado de 4 s y fecha crítica caducada bloquean Apply |
| Marcadores de demanda exigían utilización aunque el producto ya la toleraba ausente | Misma entrada conservadora de 100 % que el motor; sensor sigue ausente | Demanda idéntica al input conservador; contrato histórico permanece estricto |

## Matriz de revisión y validación

| Función | Aspectos revisados | Verificación automatizada |
| --- | --- | --- |
| Inicio y bandeja | Firmware inicial, espera de sensores, cancelación manual, minimizado, salida y limpieza | Product GUI startup/automatic retry/tray; fixture de creación pendiente y cierre |
| Ventiladores | Firmware/Manual/Automatic, rango, demandas iguales, cambios y restauración | SafetyGate, control coordinator, backend y adaptive production tests |
| Telemetría CPU/GPU/RPM | Fechas originales, campos parciales, 5/10 s, identidad, NaN, carga ausente, lifecycle | Continuity, fan reader/window/broker/proof y telemetry coordination |
| Rendimiento | PL1/PL2, GPU, dominios, source real, IPC, actualización y respuesta parcial | Performance GUI; guardian/configuration/cleanup/recovery tests; live Apply |
| Perfiles | AC/Batería independientes, borrador, Guardar/Descartar, importación, migración y copias | Product profiles, persistence, schema/duplicate/null checks |
| Curvas e influencias | Editor, puntos, interpolación, marcadores, Apply y perfil destino | Curve editor, unified demand, source/theme GUI y live curve tests |
| Simulador | Demanda, inercia, carga prolongada, reinicio y límites de historial | Comparación determinista con el motor; GUI del simulador |
| Monitorización | Frescura y ausencia de lecturas, historial y estado aplicado | Dashboard/product GUI renders y liveness/coordination tests |
| Configuración e inicio Windows | Tarea con SID/rutas, batería, Automatic, preservación al instalar | Startup XML/installation plan fixtures y formulario del instalador |
| Avanzado y retención | Límites de edición, apply, episodio acotado, pérdida de auxiliares | Live tuning y 2.324 comprobaciones de retención |
| Protecciones y reanudación | Calor bruto, plazo CPU, fuente, cancelación, límites de reintentos | Thermal confirmation, automatic/resumption/source-transition tests |
| Actualizador | Canales, versión/URL/digest/tamaño, red, caché, retry, duplicados y lifecycle | Update policy/cache/page/handoff fixtures sin red ni hardware |
| Recuperación y reinicio | Identidades exactas, respaldos, registros cambiados, incertidumbre y drain | Guided recovery, installer/shared recovery y restart fixtures |
| Diagnóstico | Exportación atómica, valores inválidos, logs acotados y evidencia original | Product GUI ZIP/persistence fixtures; logs originales conservados |
| Interfaz y recursos | DPI, layouts, teclado, colores e iconos | GUI Windows renders y soak de GDI/USER, 30 aperturas/cierres |
| Distribución | Manifiestos, integridad de todos los archivos, rutas ZIP, instalador y release | Windows Release build, FinalCheck y EXE con payload verificado |

La validación de Windows se ejecuta en CI sobre el commit publicado; el workflow bloquea la publicación si no pasan las comprobaciones de esa misma revisión. También se ejecutaron compilación con warnings como errores y fixtures portátiles durante el desarrollo. Los fixtures de la aplicación no despachan hardware real.

Se reejecutó la traza original de 390 decisiones / 593,2 s. El CSV de decisiones y todos los campos numéricos/configuración del estudio coinciden exactamente con la evidencia archivada. Se actualizó su vinculación de fuentes después de comprobarlo. El replay no ejerce la nueva caché ni acredita por sí mismo la admisión durante lecturas conservadas; esas rutas tienen regresiones separadas.

## Alcance y observaciones pendientes

Las tolerancias habituales siguen siendo RPM <10 s y campos críticos <5 s, con antigüedad original; inicio estricto y estudios históricos mantienen sus contratos. Un ACK de WMI no es prueba independiente de RPM o propiedad del firmware. Un instalador válido se conserva ante fallo de instalación; uno corrupto/incompleto debe descargarse de nuevo. No se reutiliza hardware parcialmente liberado ni se borran journals para forzar una actualización.

Quedan por observar en el HP real: arranque de Windows, actualización desde una sesión fallida del usuario, cancelación/elevación real de UAC, carga de juego/CPU al 100 %, cambios entre monitores/DPI y suspensión. La caché usa un recibo local de la metadata verificada de GitHub y revalida el archivo; no añade firma Authenticode ni una cadena de firma independiente.
