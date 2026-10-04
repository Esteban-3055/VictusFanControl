# Primer gate del broker: Windows y pila PnP

La captura GM11 de 18 rondas ya respaldó telemetría durante reposo/carga y la subida sostenida por calor confirmada por el usuario. Este paso obtiene otra evidencia: plataforma Windows y dispositivos PnP a los que podría pertenecer un futuro broker. No repite la prueba de juego.

## Resultado de la investigación de interfaces

Microsoft documenta IOCTL_ACPI_EVAL_METHOD/EX para métodos dentro del namespace del PDO correspondiente; no se encontró en las fuentes revisadas una garantía de evaluación de FieldUnit EC. Esto deja esa capacidad **sin demostrar**, no demuestra que Windows la rechace. Enumerar un nombre o conocer el PDO tampoco demuestra que pueda evaluarse el campo.

RegisterOpRegionHandler no es un lector genérico para apropiarse de ERAM: Microsoft reserva los espacios inferiores a 80h para uso interno y prescribe regiones de fabricante 80h–FFh para esta interfaz. ERAM del DSDT es EmbeddedControl, por lo que registrar un manejador propio no es un contrato documentado de sustitución del manejador EC del sistema. ACPI_INTERFACE_STANDARD/2 tampoco aporta un getter genérico ERAM en las estructuras revisadas.

El namespace del DSDT suministrado sitúa EC0 en `\_SB.PC00.LPCB.EC0`, con ERAM y campos SRP1/SRP2/SFAN. Esta ruta estática no se deriva del nombre PDO y no se presupone que Windows exponga una interfaz de usuario para evaluarla. ECh contiene bits sin nombre: incluso una lectura válida de los campos nombrados dejaría pendiente la guarda completa exigida hoy.

## Ejecutar una captura de metadatos

Actualizar la rama `feature/victus-8c40-wmi-broker-5sample` y abrir **Windows PowerShell como administrador** en la raíz del repositorio:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\Export-Victus-AcpiBrokerPreflight.ps1
```

Mantener el equipo en Firmware. No hace falta abrir un juego ni instalar OMEN Gaming Hub. El script valida 8C40/63.43/9D0R1LA/F.18, obtiene metadatos CIM del sistema y propiedades de dispositivos presentes PNP0C09/PNP0C14. Usa únicamente `pnputil /enum-devices`, con opciones según build: Windows 10 ofrece relaciones/controladores, Windows 11 añade servicios/pila/interfaces. Cada proceso PnPUtil tiene una espera de 30 segundos y mensajes cada cinco segundos mientras está pendiente. Las consultas PnP de PowerShell son consultas de metadatos síncronas, no están cubiertas por ese límite individual.

Conserva los errores de propiedades ausentes o inaccesibles y los códigos/salida de PnPUtil; no convierte una ausencia en valor cero ni en prueba de imposibilidad. Si hay varios EC, conserva todos, sin elegir uno automáticamente. No invoca HP WMI/AML, no accede a puertos EC, no instala ni cambia drivers y no modifica filtros ni configuración de seguridad.

Al terminar imprime el ZIP `diagnostics/ACPI-BrokerPreflight_*.zip`; adjuntar ese archivo y su `.zip.sha256`. Incluye `hardware.json`, `windows.json`, `devices.json`, `pnp-properties.json`, `pnputil.json`, `summary.json` y manifiesto SHA-256. Una identidad distinta produce una captura de rechazo con error, sin continuar a la enumeración PnP. No se recopilan números de serie, nombre de usuario/equipo ni lista de todos los dispositivos en los archivos de salida; las rutas de instancia de los dispositivos seleccionados sí forman parte de la evidencia.

`MetadataCollected=true` indica que el inventario recorrió sus fases, aunque contenga errores individuales. **No es un PASS del broker**. FieldUnitSupport y CompleteEcGuardSupport permanecen `unproven`, ProductionReady permanece false. Esta captura tampoco es una prueba física del transporte o de restauración.

## Decisión después de recibir el ZIP

1. Verificar build, versión de Acpi.sys, identidad, número de EC y errores de captura.
2. Contrastar PDO, padre, servicio, filtros y pila efectiva; Windows 10 puede dejar pendiente el detalle de pila. Un PDOName es un nombre del objeto, no autorización ni handle de acceso.
3. Concretar cómo probar la evaluación de SRP1/SRP2/SFAN con un componente PnP de lectura, compatible con firma y seguridad del equipo. No se entrega un instalador hasta tener diseño/build revisable y una cobertura explícita de la guarda completa.
4. Mantener bloqueada la promoción de Manual/Automatic mientras falten lectura de consignas, guarda ECh completa y ACK independiente de restauración FF/FF.

## Verificación y fuentes

CI valida sintaxis PowerShell, argumentos de enumeración en PowerShell 7/5.1, rechazo de IDs no previstos/argumentos inyectados, y captura ZIP/manifiesto tras rechazo de identidad en un Windows ajeno al target. Estas pruebas no evalúan FieldUnit ni califican el EC físico.

- https://learn.microsoft.com/en-us/windows-hardware/drivers/ddi/acpiioct/ni-acpiioct-ioctl_acpi_eval_method_ex
- https://learn.microsoft.com/en-us/windows-hardware/drivers/acpi/registering-and-deregistering-an-operation-region-handler
- https://learn.microsoft.com/en-us/windows-hardware/drivers/install/devpkey-device-pdoname
- https://learn.microsoft.com/en-us/windows-hardware/drivers/devtest/pnputil-command-syntax
- https://learn.microsoft.com/en-us/powershell/module/pnpdevice/get-pnpdeviceproperty
- `acpi-residual-control-decision-8c40-f18.md` y su inventario JSON.
