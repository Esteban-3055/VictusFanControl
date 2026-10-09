# Estado consolidado del producto — v1.0.0, 9 de octubre de 2026

Destino: **HP 8C40 / 9D0R1LA / BIOS F.18**, i7-13700H y RTX 4060 Laptop.
v1.0.0 habilita Automático habitual, la nueva curva AC y el inicio con Windows
solicitados por el usuario. [Guía vigente](PRODUCT_V1.md) y
[notas de versión](../release/RELEASE-NOTES-v1.0.0.md).

| Área | Estado vigente |
|---|---|
| GUI y paquete | Build Windows, fixtures del paquete, recuperación y renders verificados; soak de 924 renders/30 aperturas sin incremento GDI/USER |
| Motor y replay | Verificaciones Windows/Linux; reconciliación independiente de 84 ejecuciones/323876 filas; replay histórico preservado |
| Nueva curva AC | Predeterminada; replay de 390 decisiones: demanda media 20.8001 frente a 21.5807, 16 cambios frente a 18; temperaturas observadas iguales como entrada, no resultado contrafactual |
| TZ01/DTT3 | Opcional, experimental y desactivado de fábrica; AC, +2 niveles crudos/60 s; no aprobación acústica ni de batería |
| Automático habitual | Autorizado solo para el destino exacto; sin vencimiento 5/45 min; mismas protecciones y preparación CPU/GPU |
| Inicio con Windows | Instalador/tarea y activación con tres lecturas distintas comprobados por fixtures sin hardware; falta observar el logon real de esta compilación |
| Preferencias | Migra solo antiguo preset AC intacto; conserva curvas personalizadas, límites CPU/GPU y batería; respaldo al guardar |
| Recuperación | Preflight conserva journals pendientes; recuperación explícita con sesiones exactas; aceptación NVML sin readback exacto del rango |

La nueva entrega **no acredita un PASS físico completo ni menos ruido medido**.
Se conservan cinco observaciones en `release/product-v1.json` (distribuido como
`PRODUCT-RELEASE.json`): uso representativo del contrato CPU 95 °C, suspensión sin
reentrada, salida/reinicio limpios, comparación térmica/acústica y logon real.
El control se interrumpe ante lifecycle o pérdida de datos y no se rearma solo.
Las evidencias anteriores no se repiten ni se convierten en pruebas de la nueva
compilación por actualizar la etiqueta de versión.

El replay de curva/retención utiliza las mismas temperaturas observadas con
acknowledgement simulado: menor demanda no prueba enfriamiento equivalente.
WMI no acredita propiedad independiente; NVML no acredita rango GPU exacto.
La [evaluación TZ01/DTT3](PRODUCT_PLATFORM_RETENTION_2026-10-09.md) detalla su alcance.

## Registro histórico consolidado — 6 de octubre de 2026

Se conserva a continuación el estado anterior, sus pruebas y procedimientos.
Las frases sobre Automático normal cerrado o arranque no habilitado se refieren
a esas candidatas; para v1.0.0 rige la tabla y guía anteriores.

Actualización de estabilidad: [estudio 8C40](FAN_STABILITY_STUDY_8C40.md). Los presets actuales incorporan meseta fría, histéresis y espera térmica; las mediciones previas y tablas históricas no certifican este ajuste físico.

Destino actual: HP 8C40, SKU 9D0R1LA, BIOS F.18, i7-13700H y RTX 4060 Laptop.
Las pruebas del equipo histórico 88F8 no sustituyen la evidencia de este destino.

## Pruebas físicas aprobadas: conservar sus resultados

