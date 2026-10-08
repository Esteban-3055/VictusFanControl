# Curva experimental física TZ01/DTT3 — HP 8C40 / F.18

Esta entrada implementa una prueba opt-in de la curva actual y su extensión de
retención térmica. No abre el gate normal de Automatic ni declara una versión
estable. La investigación previa y sus datos siguen en
`PLATFORM_THERMAL_POLICY_2026-10-08.md`.

## Qué controla y qué compara

La curva actual conserva MAX de sus seis demandas, la fuente CPU configurada,
una sola EMA final, confirmaciones, descenso adaptativo y protección por
CPU/GPU raw. La candidata añade, antes de esa EMA:

`MAX(demanda actual, MIN(MAX(demanda TZ01, demanda DTT3), último objetivo solicitado y aceptado))`.

El límite superior del suplemento es 44; la protección original puede pedir 50.
No promedia temperaturas ni convierte DTT3 en un sensor de ubicación física
conocida. Se usan las mismas curvas auxiliares y sensibilidades del estudio;
no se reajustan a partir del resultado de esta prueba.

En paralelo se registran: base, TZ01, DTT3, ambas, ambas-retención y las dos
sensibilidades de umbral. Solo base y ambas-retención se conectan a hardware.
La base paralela conserva su historia propia; la política física comparte una
historia de EMA/carga entre bloques. Al cambiar bloque se alinea su objetivo al
último request aceptado y se descartan las confirmaciones pendientes. Esa
historia compartida, y el calor acumulado, son efectos de arrastre que el análisis
ha de considerar: el bloque A posterior no es una simulación de un equipo frío.

## Protocolo de una ejecución: 43 minutos

| Minutos desde inicio | Control físico | Acción del operador |
|---|---|---|
| 0–2 | Firmware | Reposo, comprobación de telemetría y TZ01/DTT3 |
| 2–11 | A: curva actual | 2 min reposo, 4 min carga, 3 min enfriamiento |
| 11–20 | B: ambas-retención | Repetir exactamente la misma secuencia |
| 20–29 | B: ambas-retención | Repetir exactamente la misma secuencia |
| 29–38 | A: curva actual | Repetir exactamente la misma secuencia |
| 38–43 | Firmware; CPU/GPU liberados | Reposo/enfriamiento final |

La secuencia ABBA balancea la posición temporal, pero no elimina la deriva,
el arrastre térmico ni las variaciones de la carga. La herramienta **no genera
estrés automáticamente**. Elige una escena o benchmark reproducible; repite la
misma duración, resolución y ajustes. La ventana indica cuándo iniciar y cerrar
la carga. No cambies los perfiles durante el ensayo. Las etiquetas programadas
se contrastan con las cargas/potencias medidas; no prueban por sí solas que se
haya ejecutado el mismo trabajo.

## Preparación y ejecución

1. Descarga el artefacto **product-gui-review-<commit>** de la nueva CI Windows.
   Usa el paquete nuevo completo; los paquetes anteriores no tienen esta entrada.
2. En el GUI normal, guarda tus perfiles AC/Batería. La prueba lee y congela esos
   ajustes sin modificar el archivo de preferencias. Ambos límites CPU/GPU deben
   estar habilitados, porque se reutiliza el contrato físico de revisión existente.
3. Cierra normalmente otros procesos Victus y otros controladores de reloj GPU.
   Mantén AC conectado y la pantalla encendida. Un apagado de pantalla/suspensión
   interrumpe permanentemente esta ejecución; no se reanuda el control después.
   Durante la prueba la ventana solicita mantener sistema/pantalla activos; no
   cambia tus opciones de energía y libera la solicitud al terminar. Una
   suspensión o apagado de pantalla explícitos siguen interrumpiendo el ensayo.
4. Desde PowerShell elevado en la carpeta extraída:

```powershell
Set-ExecutionPolicy -Scope Process -ExecutionPolicy Bypass -Force
.\Start-PlatformThermalTest.ps1 -Mode SelfTest
.\Start-PlatformThermalTest.ps1 -Mode Run
```

`SelfTest` no hace IO de hardware. `Run` abre una ventana separada y empieza solo
al pulsar **Iniciar prueba física (43 min)**. Al concluir, cierra la ventana: el
launcher crea en el escritorio un ZIP con la evidencia. También lo conserva si
la prueba falla; no necesita Python instalado en el equipo de prueba.

