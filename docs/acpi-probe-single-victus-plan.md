# Continuación de la sonda con un único Victus

Fecha: 2026-10-04. HP 8C40 / BIOS F.18 / EC ACPI\PNP0C09\1. El usuario confirmó que solo dispone del Victus; no se presupone otro computador, un disco externo ni un entorno de recuperación ya probado.

## Trabajo realizado sin instalar el driver

Se extrajo la lógica de creación, apertura y liberación a lifecycle.h, compartida por el driver y un fixture host con inyección de fallos. Catorce escenarios comprueban el orden y ownership, liberación exacta, bloqueo sin reintentos y conservación del presupuesto. El driver mantiene STATUS_SUCCESS para fallos de configuración de la sonda opcional. Prepare duplicado ya no puede sobrescribir un target publicado: enclava el fallo y deja la liberación a Release. Se añadió el fixture al build Windows y al manifiesto del artifact de revisión. El INF incrementa su versión a 0.1.0.2.

Estas pruebas no acreditan planificación/cancelación WDF, PnP físico, suspensión, ACL efectiva, arranque o retirada de un filtro instalado. Las filas de runtime de la matriz anterior siguen pendientes. No se cargó un driver ni se evaluó ACPI durante los fixtures host.

## Próxima captura: recuperación y estado de seguridad

Con PowerShell como administrador, desde la raíz del repositorio y su rama de investigación actualizada:

```powershell
git pull --ff-only
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\Export-Victus-AcpiBrokerPreflight.ps1 -RecoveryMetadata
```

Conservar Firmware. No hace falta un juego. Enviar el ZIP ACPI-BrokerPreflight y su .zip.sha256 de diagnostics. Además del inventario EC anterior, recovery-metadata.json contiene:

| Consulta | Datos incluidos | Límite |
|---|---|---|
| Confirm-SecureBootUEFI | Booleano o error explícito | No cambia Secure Boot |
| Get-BitLockerVolume, solo volumen del sistema | Estado, protección, porcentaje y bloqueo | No consulta/exporta KeyProtectors ni contraseñas de recuperación |
| Win32_DeviceGuard | Estados VBS y servicios de seguridad configurados/en ejecución | No cambia HVCI ni políticas |
| reagentc /info | Salida, error y código de proceso | WinRE configurado no equivale a recuperación probada |

La captura restringe los procesos nativos a operaciones de inventario con plazo de 30 segundos y progreso cada cinco segundos mientras espera. Las consultas fallidas no se convierten en false ni en éxito. RecoveryVerified=false e InstallationReady=false son explícitos: este inventario no prueba que el usuario pueda recuperar Windows offline. No se recopila la clave BitLocker; comprobar su disponibilidad, si corresponde, se hará de forma privada.

## Decisión de despliegue que falta

No hay firma Microsoft, instalador ni rollback físico calificado. Con un único equipo, separar una instalación de Windows de ensayo puede reducir la exposición de la instalación cotidiana, pero comparte firmware/EC y no resuelve automáticamente la firma. Una VM en el Victus sirve para ciertas pruebas de software y PnP simulado; no expone necesariamente el EC HP ni acredita FieldUnit.

Las vías documentadas requieren distinguir:

- Firma Microsoft por attestation: confianza Windows sin HLK completo; requiere la ruta de Hardware Dev Center/certificados. No certifica funcionamiento.
- Firma de preproducción: permite pruebas con Secure Boot habilitado, pero solo en dispositivos provisionados expresamente y mediante la ruta de partners. No permite cargar nuestro SYS sin firma en un Windows retail sin cambios.
- Firma local de ensayo: necesita un entorno Windows configurado para test signing; no se ha preparado ni autorizado aquí ningún cambio de seguridad. Si se elige esa ruta posteriormente, debe describirse su alcance real y la recuperación antes de dar comandos de modificación.

Fuentes revisadas:
- https://learn.microsoft.com/en-us/windows-hardware/drivers/dashboard/driver-signing-offerings
- https://learn.microsoft.com/en-us/windows-hardware/drivers/install/configuring-the-test-computer-to-support-test-signing
- https://learn.microsoft.com/en-us/windows-hardware/drivers/install/kernel-mode-code-signing-policy--windows-vista-and-later-

El inventario permite elegir el entorno de prueba con datos reales. No habilita campos EC, setters, ownership ni restauración. La guarda ECh completa sigue sin una ruta identificada, incluso si en una etapa posterior SRP1/SRP2/SFAN pudieran evaluarse.
