# 1.3.2: prioridad, publicación y recuperación

## Cambios incluidos

La versión reúne las correcciones posteriores a 1.3.1:

- [Confirmación tardía de liberación WMI](UPDATE-BLOCK-1.3.0.md): salida real del propietario original, informe con identidad exacta, ausencia de lease y marcadores inciertos. Nunca repite una liberación para resolver una espera anterior.
- [Prioridad Alta y publicación sin recorrido síncrono](TELEMETRY-SCHEDULING-RECOVERY.md): configuración por proceso, inventario de presentación con lector único, reloj al completar, caducidad conservadora y drenaje antes del traspaso. El inicio espera una comprobación en curso; una sesión realmente pendiente mantiene su bloqueo. La admisión nativa conserva una lectura directa de los registros.
- [Reconexión ESP32 y diagnóstico Windows](ESP32-WIFI-RECOVERY.md): backoff independiente de AutoReconnect, reintento mDNS, nueva autenticación al reconectar y registro sin SSID, contraseñas, claves o pruebas HMAC.

La demora inicial observada durante una instalación/descompresión estaba en la adquisición IntelCoreTemperatures; una captura posterior confirmó el bloqueo adicional de publicación por archivos. Se corrige este último sin atribuirle retrospectivamente toda la incidencia ni ampliar los plazos de frescura.

## Validación de software y distribución

El publicador exige sobre el commit exacto de 1.3.2 seis workflows exitosos: build/empaquetado, 19 grupos Windows, OEM, WMI, CPU/GPU y firmware. El paquete comprueba versiones de DLL, contrato de autorización del equipo, hashes, ZIP original y procedencia del instalador antes de preparar la publicación. Se conservan los controles de 1.3.1.

Las nuevas pruebas de planificación verifican prioridad Alta efectiva y restauración del proceso de prueba; 1000 consultas sin esperar una lectura bloqueada; un solo lector; resultado inicial/vencido/ilegible; identidades exactas; drenaje y descarte tardío; admisión directa frente a una caché vacía; continuidad del inicio con tres lecturas nuevas; y renovación periódica que conserva la ventana de recuperación. Las regresiones incluyen cambios AC/batería, cierre, actualizaciones y traspaso de propietarios.

El firmware se compila con core Arduino **3.3.2** y ArduinoJson **6.21.5**, y con PlatformIO en las variantes de monitor y PWM invertido. Los tests host incluyen watchdog Wi-Fi, protocolo autenticado, curvas, LCD y exportador Arduino. El ZIP de Arduino se compara con las fuentes canónicas, con PWM activado y sin configuración privada. GPIO3 invertido 18 Hz/14 bits, GPIO0 NTC, GPIO6/7 I2C, Wi-Fi 8,5 dBm, HMAC TCP 8765 y NVS mantienen su contrato.

El actualizador original 1.2.9 se prueba con versiones instaladas desde 1.2.9 hasta 1.3.1, con y sin selección de preliminares: 1.3.2 estable debe ser elegible en ambas situaciones. Se rechazan borradores, sufijos, versiones ya instaladas y preliminares sin consentimiento. Tras publicar se verifica descubrimiento, descarga completa, SHA-256, caché y confirmación pública del mismo instalador sin ejecutarlo.

La publicación no sobrescribe tags o assets anteriores. Se prepara un borrador, comprueba todos los tamaños y hashes subidos y solo entonces publica como estable. Los informes de los workflows y los archivos de procedencia de la release contienen los resultados efectivos; este documento describe los criterios de admisión, no resultados físicos nuevos.

## Instalación y límites

La actualización del programa conserva perfiles y preferencias. No instala automáticamente firmware. Para incorporar la reconexión Wi-Fi de la placa es necesario cargar el nuevo sketch sin borrar NVS; las correcciones Windows no requieren esa carga.

Se mantienen pendientes las observaciones físicas de inicio Windows, uso prolongado bajo carga, recuperación de drivers, LCD/NTC/PWM y reconexión tras reiniciar el router. No se reinicia el equipo, no se detienen mutadores a la fuerza y no se eliminan journals para completar las pruebas o una instalación. Alta no garantiza plazos ni resuelve cualquier llamada nativa que nunca retorna.
