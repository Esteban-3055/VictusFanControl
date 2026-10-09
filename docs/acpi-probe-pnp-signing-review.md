# Revisión de arranque, retirada y firma de la sonda ACPI

Fecha: 2026-10-04. Target: HP 8C40/63.43, SKU 9D0R1LA#AKH, BIOS F.18, EC ACPI\PNP0C09\1. Este documento prepara la revisión y las pruebas; no autoriza instalar el paquete sin firma. Ninguna fila pendiente se considera aprobada por compilación.

## Evidencia disponible y corrección

El commit eaf4528 pasó los dos jobs Windows de la CI 37173569596. InfVerif /w aprobó el INF; Inf2Cat generó el CAT sin errores ni advertencias. El ZIP de ocho archivos tiene SHA-256 bf02b3fe0ed740b5b9b903c0d0392a2451d0eef1c1a93dac57ce35c2b614e35e; se verificaron los siete hashes del manifiesto. El INF coincide con fuente normalizando los finales CRLF de Windows. Nada de ello acredita una instalación o firma.

La consola física del cliente anterior aprobó el parser SMBIOS (3783 bytes), sin cargar driver ni evaluar ACPI. Sigue pendiente el gate kernel, el namespace y toda cualificación PnP.

Problema encontrado por revisión: PrepareHardware propagaba errores de WdfIoTargetCreate/Open. Un canal diagnóstico opcional podía convertir el arranque de la pila EC en un fallo. Corrección: target local hasta completar apertura; ante fallo se elimina el objeto parcial cuando existe, se enclava Faulted y se permite continuar PnP. Prepare posterior no reintenta una sonda enclavada. Release elimina únicamente un target publicado. Los fallos de creación de cola/interfaz posteriores a WdfDeviceCreate también enclavan la sonda y retornan éxito. Caller rechaza el IOCTL privado en identidad rechazada/fallo antes de encolar, para que la ausencia de cola no lo convierta en passthrough. No se efectúa AML en ninguna de esas ramas.

Los fallos anteriores a crear el dispositivo WDF aún pueden impedir adjunción/carga. No se promete que un filtro kernel no pueda afectar al EC. Los efectos reales de estas correcciones requieren ejecución de callbacks y pruebas de fallo en Windows; los fixtures de parsing no cubren esa propiedad.

## Matriz pendiente de runtime

Entorno inicial: Windows de laboratorio con recuperación y depuración, dispositivo ACPI adecuado o simulador capaz de reproducir la pila. Una VM genérica sin EC0 ni su AML solo permite comprobar lo que efectivamente exponga su simulador; no acredita hardware HP ni FieldUnit. No instalar en el Victus diario para sustituir estas pruebas.

| Caso | Criterio de aceptación | Evidencia necesaria |
|---|---|---|
| Carga/start sin cliente | Cero evaluaciones ACPI automáticas; pila base iniciada | Trazas de envío, PnP y estado de pila |
| Perfil SMBIOS o instancia ajenos | Sin interfaz publicada ni target ACPI | Callbacks/objetos y estado base |
| Fallo de creación de cola/interfaz | Sonda enclavada, start continúa; IOCTL privado no pasa a ACPI | Inyección de fallo y trazas de requests |
| Fallo de creación de target | PdoTarget nulo, Faulted enclavado, start continúa | Inyección de fallo de WDF |
| Fallo de apertura de target | Objeto parcial eliminado, sin fuga, start continúa | Inyección, Verifier y estado base |
| Prepare después de fallo | Sin nueva creación/apertura, sin AML | Contadores por vida de dispositivo |
| Release repetido después de fallo | Sin close/delete de handle inválido | Verifier y trazas de objetos |
| Stop/start normal | Target cerrado/recreado; Used y Faulted se conservan | Callbacks y presupuesto observado |
| Sleep/resume normal | Sin AML automático ni restauración de presupuesto | Captura de energía y requests |
| Control _STA válido | Una evaluación, entero 0Fh; respuesta cruda y duración | Salida cliente y trazas ACPI |
| Segundo _STA | Rechazo sin segunda evaluación | Conteo de envíos |
| Selector EC | Rechazo por política, sin evaluación de FieldUnit | Código de rechazo y conteo de envíos |
| Respuesta nativa errónea/malformada | Valid=0, estado/bytes preservados, enclavamiento | Simulador de respuestas y salida cruda |
| Timeout/cancelación durante control | Sin uso de buffer liberado ni reintento; medir retirada real | Simulador de retraso, debugger y Verifier |
| Stop/remove con control pendiente | Sin deadlock, fuga ni evaluación adicional | Verifier, objetos y tiempos de callback |
| Usuario no administrador | Apertura rechazada por ACL | Prueba desde cuenta estándar |
| IOCTL/read/write ajenos de usuario | Rechazo sin envío inferior | Prueba negativa y traza de pila |
| IOCTL ajeno de kernel | Passthrough conserva estado y datos originales | Cliente kernel de laboratorio y lower target |
| Retirada de extensión | Paquete base y otros filtros preservados | Inventarios antes/después y reinicio si se requiere |
| Eventos ACPI | Buscar 13/15 y conservar también otros errores | EVTX antes/durante/después; ZIP con hashes |

