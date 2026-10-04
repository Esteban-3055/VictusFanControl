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
2. Gate efectivo de identidad completa en ejecución (el chequeo PNP0C09 actual es genérico, **no verifica placa/BIOS**), namespace y correspondencia EC0. No instalar este build en el Victus para inferir esas propiedades.
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
