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

## Resultado físico recibido: 3 de octubre

Captura `ACPI-Coverage_20261003_195540_9cb7be.zip`, SHA256 `3306a449499c4447f28e9a941d6db67c84cc9a343483f4d0f5d5c79ec1c6e180`. Los seis miembros del manifiesto coinciden en tamaño y hash. Identidad: Victus 15-fa1013la, 8C40/63.43, F.18, SKU 9D0R1LA#AKH.

Entre 22:55:40.663894 y 22:56:20.2853855 UTC se completaron las 13 solicitudes y tres rondas, sin reintentos ni eventos ACPI 13/15 observados. Control GBIF: rc=5. Los seis diagnósticos ECOK: rc=6, duración nativa 157.966–170.4125 ms, compatible con el Sleep(150) del AML. FFFS=0 en las tres rondas. RPM nominales CPU/GPU: 2600/2300, 2600/2300 y 2600/2400; llamadas nativas 359.95–368.96 ms.

El resultado respalda la ejecución de la rama condicional de lectura bajo las hipótesis de AML/proveedor descritas arriba. No verifica transiciones, consignas, guarda completa, F4, restauración ni estabilidad prolongada. ProductionReady continúa false.

## Siguiente lectura: GM11 opcional

`-FanStatus` agrega dos solicitudes por ronda; máximo 19 consultas. Usa exclusivamente Command=20008h, type=11h, hpqBIOSInt4 y payload 00-00-00-00 (CPU) o 01-00-00-00 (GPU). Requiere rc=0 y exactamente cuatro bytes; conserva todos los valores como hex sin asignarles semántica de control. La ruta base sin el switch sigue realizando 13 solicitudes.

En el DSDT suministrado, GM11 (128234–128264) devuelve FMR1/FSUS/FS1H/FS1L para selector cero y FMR2/FSUS/FS2H/FS2L para el otro selector. Esos campos pertenecen a H2RA, una región SystemMemory en FE400000h de tamaño 1000h (134480). Offsets: FSUS=1B7h, FMR1=527h, FMR2=52Fh, FS1H/L=530h/531h, FS2H/L=532h/533h. El cuerpo del getter no llama WSMI ni escribe los campos de ventilador. Se mantiene la infraestructura WMI compartida descrita arriba.

SystemMemory no demuestra que sea RAM ordinaria, una copia reciente del EC ni una instantánea atómica. Los nombres de campos no prueban unidades o significado. No hay equivalencia demostrada con SRP1/SRP2 en EC 34h/35h, el byte ECh o SFAN en F4h. Las vistas adicionales SMW0/SMB0/FLD0..3 del ERAM sólo abarcan desde 04h hasta 23h; no cubren esos registros pendientes. GM13/GM2F devuelven buffers constantes en el AML revisado y no resuelven esa cobertura.

Para esta sesión mantener Firmware, cerrar VFC y lectores, detener sus servicios/watchdogs y dejar alimentación/carga estables como en la prueba anterior. No instalar Omen Gaming Hub para esta lectura. Ejecutar como administrador en la rama actualizada:

```powershell
git pull --ff-only
.\scripts\Test-Victus-AcpiCoverage.ps1 -FanStatus
```

Enviar el ZIP y su SHA256. El progreso se muestra aproximadamente cada cinco segundos; eso no significa una consulta cada cinco segundos. Son tres rondas con seis consultas cada una, espera de un segundo tras cada respuesta y cinco segundos entre rondas; plazo del hijo de 120 s. CPU/GPU quedan en columnas `gm11_cpu_raw_hex` y `gm11_gpu_raw_hex` del CSV y en respuestas completas del JSONL. Semántica y frescura de GM11 permanecen sin calificar incluso si Healthy=true.

CI ejercita ambos modos, transporte simulado y parada al primer fallo de GM11, preservación de bytes FF/80 y selectores exactos, además del rechazo físico del equipo no objetivo. La interpretación del resultado y cualquier transición futura se decidirán con esa evidencia; este ensayo no habilita setters ni relaja guardas de producción.

## Resultado GM11 y sesión de correlación

La captura `ACPI-Coverage_20261003_201128_023bc6.zip` tiene SHA256 `9a3e7a51e858e5aae6d8754dcae55e73104ce9d9f31f73416200e4263c74bfd7`, coincidente con su sidecar y todos los miembros del manifiesto. Duración 50.1809144 s, 19 pares solicitud/respuesta en el orden previsto, tres rondas, Healthy=true, sin ACPI 13/15 observados. GM11: rc=0 en las seis respuestas, llamadas nativas 7.6887–33.6679 ms. Primeros bytes constantes 2F/3C. Últimos bytes CPU: 0A83/0A98/0A79; GPU: 0953/0951/095E.

