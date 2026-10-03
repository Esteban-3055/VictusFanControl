# Ensayo conjunto ACPI: 8C40 / F.18

`scripts/Test-Victus-AcpiCoverage.ps1` ejecuta una consulta de control y tres rondas de diagnóstico ECOK, lectura FFFS, diagnóstico ECOK posterior y RPM de ambos ventiladores. Las operaciones nativas son secuenciales; no se carga PawnIO ni se envían setters. El backend de producción conserva sus requisitos actuales.

## Alternativa a cambiar los ventiladores

En el DSDT suministrado, GBIF adquiere MUT1, comprueba ECOK y devuelve **0x0D** si es falso. Después comprueba el selector recibido y devuelve **0x06** si no es cero. Ambos caminos liberan el mutex y esperan 150 ms antes de retornar; ocurren antes de consultar MBTS y de la ruta de lectura de batería. No llaman a WSMI ni escriben campos EC en ese prefijo.

HWMC despacha Command=1/CommandType=7 a GBIF cuando hay entrada, tomando como selector el primer byte. Sin entrada devuelve **0x05** sin llamar a GBIF. Su epílogo transporta el código del paquete de retorno en RETC. Se utiliza hpqBIOSInt4 para ambos casos. El epílogo común limpia WBUF; la variante con entrada copia el payload a ese buffer compartido. Son cambios de la infraestructura WMI, no setters de ventiladores.

Esto permite buscar evidencia del estado de ECOK **sin activar GM27**. Los códigos 5 y 6 son rechazos diagnósticos intencionales, no errores que se deban reintentar. El código 13 y cualquier respuesta diferente detienen el ensayo.

La inferencia depende de que el proveedor preserve estos códigos y de que el AML activo corresponda al suministrado. No autentica el firmware ni garantiza la integridad del transporte EC. El control vacío mejora la evidencia, pero no elimina la posibilidad de que un proveedor produzca un código coincidente. En las 17 tablas suministradas ECOK sólo tiene inicialización cero y asignación a uno en _REG; no representa salud actual y no se limpia al desconectar la región.

Referencias DSDT: HWMC 123999, despacho GBIF 124106–124117, GBIF 125543–125577, epílogo HWMC 124984–125018, GM26 128699, ECOK 134822/134832, GM2D 128875. El hash del DSDT suministrado está registrado en `acpi-gm26-investigation-2026-10-03.md`.

## Contratos

| Operación | Command / type | Entrada | Salida | Resultado esperado |
|---|---|---|---:|---|
| Control GBIF, una vez | 1 / 7 | Vacía | 4 bytes | rc=5, cuatro ceros |
| Diagnóstico ECOK | 1 / 7 | 01-00-00-00 | 4 bytes | rc=6, cuatro ceros |
| FFFS | 20008h / 26h | Vacía | 4 bytes | rc=0; 00-00-00-00 o 01-00-00-00 |
| RPM | 20008h / 2Dh | 00-00-00-00 | 128 bytes | rc=0; primeros dos bytes 0..100, resolución nominal 100 RPM |

Tras el control inicial se realizan tres rondas `ECOK antes → FFFS → ECOK después → RPM`. Se deja un segundo después de cada respuesta y al menos cinco segundos entre rondas. Se registra cada llamada antes de invocar y cada respuesta antes de validarla. Una primera respuesta no esperada detiene la secuencia sin repetirla ni probar payloads alternativos. La muestra RPM se rechaza si la llamada alcanza la frontera de frescura de 3 s del lector existente.

El padre muestra progreso aproximadamente cada cinco segundos, observa nuevos ACPI 13/15 mediante cursor y UTC XML, e impone un plazo de 120 s al hijo. Observa dos segundos después de terminar. Un plazo o fallo de observación rechaza la captura. Terminar un proceso no garantiza que una llamada WMI/firmware en curso quede cancelada. El límite tampoco garantiza avance ante un bloqueo del propio observador de Windows.

Los resultados de cada ronda son secuenciales, **no una instantánea atómica**. El CSV sólo publica rondas con los cuatro contratos validados. El JSONL preserva respuestas parciales o inválidas. Si ECOK se infiere presente antes y después, un FFFS=0 queda respaldado por evidencia de la condición del getter según el AML, en vez de únicamente por su valor inicial. Eso no reemplaza una prueba de transición ni prueba estabilidad prolongada.

## Cobertura y pendientes

| Requisito | Qué entrega este ensayo |
|---|---|
| Condición ECOK de GM26 | Evidencia indirecta basada en AML y códigos del proveedor, antes y después |
| FFFS | Tres lecturas con esa evidencia; no se cambia el bit |
| RPM CPU/GPU | Tres respuestas, incluso cero como valor permitido en Firmware |
| Consignas 34h/35h | No expuestas por getters explícitos encontrados |
| Guarda completa ECh | GM26 sólo entrega bit 2; no reemplaza el byte completo |
| FanSwitch F4h | Sin getter explícito encontrado |
| GM27 y restauración | No ejecutados; no se adquiere control con setters |
| Manual/Automatic de producción | No promovido ni validado por este ensayo |

El diseño de GM27 sigue pendiente de recuperación independiente, manejo de llamadas bloqueadas y criterios para verificar el retorno al estado inicial. No se incorpora un setter cuya recuperación dependa únicamente de finally o de matar el proceso. Si falta cobertura ACPI para consignas y guardas, la siguiente decisión es de arquitectura; FFFS=0 no se declarará suficiente para sustituirlas.

## Ejecución física

Dejar VFC en Firmware y cerrarlo, con sus servicios/watchdogs detenidos mediante su ciclo existente y sin leases pendientes. Cerrar herramientas de lectura de hardware. Mantener BIOS, alimentación y carga sin cambios durante la sesión. Las comprobaciones de procesos/servicios son snapshots y no detectan todos los lectores de terceros.

En PowerShell como administrador dentro de la rama actualizada:

```powershell
git pull --ff-only
.\scripts\Test-Victus-AcpiCoverage.ps1
```

Enviar `diagnostics\ACPI-Coverage_*.zip` y su `.zip.sha256`. Incluye identidad, llamadas crudas, rondas válidas, cobertura, resumen, eventos detectados, error si lo hubo y manifiesto de hashes. Healthy=true sólo significa que esta sesión cumple sus contratos y observación; ProductionReady permanece false.

CI verifica sintaxis, secuencia exitosa y parada al primer fallo, respuestas diagnósticas y RPM inválidas/lentas, exclusión de setters, compatibilidad PowerShell 7/5.1, rechazo del equipo no objetivo antes de acceder a HP y hashes de evidencia. La ejecución física requiere el equipo del usuario.
