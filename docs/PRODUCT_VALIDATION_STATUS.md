# Estado consolidado del producto — 6 de octubre de 2026

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

No repetir estas pruebas básicas para declarar nuevamente sus subsistemas PASS.
Una modificación posterior requiere regresión de las rutas que cambió, con su
alcance específico; no invalida automáticamente los resultados anteriores.

## Pendientes del motor y GUI actuales

- Regresión física breve de la nueva aplicación de ajustes avanzados en vivo.
- Actualización CPU/GPU en vivo: propuesta pendiente de implementar y validar; la sesión actual exige desactivar los límites antes de reemplazar su configuración.
- Regresión de display/suspensión y salida con la integración Automatic actual.
- Promoción posterior de Automatic normal y decisiones de arranque/reentrada.
  No hay validación de uso desatendido ni habilitación de arranque automático.

El sensor de chasis queda descartado por decisión del usuario. El historial de
carga es una heurística; no representa una temperatura de chasis medida.

## Revisión prolongada preparada

`Start-ProductGui.ps1 -Mode AutomaticExtendedReview` abre una entrada explícita
para el destino exacto, con **45 minutos por activación** y niveles **10–50**.
`AutomaticReview` conserva cinco minutos. `Open` e inicio con Windows siguen en
Firmware con Automatic normal cerrado. La duración no se guarda en preferencias.

Ambas entradas comparten los mismos límites, preparación CPU/GPU, fuente real,
telemetría, protección térmica, cancelación, liberación y bloqueo tras lifecycle.
CPU >90 y <99 °C dispone de 2000 ms tras establecimiento; CPU ≥99 °C vuelve
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

Los pasos 1–3 siguientes conservan el procedimiento histórico. La sesión `20261006T192934603Z-bb911eb9b49a4941b33a4829eba25943` ya aprueba carga y enfriamiento en AC con CPU 25/40 W y GPU 1996 MHz. No repetirlos para aprobar otra vez la misma función. Los pasos 4–5 y la regresión de ajustes nuevos siguen pendientes.

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