| Función | Evidencia | Alcance aprobado |
|---|---|---|
| Manual, salida desde bandeja y restauración de fans | P15D1/P15D2, `P15_TARGET_CHECKPOINT.md`; M4–M8 | Backend/GUI históricos, restauración, niveles variables, no retransmisión y salida; no cualificación automática del motor nuevo |
| CPU RAPL bajo carga y restauración | `INTEL_RAPL_P1.md`; Guardian 6G | Escritura/readback y restauración; ensayo histórico 20/40 W |
| GPU y CPU/GPU AC → Batería → AC | Guardian 6F/6H; informes `release/performance-guardian-6h-*.json` | Peticiones GPU aceptadas, CPU restaurada a 45/115 W, liberación y journals ausentes; NVML no verifica rango exacto |
| Modern Standby, pantalla Off/On y recuperación CPU/GPU | `release/performance-guardian-6i-standby-qualification-pass-8c40-2026-10-04.json` | Guardian de rendimiento, release antes de standby y reacquisición tras pantalla On |
| Muerte del proceso propietario | `release/performance-guardian-6j-parent-death-qualification-pass-8c40-2026-10-04.json` | Guardian vivo libera ambos dominios; no cubre destrucción del propio Guardian |
| Curvas aplicadas en vivo | Diagnóstico `20261006T163959090Z-28cde682f9524671b1511d131eebce2d` | Cinco Apply en una activación; CPU 92 → 48 °C en 1.041 s, sin renovar el plazo |
| Automatic continuo AC → Batería → AC en la GUI actual | `release/product-gui-source-transition-8c40-2026-10-06.json` | HEAD f669831; 203 muestras completas, CPU control máximo 77 °C/GPU 39 °C; misma activación, sin bloqueo ni interrupción |
| Carga prolongada y enfriamiento con el motor actual | `release/product-gui-sustained-load-8c40-2026-10-06.json` | HEAD fbed6fd; 2083 muestras completas, una activación sin interrupciones, 1815.785 s de carga acumulada; descenso prolongado y retorno a breve tras reposo |
| Aplicación en vivo desde Avanzado | `release/product-gui-live-tuning-8c40-2026-10-06.json` | HEAD 0db58a2; filtro 7 → 8 s en la misma activación, 123 muestras completas, sin interrupciones ni renovación del plazo; CPU/GPU activos |
| Límites CPU/GPU aplicados durante Automatic en AC | `release/product-gui-live-performance-8c40-2026-10-06.json` | HEAD 34388b8; CPU 25/40 → 25/38 W y GPU 1800 → 1750 MHz en sus activaciones originales, sin interrupciones ni renovación; GPU aceptada por NVML, sin lectura independiente del rango |

No repetir estas pruebas básicas para declarar nuevamente sus subsistemas PASS.
Una modificación posterior requiere regresión de las rutas que cambió, con su
alcance específico; no invalida automáticamente los resultados anteriores.

## Pendientes del motor y GUI actuales

- Influencias ampliadas: temperatura 100–200 %, potencia/carga 0–200 %; validación, persistencia, simulador y editor comparten el rango. Defaults y protecciones conservados; efectos acústicos de valores altos dependen del perfil elegido.
- Regresión de display/suspensión y salida con la integración Automatic actual.
- Regresión breve del contrato térmico de la candidata: inicio ≤90 °C, confirmación activa desde 95 °C y retorno inmediato desde 99 °C. La evidencia anterior no aprueba físicamente este cambio.
- Promoción posterior de Automatic normal y decisiones de arranque/reentrada.
  No hay validación de uso desatendido ni habilitación de arranque automático.

El sensor de chasis queda descartado por decisión del usuario. El historial de
carga es una heurística; no representa una temperatura de chasis medida.

## Revisión prolongada preparada

La candidata actual se describe en `PRODUCT_FINAL_CANDIDATE.md`. El último
diagnóstico bf445702 contiene 1421 muestras completas, cuatro curvas, tres
ajustes y tres límites aplicados en la misma activación. A las 19:31:23 Santiago
interrumpió por la confirmación adicional de 90 °C, con CPU observada 92/93/93 °C
y nivel solicitado 50. Se conserva en
`release/product-gui-thermal-interruption-8c40-2026-10-06.json`; no es un PASS
de estabilidad ni de la nueva regla activa de 95 °C.

`Start-ProductGui.ps1 -Mode AutomaticExtendedReview` abre una entrada explícita
para el destino exacto, con **45 minutos por activación** y niveles **10–50**.
`AutomaticReview` conserva cinco minutos. `Open` e inicio con Windows siguen en
Firmware con Automatic normal cerrado. La duración no se guarda en preferencias.

Ambas entradas comparten los mismos límites, preparación CPU/GPU, fuente real,
telemetría, protección térmica, cancelación, liberación y bloqueo tras lifecycle.
CPU ≥95 y <99 °C dispone de 2000 ms tras establecimiento; CPU ≥99 °C vuelve
inmediatamente. GPU >82 °C, CPU >60 W, GPU >75 W o datos inválidos interrumpen.
El paso de fuente sigue limitado a 4 s y no renueva el plazo ni la inercia.
Aplicar una curva y repetir Automatic tampoco renuevan el reloj.
Al expirar se solicita Firmware; los límites mantienen su sesión independiente
hasta Liberar CPU/GPU o Salir. Una llamada nativa iniciada no se cancela por el reloj.

En Control se muestra carga acumulada y tipo de descenso. El diagnóstico incluye
`ObservedLoadSeconds`, `SustainedLoadCooling` y `AutomaticReviewMaximumSeconds`.
Esta entrada conserva segmentos de 8 MiB más uno anterior y exporta hasta **16 MiB
por flujo**, únicamente de la apertura actual. Sigue siendo una cola acotada:
exportar en los hitos evita perder evidencia si hay muchas activaciones o logs.
Los modos habituales conservan segmentos de 5 MiB y exportación de 2 MiB por flujo.

## Regresión agrupada preparada: duración y enfriamiento ya observados

