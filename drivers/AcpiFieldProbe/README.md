# ACPI Field Probe — prototipo de fuente/compilación, no paquete instalable

Target investigado: HP 8C40/63.43/9D0R1LA/F.18, Windows 11 x64 build 26200. La captura `ACPI-BrokerPreflight_20261003_232612_2fcceb.zip` tiene SHA-256 `4278d70aa26b86c7ef666d4a8ce9236e50ca24ed8593095d085b4b6f1b6d9684`: seis miembros de manifiesto íntegros, un EC `ACPI\PNP0C09\1` iniciado, PDO observado `\Device\0000004e`, pila ACPI, propiedades de filtros vacías; cuatro dispositivos PNP0C14 con WmiAcpi/ACPI. Las 55 consultas de propiedades no registran errores y PnPUtil retorna cero cinco veces. El archivo acpi.sys reporta versión 10.0.26100.1; esto no identifica por sí solo una imagen kernel cargada. El nombre PDO observado no se fija en el código.

## Implementación

- `driver.c`: filtro KMDF PnP; reconoce hardware ID PNP0C09, recibe el PDO de su propia pila y abre un I/O target de ese objeto. No busca objetos por nombre privado ni toca puertos 62h/66h. Prepara/cierra el target con el ciclo PnP. Cero evaluación automática durante carga, start, resume o unload.
- Interfaz propia con ACL SYSTEM/Administradores. IOCTL privado BUFFERED/READ con entrada exacta versión+selector y salida fija. Bloquea IOCTLs arbitrarios/read/write de usuario antes del passthrough. Las solicitudes ajenas del kernel mantienen el passthrough normal del filtro.
- Whitelist fija: `_STA`, `SRP1`, `SRP2`, `SFAN`, `FFFF`, `FFFS`, todos hijos inmediatos EC0 en el AML revisado. Se envía `IOCTL_ACPI_EVAL_METHOD` sin argumentos. Que el envío esté implementado no demuestra soporte Windows de FieldUnit.
- El build actual fija `VFC_FIELD_PROBES_ENABLED=0`: únicamente `_STA` puede ejecutarse. Los campos EC retornan NOT_SUPPORTED **por política del prototipo**, no por una respuesta de Acpi.sys. No cambiar el define para una prueba física sin resolver los gates pendientes.
- Una sola cola secuencial y una llamada por selector durante la vida del dispositivo; presupuesto consumido antes de enviar. `_STA` debe devolver entero 0Fh válido antes de campos. Cualquier fallo de transporte/contrato enclava el dispositivo. Stop/start o sleep no resetea el presupuesto ni el fallo; no hay reintentos ni polling.
- Validación estricta de firma, 20 bytes de salida v1, Count=1, argumento INTEGER/ULONG, tamaño cuatro bytes y rangos (bytes 0–255, bits 0–1). Cero es válido solo tras un contrato válido; Valid=0 nunca equivale a una lectura cero. La salida conserva NTSTATUS, bytes nativos, hasta 20 bytes crudos y ticks/frecuencia de duración.
- Timeout solicitado de cinco segundos al framework. No se garantiza cancelación del trabajo firmware/SMM ni cota de retirada/unload si la pila no completa. No afirmar que matar el cliente cancele una operación firmware. Estas limitaciones requieren prueba de fallos en un entorno adecuado antes del equipo de uso diario.
- `probe-client.c`: resuelve la interfaz propia con SetupAPI, rechaza varias interfaces y ofrece exclusivamente `--control` para `_STA`. No toma nombres de método, rutas PDO o direcciones del usuario. No instala ni carga drivers. La ausencia de interfaz es un error de despliegue distinto de soporte ACPI.

## Compilación verificable

`powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\Build-Victus-AcpiFieldProbe.ps1`

Solo en Windows con Visual Studio C++ x64 y NuGet. Restaura paquetes oficiales WDK/SDK 10.0.26100.1, compila/enlaza KMDF x64, ejecuta contratos nativos y verifica el rechazo del cliente sin interfaz instalada. El script exige un entorno limpio sin este prototipo desplegado. Registra versión KMDF y hashes. CI produce `acpi-field-probe-review-only`: SYS **sin firma**, cliente, fixtures y manifiesto. No incluye INF, CAT, comandos de instalación ni cambios de seguridad. Este trabajo está separado del backend y de la solución C# vigente.

## Gates pendientes antes de instalar o habilitar campos

