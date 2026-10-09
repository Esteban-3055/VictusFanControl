# Lectura residual y restauración: decisión 8C40 / F.18

## Resultado de la revisión

GM11 queda respaldado como candidato de telemetría por la captura física de 18 rondas, el AML y la decodificación de Linux. Los getters revisados de este firmware no proporcionan la evidencia completa que el backend actual exige para adquirir autoridad, confirmar consignas y verificar restauración. No hay una nueva prueba de escritura calificada para ejecutar todavía.

Se revisaron las 17 tablas suministradas y las siete vistas Field de ERAM por offsets, además de referencias de campos, rutas de lectura/escritura y requisitos efectivos del backend. El inventario JSON adjunto conserva hashes, offsets y referencias; es un inventario estático, no un analizador semántico completo ni una prueba de ausencia de protocolos SMM opacos.

| Requisito | Fuente real | Getter revisado | Estado |
|---|---|---|---|
| Consigna CPU | EC 34h, SRP1 | GM2E escribe SRP1; no getter explícito encontrado | Falta lectura/ACK |
| Consigna GPU | EC 35h, SRP2 | GM2E escribe SRP2; no getter explícito encontrado | Falta lectura/ACK |
| Guarda completa | EC ECh | FFFF es bit 1, FFFS bit 2; GM26 sólo retorna FFFS | Falta byte completo |
| FanSwitch | EC F4h, SFAN | FSSP escribe 0/2; no getter encontrado | Falta lectura |
| RPM | H2RA, GM11; EC/SMM, GM2D | Ambos observados físicamente | Telemetría; no ACK de consigna |
| Restauración FF/FF | GM2E FF/FF seguido de GM1A LegacyDefault | Hay ruta de comandos; ACK actual depende de leer consignas | Falta verificación independiente sin puertos directos |

Las seis vistas adicionales ERAM empiezan en 04h y terminan en 05h, 04h, 0Bh, 13h, 1Bh y 23h. Ninguna alcanza los cuatro registros requeridos. No se encontró Alias de SRP1/SRP2/SFAN/FFFS/ERAM en las tablas revisadas. El SFAN de SSDT13 pertenece a un buffer de GPU y no es el byte EC F4h. SFS1/SFS2 de H2RA son destinos de GM12; no hay equivalencia demostrada con SRP1/SRP2. FMR/FSUS y FAS1/FAS2 no proporcionan esa equivalencia por nombre o proximidad.

RPIO usa EI01/EI02/EI03 en SystemIO 381h/382h/383h. No se conoce un contrato que permita tratar sus argumentos como una dirección ERAM arbitraria. ECMD escribe y espera en un bucle sin cota explícita. SMRD es un protocolo SMBus que escribe su infraestructura; no es un getter genérico EC. No se califican esas rutas mediante barridos de argumentos. GM30/otros wrappers WSMI dependen de comportamiento SMM no documentado por este AML; retornar datos no basta para conocer su semántica o efectos.

## Qué exige hoy el programa

`Hp8C40FanControlBackend.EnterCustomModeAsync` exige byte MaxFan=00, FanSwitch=00 y consignas FF/FF. En cada comando verifica ownership y ACK de la consigna solicitada. `RestoreLockedAsync` envía la secuencia de liberación y espera lectura de FF/FF. `RestoreWithWatchdogLockedAsync` añade confirmación de liberación y limpieza del journal por el watchdog.

Por tanto, rc=0 del setter, FFFS=0 o RPM que cambian no prueban restauración. Una consigna fija coincidente con el estado térmico puede producir las mismas RPM que Firmware. Matar un proceso no confirma que un setter terminó ni que se canceló; finally tampoco cubre doble muerte o llamada firmware bloqueada. No se elimina un journal retenido para simular éxito.

## Alternativas evaluadas

