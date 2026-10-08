# TZ01/DTT3 sobre la politica existente: propuesta y experimento

## Decision

La caracterizacion de sensores v4-v9 ya esta hecha. TZ01 esta trazada a RTMP
mediante ACPI; DTT1/DTT2 siguen dominios CPU; DTT3 tiene una respuesta
GPU/plataforma filtrada o derivada. No se repite ese inventario ni se considera
que el fallo del predictor OEM invalide los sensores.

La propuesta es conservar la curva propia y estudiar **retencion de ventilacion
durante enfriamiento** mediante TZ01/DTT3. El candidato preferido para la siguiente
fase es `both-retention`: los sensores pueden sostener demanda previamente
alcanzada, pero no iniciar una subida nueva por si solos. La variante `both`, que
permite demanda adicional completa, se conserva como comparacion. Esta eleccion
es una decision de diseno apoyada por el experimento, no una cualificacion termica.

Los valores de las curvas nuevas son hipotesis de producto, no reglas firmware
demostradas. En particular, AC1=55/AC0=67 no se convierten automaticamente en
umbrales obligatorios de ventilacion ni en limites de seguridad.

## Arquitectura desarrollada

1. Se calculan las seis demandas existentes, sus influencias y su maximo.
2. El observador separado admite TZ01 y/o DTT3 por identidad de fuente, unidad y
   timestamp ya preservados por la captura. En replay comprueba edad, continuidad
   y adquisiciones distintas. No realiza acceso EC directo ni llamadas nativas.
3. Cada temperatura admitida se convierte en una demanda de nivel de ventilador.
   Se toma su maximo; no se promedian temperaturas ni se suman demandas.
4. En `both-retention`, ese maximo se limita al ultimo nivel del observador paralelo.
   En el inicio se limita al nivel del calculo base. Es una **cota de demanda**, no
   una orden permanente: cuando los sensores bajan, tambien baja esa cota.
5. El maximo entre demanda base y suplemento entra en el **filtro e inercia
   existentes**, una sola vez. Se reutiliza `AdaptiveFanInertiaPolicy`; no se
   copia el controlador ni se anaden filtros EMA a las temperaturas nuevas.
6. CPU Package/core mas caliente y GPU siguen determinando la respuesta termica
   inmediata. El suplemento no activa, reduce ni sustituye sus protecciones.

Se anadieron dos sobrecargas internas de calculo y una identidad friend para el
replay. Las firmas publicas existentes se conservan y pasan suplemento `null`.
Ningun caller GUI, Runtime, Guardian o controlador productivo suministra el
suplemento. No hay nuevo modo activo, preferencia persistida o ruta de escritura.
El candidato reside en `tools/PlatformThermalReplay`, separado del runtime.

## Datos, ajustes y limites

Se usan las tres capturas conservadas, sin ajustar parametros contra los niveles
OEM. El experimento no puntua error de imitacion del firmware: calcula diferencias
de decisiones de nuestra politica con las mismas temperaturas registradas.

Se recuperan de los CSV originales v10/v11 las 14 temperaturas fisicas y sus tipos
Performance/Efficiency. No se infiere el tipo por indice o temperatura. Se conservan
las curvas y ajustes AC/Battery del `profiles-draft.json` del diagnostico GUI
1e30e2a y se prueban tambien los dos perfiles predeterminados de esta rama.
El borrador del diagnostico no demuestra cuales eran las preferencias activas
durante v10/v11: se aplican retrospectivamente para estudiar el producto actual.

Las fuentes de codigo de demanda, filtro y ajustes anteriores al cambio se
compararon con la GUI 1e30e2a: son iguales tras normalizar finales de linea.
La inercia tambien fue revisada frente a esa revision. No se cambia la base de
la rama ni se incorpora toda la GUI nueva a traves de este experimento.

La hora live-20261008 no tiene carga CPU ni temperaturas/tipos individuales de
cores. No se rellenan con cero, hottest-core o DTT1. Se evalua la disponibilidad
de TZ01/DTT3, pero **no hay decisiones completas de nuestra politica en esa hora**.
Por tanto, el regimen alto nuevo no queda probado con la politica extendida.

### Admision y perdida de una fuente

- Edad de consulta: `0 <= age < 3 s`; tres segundos exactos son stale.
- Separacion maxima entre frames: 3 s; fuera de orden/duplicados rompen continuidad.
- Se requieren dos adquisiciones distintas y al menos un segundo de historial por
  fuente habilitada. Filas cacheadas no califican nuevas adquisiciones.