El controlador primario de Linux `hp-wmi` identifica 11h como consulta de velocidad y combina bytes 2/3 como alto/bajo; para su ruta Victus S 2Dh multiplica el byte por 100. Fuente consultada: https://github.com/torvalds/linux/blob/master/drivers/platform/x86/hp/hp-wmi.c (funciones hp_wmi_get_fan_speed y hp_wmi_get_fan_speed_victus_s, consultado 2026-10-03). Esta implementación respalda la decodificación, pero no califica automáticamente el firmware 8C40. No interpreta los primeros bytes de GM11.

| Ronda | GM11 CPU candidato RPM | GM2D CPU nominal RPM | GM11 GPU candidato RPM | GM2D GPU nominal RPM |
|---|---:|---:|---:|---:|
| 1 | 2691 | 2700 | 2387 | 2400 |
| 2 | 2712 | 2600 | 2385 | 2300 |
| 3 | 2681 | 2600 | 2398 | 2300 |

La diferencia temporal entre respuestas GM2D y GM11 fue 1.597–2.164 s para CPU y 3.151–3.989 s para GPU. No se deben usar estas parejas como mediciones simultáneas ni inferir exactitud de 1 RPM de la resolución del formato.

### Ejecución ampliada

El switch `-Correlation` activa GM11 y fija 18 rondas: seis en reposo, seis con carga habitual moderada y seis durante recuperación. Máximo 109 solicitudes nativas secuenciales, plazo del hijo de 600 s, sin reintentos ni setters. Mantiene la parada ante primer fallo o nuevos ACPI 13/15. Espera de un segundo tras cada respuesta y cinco segundos entre rondas; duración esperada aproximadamente 5–6 minutos, variable por CIM/WMI. No se cambia la cadencia nativa para perseguir una muestra exacta cada cinco segundos.

Dejar el equipo en Firmware, cerrar VFC y sus servicios/watchdogs/lectores como antes. No variar alimentación, BIOS, límites de CPU/GPU ni modo de rendimiento. Empezar con el equipo en reposo. Desde PowerShell administrador, rama actualizada:

```powershell
git pull --ff-only
.\scripts\Test-Victus-AcpiCoverage.ps1 -Correlation
```

1. FASE 1/3 REPOSO: esperar sin iniciar carga.
2. Cuando aparezca FASE 2/3 CARGA: iniciar una aplicación habitual de carga moderada, por ejemplo un juego limitado a 30 FPS; mantener la misma escena. No abrir HWiNFO/LHM/otros lectores. No usar una prueba extrema para forzar RPM.
3. Cuando aparezca FASE 3/3 RECUPERACION: cerrar la aplicación de carga y dejar el equipo en reposo hasta terminar. El recolector no inicia ni detiene esa aplicación.
4. Enviar ZIP y SHA256 e indicar aplicación/escena utilizada, si ambos ventiladores cambiaron perceptiblemente y cualquier retraso al seguir los avisos. Si la prueba se detiene, cerrar también la carga y enviar la evidencia parcial.

El padre muestra progreso aproximadamente cada cinco segundos y registra los avisos de carga/recuperación en `phase-prompts.jsonl`. El CSV usa fases **planificadas**, no prueba que el usuario aplicó o retiró la carga en ese instante. Conserva bytes crudos, RPM candidatas calculadas y UTC independientes de cada respuesta para estudiar desfase. No mide temperatura ni carga, ni incorpora un corte térmico nuevo; se mantiene la protección normal del firmware. Si el equipo se comporta anormalmente o se calienta excesivamente, detener la carga y la prueba.

Criterios de análisis: integridad/orden/completitud de llamadas, latencias, cambios de GM11 en ambos ventiladores, concordancia de tendencia con GM2D y recuperación. Sin variación suficiente la sesión es inconclusa para seguimiento dinámico. Un salto, valor repetido o discrepancia se investigará con los tiempos y la cuantización antes de atribuirlo a fallo. Healthy sólo valida contratos/observación, no correlación, frescura, precisión, estabilidad prolongada ni control. No reemplaza consignas 34h/35h, ECh, F4h ni pruebas de restauración.

CI verifica las 18 rondas, seis etiquetas por fase, 109 solicitudes, 17 esperas entre rondas, decodificación y conservación de UTC/hex; un fallo en GM11 GPU de la ronda 7 detiene tras 43 solicitudes y conserva seis rondas completas. También verifica el modo corto existente y el rechazo del equipo no objetivo con la configuración de correlación.