1. Diseño de instalación como filtro específico, sin sustituir machine.inf/Acpi.sys y sin filtros globales de clase; rollback, PnP/power/remove/cancel/ACL revisados y probados. Falta validación con Driver Verifier y hardware/VM apropiado. La compilación no valida comportamiento PnP.
2. Calificar físicamente el gate de identidad implementado y verificar namespace/correspondencia EC0. El gate comprueba SMBIOS HP/8C40/63.43/modelo/SKU exacto/F.18 e instancia PnP exacta; eso no prueba el namespace ni soporte FieldUnit.
3. Firma/despliegue compatible con seguridad del equipo; no instrucciones para desactivar Secure Boot, HVCI ni firma. No existe paquete firmado en esta etapa.
4. Harness físico acotado con observación de eventos ACPI 13/15, salida cruda/tiempos, captura ZIP y validación independiente. Solo entonces habilitar candidatos mediante un cambio revisado y su CI.
5. El byte ECh completo carece de campo nombrado en la revisión: FFFF/FFFS no cubren bits restantes. El prototipo **no** puede calificar ownership/restauración del backend aunque SRP1/SRP2/SFAN se lean. No setters, restauración, Manual ni Automatic.

Este prototipo concreta el transporte y contrato para revisión; **FieldUnit, guarda completa y seguridad física permanecen sin calificar**. No ejecutar `probe-client --control` como sustituto de la próxima prueba física: primero hace falta un despliegue revisado y firmado.

Fuentes primarias consultadas:
- https://learn.microsoft.com/en-us/windows-hardware/drivers/acpi/evaluating-acpi-control-methods
- https://learn.microsoft.com/en-us/windows-hardware/drivers/ddi/wdfiotarget/nf-wdfiotarget-wdf_io_target_open_params_init_existing_device
- https://learn.microsoft.com/en-us/windows-hardware/drivers/ddi/wdfdevice/nf-wdfdevice-wdfdevicewdmgetphysicaldevice
- https://learn.microsoft.com/en-us/windows-hardware/drivers/ddi/wdfdevice/nf-wdfdevice-wdfdeviceinitassignsddlstring
- https://learn.microsoft.com/en-us/windows-hardware/drivers/ddi/wdfiotarget/nf-wdfiotarget-wdfiotargetsendioctlsynchronously
- https://learn.microsoft.com/en-us/windows-hardware/drivers/install-the-wdk-using-nuget

## Etapa de identidad implementada

El driver consulta metadatos con AuxKlibGetSystemFirmwareTable(RSMB) y WdfDeviceQueryPropertyEx(InstanceId), APIs documentadas, sin métodos ACPI ni puertos. Verifica tipos SMBIOS 0/1/2 únicos, versión/formato, límite 1 MiB, campos y terminaciones dentro del buffer y Type127 final. Requiere HP, placa 8C40, versión 63.43, modelo Victus by HP Gaming Laptop 15-fa1xxx, SKU exacto 9D0R1LA#AKH y BIOS F.18, más ACPI\PNP0C09\1. No compara fabricante de BIOS porque la evidencia disponible no lo fijó. No almacena ni imprime seriales/UUIDs. SMBIOS identifica el perfil, **no es atestación criptográfica**. Formatos distintos, padding después de Type127 y múltiples placas se rechazan; no se calificaron físicamente estas restricciones todavía.

La identidad se verifica antes de publicar interfaz y antes de abrir target en PrepareHardware. Un rechazo deja el filtro inactivo sin publicar interfaz inicialmente, en vez de fallar el start por ese rechazo. Una pérdida de identidad en un start posterior enclava el rechazo. La preservación del dispositivo base y todos los ciclos PnP aún requieren prueba de runtime/Verifier; no basta la compilación.

`probe-client.exe --identity` ejecuta **el mismo parser de SMBIOS en modo usuario**, sin instalar/cargar el driver ni evaluar ACPI. Es la siguiente comprobación física disponible: debe imprimir identity_match:true en este equipo. No verifica el callback kernel, el PDO ni FieldUnit. En CI debe rechazar el SMBIOS del runner ajeno al target; los fixtures sintéticos verifican límites y perfiles erróneos.

Despliegue propuesto: extensión específica de dispositivo mediante AddFilter, conservando machine.inf/Acpi.sys; posición, INF/CAT y rollback aún pendientes de validación. La ruta de firma de Microsoft requiere cuenta Hardware Dev Center y certificado EV asociado; no existe esa firma en este proyecto. No se sustituye por un certificado local ni se solicita cambiar la seguridad del Victus. **No instalar el SYS de este artifact.**

Fuentes adicionales:
- https://learn.microsoft.com/en-us/windows-hardware/drivers/ddi/aux_klib/nf-aux_klib-auxklibgetsystemfirmwaretable
- https://learn.microsoft.com/en-us/windows/win32/api/sysinfoapi/nf-sysinfoapi-getsystemfirmwaretable
- https://www.dmtf.org/sites/default/files/standards/documents/DSP0134_3.7.0.pdf
- https://learn.microsoft.com/en-us/windows-hardware/drivers/install/inf-addfilter-directive
- https://learn.microsoft.com/en-us/windows-hardware/drivers/install/kernel-mode-code-signing-policy--windows-vista-and-later-