- Una adquisicion futura, regresiva, con valor cambiado bajo el mismo timestamp,
  ausente, no finita o fuera de 0..120 C deja el candidato no disponible.
- En TZ-only, DTT3 no es un requisito; en DTT-only, TZ01 no lo es. Deshabilitado,
  el suplemento no crea requisitos de sensores.
- Consulta reciente no demuestra actualizacion fisica reciente: TZ01 puede tener
  su propia cadencia/cache ACPI. Ese limite permanece explicitamente abierto.

Mientras falla la admision, la salida propuesta es `NoTargetHandoffRequired`.
El observador paralelo conserva internamente la ultima demanda calificada como
**memoria**, sin etiquetarla como temperatura fresca ni proponer un objetivo.
Esto evita destruir la EMA en cada hueco del archivo. La perdida de entradas
CPU/GPU completas reinicia ese observador. Un futuro runtime debera devolver
autoridad a Firmware y exigir una activacion/revalidacion real para recuperar
control; no podra aplicar ciegamente ese nivel virtual al recuperar una fuente.

### Curvas experimentales fijadas antes del replay

| TZ01 (C) | Nivel | DTT3 (C) | Nivel |
|---:|---:|---:|---:|
| 40 | 12 | 40 | 12 |
| 50 | 16 | 45 | 16 |
| 60 | 22 | 50 | 22 |
| 70 | 28 | 55 | 28 |
| 80 | 34 | 60 | 34 |
| 90 | 40 | 67 | 40 |
| 100 | 44 | 75 | 44 |

Interpolacion lineal, extremos saturados, suplemento maximo 44. La curva base
puede seguir pidiendo 50. El 44 es un limite del experimento, no una temperatura
o nivel cualificado por estas capturas. Para comprobar sensibilidad se desplazan
los puntos TZ01 +/-5 C y DTT3 +/-3 C; no se elige un ganador por acercarse al OEM.

## Resultados

Se ejecutaron 7 variantes x 4 configuraciones x 3 sesiones: **84 runs** y
**323876 filas**. Las variantes son base, TZ01, DTT3, ambas, ambas-retencion y
ambas con umbrales desplazados hacia frio/caliente. Las filas repetidas entre
variantes no son nuevas mediciones fisicas ni sesiones independientes.

Disponibilidad conjunta tras admision:

| Sesion | TZ01/DTT3 disponibles | Filas | Uso para politica completa |
|---|---:|---:|---|
| v10 | 4652 | 6049 | Si, en ventanas calificadas |
| v11 | 1961 | 1976 | Si, en ventanas calificadas |
| live-20261008 | 3539 | 3542 | No: faltan carga CPU y cores tipados |

El calculo sigue el flujo de frames archivados. No reproduce los instantes de
decision reales de GUI, el periodo de polling/ACK, las colas WMI o SafetyGate.
Los timestamps historicos aproximan adquisicion; watts/carga son contexto de fila.
Las duraciones usan intervalos entre dos decisiones disponibles separados <=3 s;
se excluyen huecos y requalificacion. La base se compara en esos mismos intervalos.

Con los ajustes **AC del diagnostico**:

| Variante | v10: tiempo por encima de base (s) | Maximo extra (niveles) | v11: tiempo por encima (s) | Maximo extra |
|---|---:|---:|---:|---:|
| TZ01 | 492.8 | 4 | 25.9 | 3 |
| DTT3 | 346.1 | 5 | 5.7 | 1 |
| Ambas | 492.8 | 5 | 25.9 | 3 |
| Ambas-retencion | 298.8 | 5 | 6.2 | 1 |

Los intervalos comparables suman 1469.1 s en v10 y 564.6 s en v11. Un maximo extra
de cinco niveles no significa cinco niveles de subida por muestra: puede aparecer
porque la base ya descendio y el candidato conserva un nivel anterior.

En v10, ambas-retencion conserva demanda extra durante 215.1 s con CPU<15 W y
GPU<10 W. Esto es contexto de baja potencia, **no prueba de que el chasis ya este
frio ni de que esa ventilacion sea necesaria**. Los cambios de nivel en los
intervalos comparables son 59 frente a 59 de la base; con ambas sin limitar son
63 frente a 59. En v11, ambas-retencion tiene 29 frente a 29; ambas tiene 31 frente
a 29. Contar cambios no mide ruido ni su audibilidad.