## Protecciones y límites

- Destino exacto 8C40/9D0R1LA/F.18, CPU i7-13700H y módulo IntelMSR SHA conocido.
- Una sola GUI/lectura Victus; slots WMI independientes acotados a un worker por
  grupo. Una llamada bloqueada no se reemplaza por más llamadas nativas.
- TZ01/DTT3 se unen por epoch <= inicio de snapshot y edad <3 s; dos adquisiciones
  distintas y >=1 s cualifican. Un query reciente no prueba que el firmware haya
  actualizado físicamente el sensor. DTT1/DTT2 se registran como contexto.
- Solo AC. Una pérdida de fuente habilitada, cambio a batería, discontinuidad de
  telemetría, suspensión, pantalla Off o Stop termina la prueba y cierra admisión.
- Control mediante el coordinador, Guardian WMI y watchdog existentes. El hook
  experimental se vuelve a comprobar inmediatamente antes del request nativo.
- Envolvente existente: CPU <=90 °C al arrancar; CPU >=95 °C tiene confirmación
  de 2000 ms; CPU >=99 °C devuelve inmediatamente a Firmware; GPU <=82 °C,
  CPU package <=60 W, GPU <=75 W. Las protecciones raw no dependen del promedio
  de núcleos elegido. Se mantienen pasos y niveles 10–50 de la revisión explícita.
- Las fuentes auxiliares faltantes no se sustituyen por cero. No hay reacquire
  automático de una sesión experimental interrumpida.
- Fin/Stop intenta todos los cierres del runtime, incluyendo restaurar Firmware
  y liberar CPU/GPU mediante el Guardian. Un fallo de liberación se registra con
  código no cero; los journals originales se conservan. No se fuerza recuperación.

## Evidencia y análisis

`experiment.jsonl` no rota: contiene snapshots completos de los 14 núcleos,
cargas/potencias, niveles HP-WMI y sus epochs, TZ01/DTT1/2/3, admisión, las siete
políticas, contribución adicional, request físico, resultado del dispatch,
etapas y cleanup. `appliedChanges` cuenta cambios de request aceptados; no cuenta
transiciones medidas de RPM ni demuestra el setpoint instalado. Los resultados del controlador se distinguen de los niveles
observados. Estos niveles dan RPM nominales de 100 RPM por nivel, no una lectura
exacta de tacómetro ni de ruido. `profiles.json`, `metadata.json` y la identidad
del paquete fijan configuración y build. `summary.json` no afirma un PASS físico.
El launcher añade SHA-256 de los archivos y empaqueta el resultado al salir.

El checker independiente verifica epochs, admisión, MAX de las seis entradas,
interpolación, límite de retención, correspondencia entre decisión y dispatch,
conteos y terminal completo. Produce máximas/P95 por bloque y cobertura de carga:

```bash
python scripts/check-platform-physical-experiment.py directorio-extraido-de-evidencia
```

La CI genera un protocolo completo **sintético**, lo verifica por separado y
prueba pérdidas de fuente, cambio AC/Battery, gaps, clocks futuros, cache,
cambio de etapa y pérdida entre evaluación y request nativo. Ese PASS no prueba
hardware Windows del notebook.

Criterios para analizar el resultado real:

1. Confirmar integridad, cleanup y bloques completos. Una interrupción conserva
   evidencia útil pero no cuenta como protocolo completo.
2. Comparar A/B con potencias y cargas semejantes, contando cobertura; separar
   carga y enfriamiento. No interpretar una GPU menos cargada como mejor curva.
3. Comprobar cuánto retiene B, sus pasos/transiciones y si los ventiladores
   observados siguen los requests. Mantener separado el shadow contrafactual.
4. Comparar temperaturas, duración del enfriamiento y rendimiento del benchmark.
   El rendimiento/ruido no se mide automáticamente: conserva el resultado del
   benchmark y, si importa la acústica, una medición externa comparable.
5. Revisar fuente/frescura y todas las causas de interrupción. Una ejecución
   aislada informa sobre esta configuración/carga/equipo; no establece seguridad
   universal ni justifica habilitar el suplemento por defecto.