| Ruta | Alcance actual | Decisión |
|---|---|---|
| Getters WMI existentes | RPM y FFFS | Útiles para telemetría; cobertura residual insuficiente |
| Broker PnP hacia Acpi.sys | Posible invocación de objetos/métodos del dispositivo EC | Candidato de investigación; soporte de evaluación directa de FieldUnit pendiente |
| Getter AML adicional proporcionado por firmware | Podría devolver SRP1/SRP2, ECh y SFAN por la región EC del sistema | Requiere una implementación/provisión distinta del firmware; no existe en la revisión |
| EC directo reducido a transiciones | Conserva la lectura de todos los bytes | Sigue fuera de la coordinación demostrada; reducir frecuencia no resuelve los eventos de Scenario C |
| Cambiar el criterio de autoridad a RPM/FFFS | Evitaría lecturas que faltan | No satisface ACK, conflictos ni restauración; no adoptado |
| Interfaces SMM/puertos alternativos desconocidos | Semántica y efectos pendientes | No calificadas; no se incorporan al whitelist |

La documentación pública de Microsoft describe IOCTL_ACPI_EVAL_METHOD/EX para métodos soportados en el namespace del PDO y sus descendientes. ACPI_INTERFACE_STANDARD/2 expone gestión GPE/notificaciones, sin una función genérica de lectura ERAM en las estructuras revisadas. No se deduce de esos documentos que evaluar directamente una FieldUnit esté soportado, ni se afirma que sea imposible en toda versión de Windows. Invocar el IOCTL requiere además una ruta PnP válida: un handle genérico no permite seleccionar cualquier objeto del namespace.

## Próximo gate concreto: viabilidad del broker

La ruta de investigación seleccionada es un broker estrecho hacia Acpi.sys, condicionado a demostrar que puede obtener los valores existentes sin transacciones directas por puertos y sin añadir/modificar tablas ACPI. Esto todavía no es un driver listo para instalar.

1. Demostrar soporte real de evaluación de FieldUnit en la versión objetivo de Windows o localizar otro contrato documentado de lectura. Identificar PDO/stack EC correcto y límites de namespace. Un ejemplo que sólo invoca _STA o GM11 no cierra este requisito.
2. Si ese soporte existe, implementar un broker de lectura con whitelist fija SRP1/SRP2 y guardas realmente disponibles; sin método/payload/dirección arbitrarios ni setters. Firmado e instalado mediante PnP compatible con la seguridad del equipo. No reemplazar Acpi.sys ni usar offsets privados del kernel.
3. Verificar rc/estado, tipo/tamaño, tiempos y lecturas no atómicas; cota de espera, parada y manejo de llamadas tardías. No interpretar salida cero o NTSTATUS de transporte como una lectura válida de ownership.
4. Calificar físicamente primero en Firmware: identidad exacta, snapshot de consignas y guardas, observación de ACPI 13/15 y evidencia. El nuevo broker debe permitir detectar datos inválidos y pérdida de observabilidad.
5. Sólo después, diseñar transiciones y recuperación independiente con ACK de consignas, restauración FF/FF y watchdog/journal. Repetir fallos/bloqueos/crashes; no promover Manual/Automatic por haber obtenido un primer snapshot.

Si el primer gate falla, la solución completa con este firmware y las interfaces investigadas permanece bloqueada; necesitaría un getter provisto por firmware o cambiar el requisito/plataforma con sus consecuencias explícitas. No se programa una prueba de ventiladores cuya restauración dependa de evidencia inexistente.

## Fuentes

- DSDT/16 SSDT suministrados; hashes en `acpi-residual-field-inventory-8c40-f18.json`.
- Código vigente `Hp8C40FanControlBackend.cs`, `Hp8C40BiosFanControl.cs`, `AcpiEcReader.cs`.
- Captura física y aceptación del usuario registradas en `acpi-coverage-session-2026-10-03.md`.
- Microsoft: https://learn.microsoft.com/en-us/windows-hardware/drivers/acpi/evaluating-acpi-control-methods
- Microsoft: https://microsoft.github.io/windows-docs-rs/doc/windows/Wdk/System/SystemServices/struct.ACPI_INTERFACE_STANDARD.html
- Microsoft: https://microsoft.github.io/windows-docs-rs/doc/windows/Wdk/System/SystemServices/struct.ACPI_INTERFACE_STANDARD2.html

Estado: revisión estática completada, candidato GM11 aceptado para la sesión; broker no implementado/instalado ni calificado físicamente. Backend y whitelist de hardware permanecen en su alcance existente.

Seguimiento: `acpi-broker-preflight-8c40-f18.md` documenta la investigación de interfaces y una captura de metadatos Windows/PnP para concretar el dispositivo del futuro broker. El inventario no evalúa FieldUnit ni cierra la guarda ECh completa.