En estas sesiones/configuraciones no hubo demanda raw inferior a base, diferencias
de bandera de respuesta termica ni objetivos propuestos inferiores a base.
La no disminucion de demanda raw esta garantizada por MAX; la comparacion de
objetivos temporales es un resultado observado, no una demostracion matematica
para todas las secuencias posibles del controlador con histeresis.

TZ01 es la senal que mas altera la duracion con estos ajustes AC. No domina de
forma universal ni identifica la logica OEM. DTT3 conserva su caracterizacion y
puede aportar en otras fases. La configuracion importa: con ajustes de bateria
del diagnostico, retencion no cambia v11; con defaults aumenta mas la ventilacion.
No se presentan esas diferencias como una optimizacion universal.

## Verificacion y reproduccion

Se ejecutan **9236 comprobaciones puras** de admision, cache, continuidad, limites,
demanda adicional, respuesta termica y equivalencia null/neutral. Tambien pasan
los self-tests existentes de politica, produccion con mocks, perfiles y ajustes.

La DLL original, procedente del artefacto Windows de bde367e, reproduce exactamente
las 46268 filas base locales de las cuatro configuraciones y tres sesiones con
el ejecutable nuevo en `--baseline-only`. Se guardan firmas de decisiones discretas
para repetir esa comparacion entre plataformas, evitando exigir identidad binaria
de los ultimos decimales de `exp()` entre bibliotecas matematicas de distintos SO.

El checker Python independiente reconcilia 323876 filas: fuentes/timestamps,
admision, interpolacion, las seis demandas existentes, MAX adicional, ausencia
de objetivo durante fallos, firmas base, duraciones, histogramas y conteos. Las
salidas JSONL se completan atomicamente antes del resumen y luego se comprimen
despues de verificarlas. La CI ejecuta el proyecto y el checker en Windows/Ubuntu.

```bash
dotnet build tools/PlatformThermalReplay/PlatformThermalReplay.csproj -c Release
dotnet run --no-build -c Release --project tools/PlatformThermalReplay -- --self-test
dotnet run --no-build -c Release --project tools/PlatformThermalReplay -- \
  --replay tools/OemShadow/fixtures tools/PlatformThermalReplay/fixtures artifacts/platform-thermal
python scripts/check-platform-thermal-replay.py \
  tools/OemShadow/fixtures tools/PlatformThermalReplay/fixtures artifacts/platform-thermal --compress
```

El directorio de salida debe ser nuevo. `summary.json` incluye parametros, cuatro
configuraciones, hashes de fuentes y resultados de todas las variantes. El informe
de esta revision queda en `docs/oem-shadow/platform-thermal-policy-2026-10-08.json`.

## Plan de integracion restante

1. **Hecho:** caracterizacion previa, diseno de demanda/retencion, ablation,
   comparacion con ajustes reales, pruebas de fallo y regresion de la base.
2. **Siguiente fase de software:** integrar estas lecturas como telemetria
   observacional en el runtime con sus admission slots nativos acotados; registrar
   carga CPU, cores tipados, epochs/edad y configuracion vigente. Mostrar raw,
   contribucion y estado de fuente; no atribuir nombres fisicos inventados a DTT.
3. **Validacion observacional:** ejecutar la politica base y ambas-retencion en
   paralelo sobre entradas completas, especialmente recuperacion de carga GPU,
   perdida de fuente y cambios AC/Battery. No modificar los ventiladores en esta fase.
4. **Control opt-in posterior:** mantener el controlador existente, unir la demanda
   antes de su EMA, devolver autoridad a Firmware ante perdida de fuente habilitada,
   verificar reentrada y coordinar la transicion de perfiles sin heredar confirmaciones
   indebidas. Probar la ruta actual deshabilitada y las rutas nuevas con mocks.
5. **Prueba fisica acotada:** comparar base/candidato bajo condiciones comparables,
   midiendo temperaturas, rendimiento y niveles efectivos; medir ruido si se quiere
   declarar una mejora acustica. Un replay con temperaturas OEM fijas no sustituye
   esa prueba porque cambiar ventilacion tambien cambia las temperaturas futuras.

Conclusion de esta entrega: **GO para observacion de retencion; NO-GO para
activar el suplemento como control productivo predeterminado**. No es necesario
repetir todo el inventario. El dato pendiente mas concreto es la entrada completa
de nuestra politica durante el regimen alto de la captura nueva.