Los pasos 1–3 siguientes conservan el procedimiento histórico. La sesión `20261006T192934603Z-bb911eb9b49a4941b33a4829eba25943` ya aprueba carga y enfriamiento en AC con CPU 25/40 W y GPU 1996 MHz. No repetirlos para aprobar otra vez la misma función. La regresión breve de Avanzado también está aprobada. Los pasos 4–5 siguen pendientes.

1. Salir normalmente de la GUI anterior. Extraer un paquete nuevo, Verify, abrir
   con `-Mode AutomaticExtendedReview`. Mantener AC y los valores ya usados:
   CPU 30/50 W y GPU 210–1800 MHz; Batería 9/12 W y GPU 210–1200 MHz.
   Esperar Healthy, seleccionar Automatic una vez y exportar el inicio.
2. Usar la carga habitual que interesa evaluar, sin añadir un nuevo stress térmico.
   Mantener la misma activación hasta ver ≥20 min de carga acumulada y descenso
   de carga prolongada. Exportar ese hito. El tiempo de reloj no sustituye carga:
   cuenta CPU/GPU ≥50 %, CPU ≥25 W o GPU ≥40 W; pausas largas antes de calificar
   pueden borrar el acumulado. No acortar artificialmente el umbral para pasar.
3. Terminar la carga y observar 3–5 minutos de enfriamiento; exportar. El descenso
   prolongado se desactiva tras 120 s de reposo observado; las temperaturas aún
   elevadas siguen demandando ventilación. Si aparece interrupción, exportar de
   inmediato y conservar el motivo; no forzar reentrada ni asumir un PASS.
4. Con carga ya terminada, suspender desde Windows y reanudar. Exportar el estado:
   ventiladores permanecen Firmware/bloqueados tras lifecycle; no deben rearmarse
   solos. Separar el comportamiento del Guardian de la autoridad de ventiladores.
5. Salir desde bandeja, abrir normalmente y exportar: Firmware y ausencia de una
   nueva aplicación de límites por el arranque. Conservar cualquier journal o
   fallo de release; no borrar evidencia ni utilizar Recovery sin IDs exactos.

Después de esta regresión se podrá decidir la promoción del uso continuo normal.
El arranque/reentrada automática requiere un contrato propio; no se activa como
consecuencia de aprobar solo la duración de la prueba.

## Evaluación del diagnóstico de las 17:15

Una activación entre 16:39:31 y 17:15:12 (hora de Chile), CPU 25/40 W,
GPU 1996 MHz. Se acumularon 30 min 15.785 s de carga observada.
El descenso prolongado comenzó a las 16:59:42; después de retirar la carga,
los pasos de bajada confirmaban unos 16.7 s. A las 17:11:57 se completó el
reposo observado y las confirmaciones pasaron a unos 4.2 s. Los intervalos
reales entre comandos también incluyen adquisición y respuesta WMI.

CPU control máximo 97 °C, GPU 78 °C. Seis picos CPU recuperados dentro del
plazo; cero interrupciones. Al exportar: Healthy, Automatic, CPU 44 °C,
GPU 37 °C, RPM informadas 1200/1200. El nivel solicitado final era 13;
la lectura cuantizada de RPM era nivel 12: no hay readback del setpoint ni
prueba de propiedad independiente. La aceptación WMI no se presenta como
confirmación física exacta. CPU pico 43.28 W con PL2 40 W tampoco constituye,
por sí solo, prueba de fallo del limitador; falta una ventana de potencia y
readback contemporáneo para evaluar una sobrescritura externa.

El registro aprueba comportamiento observado con esa configuración y el motor
anterior a la nueva aplicación de ajustes. No certifica la GUI modificada, la
expiración de los 45 minutos, la suspensión actual ni el uso desatendido.

## Evaluación del diagnóstico de las 17:52

Paquete 0db58a2, 123 muestras completas y 46 decisiones. Activación única
`12944dd91d9948d09628f2bd5c02010f`: empezó con filtro de subida de 7 s y a las
17:51:17.745 se aplicaron 8 s desde Avanzado. El registro conserva el mismo ID,
2696 s restantes al aplicar y 2633 s al exportar; no se renovó el reloj de 45 min.
La configuración final confirma 8 s. CPU/GPU conservan 25/40 W y 1996 MHz en AC,
CPU Active y GPU ActiveUnverified. No hubo interrupciones ni recuperación después
de la validación inicial. CPU máximo 68 °C y GPU 37 °C, en actividad ligera.

Aprueba exclusivamente esta actualización de respuesta en vivo. No prueba todos
los parámetros, el cambio de definición de carga, el rango NVIDIA exacto ni
propiedad independiente de ventiladores. El cierre hacia versión final se detalla
en PRODUCT_FINAL_RELEASE_PLAN.md.