El timeout de cinco segundos solicitado a WDF no impone una cota al firmware/SMM ni garantiza cancelación inmediata. Un cliente terminado no prueba retirada segura. Driver Verifier puede detectar defectos y provocar un bugcheck; el entorno debe poder recuperarse. No hay resultados de estas pruebas todavía.

## Diseño de despliegue y retirada pendiente

1. Antes de staging: verificar identidad exacta, versión de Windows, estado de seguridad y firma efectiva, hashes y contenido INF/SYS/CAT, inventario de EC, paquete base y filtros actuales. El ID INF PNP0C09 es genérico: el gate kernel no restringe el alcance de staging. Rechazar inventarios distintos del perfil cualificado.
2. Registrar el nombre publicado real oemN.inf, proveedor, ExtensionId y DriverVer del paquete instalado; no deducir un número fijo ni borrar paquetes por patrón. Conservar el paquete previo si se reemplaza una versión de la misma extensión.
3. Primera prueba física posterior a laboratorio/firma: solo _STA, una llamada; no campos, setters, Automatic ni persistencia. Capturar estado PnP, pila, eventos y salida cruda. No reintentar automáticamente un fallo.
4. Retirada: usar la operación documentada de desinstalación de esa extensión identificada, preservando el INF base. No borrar Acpi.sys, machine.inf, el dispositivo EC, valores globales de filtros ni extensiones ajenas. No usar force como recuperación normal. Si se solicita reboot, registrar y verificar después de él.
5. Comparar inventarios antes/después y verificar inicio del EC y ausencia de la extensión, no solo el código de salida del proceso. Si hay retirada pendiente o bloqueo, conservar evidencia y detener promoción. Un rollback documentado no equivale a rollback ejecutado.

No se incluye un instalador o comando físico listo: falta firma, laboratorio, recuperación verificada y resultados de la matriz.

## Alternativas de firma/transporte

| Ruta | Lo establecido | Lo que falta | Decisión |
|---|---|---|---|
| KMDF actual + firma Microsoft | Nuevo driver kernel sujeto a firma Microsoft; attestation/WHCP requieren EV asociado a la cuenta | Cuenta/certificados, envío aceptado, firma real y runtime | Ruta conocida; no comprar certificado solo porque la CI pase |
| UMDF 2 | Windows documenta evaluación ACPI desde UMDF y filtros UMDF | Diseño del lower target, paso de clientes kernel, identidad/ACL, compatibilidad EC, firma de paquete e instalación | Alternativa a investigar; aún sin prototipo ni evidencia de viabilidad en este EC |
| Certificado local del KMDF | Puede servir en entornos de prueba configurados para ello | No satisface la ruta actual manteniendo intacta la seguridad del Victus | No presentar como solución desplegable en este equipo |

UMDF no es una aplicación normal ni elimina la adjunción a una pila PnP. WdfDeviceWdmGetPhysicalDevice es exclusivo de KMDF, por lo que no puede trasladarse el transporte actual sin rediseño. El soporte UMDF de métodos ACPI tampoco prueba que Acpi.sys evalúe un FieldUnit. Deben estudiarse explícitamente UmdfKernelModeClientPolicy y las restricciones de requests/file objects antes de permitir clientes kernel.

Microsoft distingue firma de binario en modo usuario y firma del paquete para instalación; el tutorial no permite concluir que este paquete UMDF concreto pueda instalarse sin firma en Windows 11. La documentación genérica no acredita una ruta gratuita que preserve todas las condiciones actuales. Una cuenta Hardware Dev Center para attestation/WHCP necesita EV asociado vigente; una presentación individual puede usar otro certificado Authenticode registrado. No equivale a exigir un EV distinto para cada envío.

## Orden para continuar

Cerrar primero las pruebas de fallo/lifecycle en un entorno apto; estudiar UMDF como alternativa de transporte y establecer una vía de firma compatible. Después preparar el harness físico _STA con captura independiente. No promover lecturas EC por éxito de _STA: el soporte FieldUnit y el byte ECh completo siguen sin resolverse. GM11 conserva su cualificación de RPM y no sustituye setpoints, ownership ni readback de restauración.

## Fuentes primarias

- https://learn.microsoft.com/en-us/windows-hardware/drivers/ddi/wdfdevice/nc-wdfdevice-evt_wdf_device_prepare_hardware
- https://learn.microsoft.com/en-us/windows-hardware/drivers/ddi/wdffdo/nf-wdffdo-wdffdoinitsetfilter
- https://learn.microsoft.com/en-us/windows-hardware/drivers/install/using-an-extension-inf-file
- https://learn.microsoft.com/en-us/windows-hardware/drivers/acpi/evaluating-acpi-control-methods
- https://learn.microsoft.com/en-us/windows-hardware/drivers/ddi/wdfdevice/nf-wdfdevice-wdfdevicewdmgetphysicaldevice
- https://learn.microsoft.com/en-us/windows-hardware/drivers/wdf/supporting-kernel-mode-clients-in-umdf-drivers
- https://learn.microsoft.com/en-us/windows-hardware/drivers/install/windows-driver-signing-tutorial
- https://learn.microsoft.com/en-us/windows-hardware/drivers/dashboard/code-signing-reqs
