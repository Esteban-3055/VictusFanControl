# Atlas técnico del HP Victus 15-fa1013la (HP 8C40 / BIOS F.18)

**Fecha de consolidación:** 12 de octubre de 2026. **Idioma:** español · [English summary](HP_8C40_RESEARCH_ATLAS.md).

> Este documento es un mapa de conocimiento y evidencia, no una especificación oficial de HP, una guía para escribir registros EC ni una certificación universal de la familia Victus. Cada resultado queda limitado a la placa, firmware, instrumentación, sesiones y revisiones indicadas. Un resultado en CI no equivale a una prueba física.

## 1. Identidad física y alcance

| Propiedad | Unidad investigada |
|---|---|
| Identificación comercial | HP Victus 15-fa1013la / Victus 15-fa1xxx |
| System SKU | 9D0R1LA (variante documentada: 9D0R1LA#AKH) |
| Placa | HP 8C40, revisión 63.43 |
| BIOS caracterizada | F.18 |
| Procesador | Intel Core i7-13700H, 6 P-cores + 8 E-cores físicos |
| Gráficos | NVIDIA GeForce RTX 4060 Laptop GPU |
| Suspensión | Modern Standby: S0 Low Power Idle, no S3 heredado |
| Target del controlador | HP-8C40-9D0R1LA-F18 |

El Victus 16-d0515la (placa 88F8, i7-11800H y RTX 3060) es **una plataforma histórica distinta**. Las coincidencias en métodos HP, bytes EC, ventiladores o nombre comercial no autorizan aplicar el perfil 8C40 a otra unidad. Cambiar BIOS, placa, SKU, GPU o modelo de suspensión exige revalidación.

**Fuentes:** [identidad y cualificación 8C40](HARDWARE_8C40.md), [Modern Standby](MODERN_STANDBY_8C40_M0.md) y [estado de validación del producto](PRODUCT_VALIDATION_STATUS.md).

## 2. Cómo interpretar la evidencia

| Etiqueta | Qué demuestra | Qué no demuestra |
|---|---|---|
| **Observado físicamente** | Una magnitud o transición medida bajo una sesión y hardware identificados | Comportamiento idéntico en todos los HP Victus |
| **Caracterizado** | Reproducción de un patrón con muestras, contexto y límites | Causalidad completa o algoritmo interno exacto |
| **Trazado en AML/DSDT** | Existencia de campo/método y transformación estática en BIOS F.18 | Significado térmico completo de cada campo |
| **CI / fixture / replay PASS** | Que un algoritmo, contrato o evaluador pasa sus pruebas | Efecto térmico, acústico o control seguro en hardware |
| **Hipótesis de firmware** | Modelo funcional útil para nuevos experimentos | Código de HP reconstruido o curvas OEM certificadas |
| **NO-GO** | Evidencia insuficiente para usar un modelo como controlador | Inutilidad de sus sensores o del experimento |

Jerarquía aplicada: logs físicos y procedencia por SHA > evaluación estadística reproducible > trazado DSDT del mismo target > documentación histórica > analogía con otros modelos. Las fases antiguas que dicen «Automatic cerrado» describen su momento; **no invalidan la publicación posterior v1.0–v1.2.3**.

La rama por defecto `main` conserva un snapshot v1.0.0; la [release estable más reciente en esta consolidación es v1.2.3](https://github.com/Esteban-3055/VictusFanControl/releases/tag/v1.2.3), publicada desde otro commit. Para reproducir una prueba, fijar **tag/commit y datos de esa prueba**; no mezclar los estados de ambas ramas.

## 3. Ventiladores: interfaz HP WMI y límites de observabilidad

La ruta de producto actual es **HP BIOS/WMI**, no escritura/lectura EC directa. Para la placa 8C40 se ha investigado el método HP 20008h, con operaciones:

| Operación | Identificador | Interpretación |
|---|---|---|
| SetFanMode | `0x1A` | Modo de control; `LegacyDefault` usado al devolver autoridad |
| GetFanLevel | `0x2D` | Nivel efectivo reportado CPU/GPU, **no tacómetro físico exacto** |
| SetFanLevel | `0x2E` | Solicitud acotada de nivel CPU/GPU |
| Liberación | `FF/FF` | Secuencia de restauración calificada, junto con `LegacyDefault` |

**Corrección de terminología:** algunas notas históricas llaman «RPM» a `GetFanLevel * 100`. Es una representación nominal/resolución de nivel, no una medición independiente del rotor. Un nivel mayor que cero tampoco prueba por sí solo que el ventilador esté girando. Lecturas de tacómetros EC B0–B3 fueron herramientas de laboratorio de etapas anteriores, **no** la telemetría normal del producto 8C40.

Las primeras pruebas físicas bajo harness acotado recorrieron niveles iguales 10..50; se observaron aproximadamente 1028/1013 RPM para 10/10 y 4991/4991 RPM para 50/50 mediante la ruta de tacómetro de investigación. Esos datos **no** convierten una respuesta WMI futura en una confirmación física exacta.

El controlador propio solicita el mismo nivel para CPU/GPU en su política de ventilación coordinada; **mismo mando no implica RPM idénticas**. Tampoco debe confundirse esta política con los **dos niveles OEM diferentes** que revela 0x2D cuando HP gobierna los ventiladores.

**Fuentes:** [estudio WMI y niveles](HP_8C40_WMI_FAN_TELEMETRY.md), [demostración WMI](HP_8C40_WMI_CONTROL_PROOF.md), [hardware e históricos de validación](HARDWARE_8C40.md), [producto y límites](PRODUCT_VALIDATION_STATUS.md).

## 4. Hallazgos ACPI / Intel DTT / EC (solo investigación)

### 4.1 Señales documentadas

| Señal | Evidencia e interpretación en F.18 | Límite |
|---|---|---|
| TZ01 (`MSAcpi_ThermalZoneTemperature`) | Zona ACPI activa trazada estáticamente a `EC0.RTMP`, offset `0x58` | Cache/cadencia ACPI; no valor instantáneo garantizado |
| DTT1 / participante A71D _1 | Similar a CPU Package; fuerte correlación observada | No es identificación física universal |
| DTT2 / participante A71D _2 | Dominio CPU/core cercano al núcleo más caliente | No sustituye DTS crudo de seguridad |
| DTT3 / participante A71D _3 | Dominio GPU/plataforma filtrado o virtual, responde a GPU-only con retardo | Umbrales AC1/AC0 no son por sí solos mandos de ventilador |
| GPTM (EC `0xB7`) | Señal de firmware que sigue muy estrechamente a GPU | No usar como acceso EC productivo |
| TNT1 (EC `0x59`) | Señal lenta de plataforma, influida por GPU | Inferencia dinámica |
| CPUT (`0x57`) / TNT4 (`0x49`) | Coincidieron en 528/529 barridos limpios; señal lenta | No son CPU Package |
| GPUT (EC `0x89`) | Casi siempre valor 4 en esta unidad | **No representa** la temperatura de GPU aquí |

**TZ01/RTMP:** el DSDT del target contiene la conversión aproximada `_TMP = 2732 + 10 * RTMP`, en décimas Kelvin. Si RTMP=70, Windows puede mostrar **70,05 °C**. El decimal .05 no implica una resolución real de 0,05 °C. Se observaron cambios de época alrededor de un segundo y valores que podían permanecer sin cambio del orden de 16 s.

**DTT3:** se observaron `AC1=55 °C` y `AC0=67 °C` en los datos de DTT/IPF. Bajo GPU-only, DTT3 pasó de mediana ~39 a ~64 °C y respondió después del dominio GPU. No se han demostrado como umbrales exactos de activación de los ventiladores ni límites universales.

### 4.2 Por qué el EC directo no es la ruta de producción

Un estudio de 786 barridos EC tuvo solo **529 barridos <=20 ms**; **241 tardaron >=250 ms** y el máximo superó 7 s. Los barridos lentos concentraron lecturas aparentemente válidas pero incoherentes. Se observó además un evento transitorio Windows Kernel-Power 524 con porcentaje de batería reportado como 0 y AC conectado; **no está demostrada la causalidad directa** entre ambos fenómenos.

El acceso EC por puertos `0x66/0x62` podría competir con `acpi.sys` y no existe un mecanismo público fiable para tomar el lock interno de Windows. Por ello **no utilizar lectura ni escritura EC directa en el bucle productivo** ni proponer que una dirección descubierta está disponible para escritura. La investigación debe usar superficies admitidas: Intel MSR/PawnIO, NVIDIA NVML, ACPI TZ01, Intel DTT/IPF y HP WMI.

**Fuentes:** [documentación del estudio WMI/EC](WMI_ONLY_EC_ISOLATION.md), [investigación residual EC](SCENARIO_C_RESIDUAL_EC.md), [política TZ01/DTT3](PLATFORM_THERMAL_POLICY_2026-10-08.md), [modelo OEM](OEM_SHADOW_MODEL_8C40.md). La consolidación documental v4.0 del 07-10-2026 fue también una fuente de esta síntesis.

## 5. Política térmica OEM: mesetas, memoria e histéresis

En las capturas **v10** (6049 snapshots, 6003 completos) y **v11** se dejó al firmware HP gestionar los ventiladores y se observaron niveles mediante WMI. Aparecieron mesetas reconocibles:

| Estado observado inicialmente | CPU level | GPU level | Descripción |
|---|---:|---:|---|
| A | 21–22 | 19–20 | Frío / mínimo observado |
| B | 25–26 | 23–24 | Moderado |
| C | 33–34 | 28–29 | Alto sostenido |
| D | 39–40 | 35–36 | Máximo observado en v10 |

Las rampas entre estados pueden durar decenas de segundos. Después de terminar una carga, potencia y uso caen antes que el nivel de ventilación. Esto sugiere memoria térmica, permanencia mínima (`dwell`), filtrado e histéresis; **no demuestra el algoritmo interno exacto de HP**.

La v11 registró A→B alrededor de **14:14:21 UTC** y B→A alrededor de **14:18:42 UTC** (usar épocas `fan_sampled_at_utc`, no la notificación retrasada). Un pico breve de CPU cerca de 97 °C no bastó para salir de A; una condición sostenida sí. B→A ocurrió aproximadamente con CPU 41 °C, GPU 35 °C, DTT3 39 °C y TZ01 42 °C. TZ01 pudo volver a subir sin una transición inmediata de A a B: evidencia de histéresis.

**Corrección posterior importante:** una captura nueva encontró pares cercanos a **46–47/40–41** durante varios minutos, fuera de las cuatro mesetas iniciales. No se puede llamarlos automáticamente «rampa» ni afirmar que A/B/C/D sean el inventario completo del firmware. El clasificador se corrigió para registrar **Unmapped** y candidatos estables no asignados, sin promoverlos a estados calificados.

**Fuentes:** [clasificación corregida 08-10](OEM_SHADOW_CLASSIFICATION_2026-10-08.md), [reconstrucción/replay OEM](OEM_SHADOW_MODEL_8C40.md) y [comparación causal de modelos](OEM_SHADOW_MODEL_COMPARISON_2026-10-08.md).

## 6. Qué mostró el OEM Shadow y por qué sigue NO-GO

El predictor OEM experimental es **solo lectura / replay**, separado de la política propia de la aplicación. Nunca debe usar las etiquetas de ventiladores observadas como entrada para predecir esos mismos niveles.

- Modelo semilla sobre v10: **24,24 %** de acierto en estados conocidos; el conjunto v11 alcanzó **86,77 %**, pero no cubría C/D y las transiciones estimadas llegaron con decenas de segundos de retraso.
- En v10 el modelo original perdió cinco transiciones observadas. La falta de fuentes frescas explica parte del problema, **no** todo.
- La evaluación posterior reservó **sesiones completas** para evitar contaminación de entrenamiento y validación. Añadir historial causal mejora frente a temperatura instantánea; DTT1/DTT2 no aportaron mejora constante, y el régimen alto siguió insuficientemente explicado.
- El estado **NO-GO** aplica a convertir el modelo OEM experimental en un controlador físico, no a utilizar TZ01 o DTT3 como instrumentos de investigación.

El controlador de VFC utiliza una **política propia**: seis demandas (temperatura, potencia y carga de CPU/GPU), máximo de demandas, filtro/inercia y protección de temperaturas crudas. Su suplemento TZ01/DTT3, cuando procede, es una retención **experimental y acotada**, no una reconstrucción del algoritmo OEM ni una prueba acústica.

**Fuentes:** [replay semilla](OEM_SHADOW_MODEL_8C40.md), [corrección de clases](OEM_SHADOW_CLASSIFICATION_2026-10-08.md), [comparación de modelos](OEM_SHADOW_MODEL_COMPARISON_2026-10-08.md), [retención experimental](PRODUCT_PLATFORM_RETENTION_2026-10-09.md).

## 7. CPU y GPU: potencia, clock y recuperación

- **CPU:** en este equipo `MSR_PKG_POWER_LIMIT (0x610)` era accesible sin lock en la sesión de estudio; baseline observado `PL1=45 W, PL2=115 W`. Una prueba bajo carga aproximadamente constante (~100 %) mostró fase cercana a **39,91 W** y después **19,29 W** al aplicar 20/40 W, con restauración comprobada al baseline. Esto demuestra efecto en **esta unidad/sesión**, no un baseline universal.
- **GPU:** el proyecto prioriza límites de gráficos por NVIDIA NVML (no `nvidia-smi` repetitivo ni GPU power limit). Se cualificaron sesiones AC **210..1850 MHz** y batería **210..1200 MHz**, con un solo Set al cambiar presets.
- **Observabilidad GPU:** NVML público no proporcionó un getter documentado del rango arbitrario de lock efectivamente instalado. Un Set aceptado no significa ownership exacto: el estado apropiado es `ActiveUnverified`.
- **Guardian de rendimiento:** proceso separado, mutex de escritor único, sesión identificada, journal durable antes de escrituras, liberación y recuperación distintas para CPU y GPU. El recovery CPU por readback de campos propios no se traslada automáticamente a GPU.
- **Hallazgo térmico anterior:** un bloqueo manual de clock cerca de 1800 MHz coincidió con descenso preliminar del hotspot GPU de ~105 a ~92 °C. No demuestra por sí solo que la disipación física sea correcta ni diagnostica una avería de contacto; procede comprobar condiciones mecánicas si la diferencia core/hotspot vuelve a ser excesiva.

**Fuentes:** [RAPL P1](INTEL_RAPL_P1.md), [fundación P2](INTEL_RAPL_P2_FOUNDATION.md), [Guardian P2B](INTEL_RAPL_P2B_GUARDIAN.md), [integración GPU+CPU](FAN_PERFORMANCE_GUI_INTEGRATION.md), [retención y regulación](PRODUCT_PLATFORM_RETENTION_2026-10-09.md).

## 8. Riesgos y limitaciones que deben aparecer al citar el proyecto

1. **Control ≠ telemetría ≠ efecto físico.** Aceptación WMI no prueba propiedad independiente del firmware; un nivel WMI no es lectura precisa de RPM; un lock NVML aceptado no es lectura independiente del rango.
2. **Histórico ≠ vigente.** ``Automatic=false` de etapas P16/P17 y referencias a EC son estados de investigación antiguos; la release 1.2.3 y su propio estado de validación son posteriores.
3. **Replay ≠ reducción acústica.** Una menor demanda simulada no demuestra menos dBA ni enfriamiento equivalente bajo la misma carga.
4. **Identidad estricta.** No ampliar el alcance a otros Victus o BIOS por compartir apariencia o componentes.
5. **Seguridad del controlador.** Mantener restauración a firmware ante datos caducados, emergencias, fallos de lifecycle y pérdida de autoridad; las validaciones de ventiladores y de límites de rendimiento tienen dominios separados.
6. **Publicación responsable.** No subir logs brutos sin eliminar datos personales, rutas de usuario, IDs del equipo, información de la batería y otras trazas identificables; conservar hashes/procedencia y nombres de experimentos sin exponer información privada.

## 9. Guía para continuar y reproducir

| Objetivo | Fuente principal |
|---|---|
| Identidad 8C40, barrido 10..50, telemetría histórica | [HARDWARE_8C40.md](HARDWARE_8C40.md) |
| WMI-only y diferencias EC/WMI | [WMI_ONLY_EC_ISOLATION.md](WMI_ONLY_EC_ISOLATION.md) |
| Modelo de estados, replay inicial, NO-GO | [OEM_SHADOW_MODEL_8C40.md](OEM_SHADOW_MODEL_8C40.md) |
| Clasificación Unmapped y captura de una hora | [OEM_SHADOW_CLASSIFICATION_2026-10-08.md](OEM_SHADOW_CLASSIFICATION_2026-10-08.md) |
| Modelos históricos con sesiones separadas | [OEM_SHADOW_MODEL_COMPARISON_2026-10-08.md](OEM_SHADOW_MODEL_COMPARISON_2026-10-08.md) |
| Política experimental TZ01/DTT3 y 84 replays | [PLATFORM_THERMAL_POLICY_2026-10-08.md](PLATFORM_THERMAL_POLICY_2026-10-08.md) |
| Interrupción con carga CPU/RAM y corrección de muestras auxiliares | [PLATFORM_CPU_RAM_INTERRUPTION_2026-10-08.md](PLATFORM_CPU_RAM_INTERRUPTION_2026-10-08.md) |
| Recuperación / trazabilidad de producto | [PRODUCT_VALIDATION_STATUS.md](PRODUCT_VALIDATION_STATUS.md) |
| Operación de la release más reciente | [README de v1.2.3](https://github.com/Esteban-3055/VictusFanControl/blob/v1.2.3/README.es.md) |

Para una nueva hipótesis, registrar: target exacto, BIOS, commit, fuente de cada sensor, época de **adquisición** frente a época de consulta, carga, condiciones iniciales, número de muestras realmente independientes, umbrales de aborto, hashes de los datos y diferencias entre predicción y observación. Los tests deben ser **read-only de fábrica** salvo una cualificación física explícita, acotada y supervisada.

**Plantilla y buenas prácticas para publicar resultados:** [protocolo de reproducibilidad e issues](RESEARCH_REPORTING_GUIDE.es.md).

**Pendientes de investigación:** describir el régimen alto Unmapped con sesiones independientes; correlacionar señales térmicas sin atribuirles causalidad automática; cualificar acústica/temperatura/lifecycle en la versión distribuida exacta; y mejorar la visibilidad de versión entre `main` y las releases etiquetadas. Los issues técnicos mantienen el seguimiento; no son autorización para escrituras EC o pruebas destructivas.
