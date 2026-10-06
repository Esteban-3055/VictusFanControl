# Motor de demanda única del producto

## Cálculo compartido y alcance

La GUI producto usa `FanConfiguration.UnifiedDemand`; el motor puro, inercia,
controlador Automatic, simulador, marcadores y diagnósticos comparten ese modelo.
Las rutas históricas sin el campo conservan las seis curvas y su comportamiento;
no se cambia la autorización del Automatic normal ni se promueve una calificación.
AutomaticReview conserva el target 8C40/F.18, 300 s, admisión, CPU 2000 ms/99 °C,
GPU/potencias, Guardian y liberación Firmware. No hay nuevo IO nativo directo.

Cada señal se normaliza a 0–100 % con límites fijos. Se multiplica por su influencia,
se limita cada contribución a 100 %, se toma MAX y se interpola una sola curva.
Los porcentajes son ganancias independientes; no suman 100 % ni se promedian.

| Variable | Referencia fija (0 % → 100 %) | Influencia AC / batería | Rango del slider |
|---|---|---:|---|
| Temperatura CPU de demanda | 40 → 90 °C | 100 / 100 % | 100–150 % |
| Temperatura GPU | 35 → 81 °C | 100 / 100 % | 100–150 % |
| Potencia CPU real | 0 → 60 W | 40 / 20 % | 0–100 % |
| Potencia GPU real | 0 → 75 W | 60 / 35 % | 0–100 % |
| Carga CPU | 0 → 100 % | 20 / 10 % | 0–100 % |
| Carga GPU | 0 → 100 % | 20 / 10 % | 0–100 % |

CPU de demanda conserva el promedio de los tres P-Cores más calientes por default.
El máximo crudo paquete/núcleo se conserva aparte y nunca se promedia para seguridad.
Las referencias de potencia no dependen de PL1/PL2, MHz ni de la fuente: limitar a
18 W en batería no convierte 18 W en 100 % de demanda. Las lecturas no se recortan
antes de la admisión: 72.485 W GPU no se convierte a 70 W. Normalizar/clamp al calcular
porcentaje no significa admitir una lectura fuera del sobre físico de revisión.

## Curvas e intervención protegida

AC: 0→12, 20→12, 40→21, 60→28, 76→35, 90→44, 100→50.
Batería: 0→10, 25→10, 40→12, 60→24, 76→35, 90→44, 100→50.

Las curvas tienen 2–64 puntos enteros, entradas estrictamente crecientes, niveles
10–50 no decrecientes y extremos de entrada 0/100 %. El punto final 100→50 no se
mueve ni elimina; el primero tampoco se elimina. Los sliders térmicos no permiten
menos del 100 % de la señal base; los otros aportes pueden desactivarse con 0 %.

CPU raw ≥85 °C o GPU ≥78 °C impone contribución térmica al menos 90 % y objetivo
mínimo 44. CPU raw ≥90 °C o GPU ≥81 °C impone 100 % y objetivo 50. Estas condiciones
no se editan ni se desactivan con pesos o nodos. La inercia conserva el paso térmico
4, bypass de EMA térmico y descenso confirmado. Un objetivo protegido no implica
que el backend ya haya alcanzado su nivel: el punto verde representa la solicitud
aceptada. SafetyGate y el sobre de revisión se evalúan antes del despacho.

## Editor, simulación y marcadores

Curvas tiene paneles Influencias y Puntos, un gráfico Demanda %→Nivel y selección
AC/Batería. Azul es la curva editable; gris discontinua es el default de la misma
fuente, no firmware. Amarillo es el objetivo calculado con el borrador actual;
verde la solicitud aceptada con configuración congelada y adquisición exacta.
La etiqueta del gráfico muestra demanda; los diagnósticos incluyen las seis contribuciones
y la variable dominante en la decisión del controlador. Si faltan sensores o hay
valores inválidos no se inventa demanda. Marcadores aplicados requieren autoridad,
fuente/perfil coincidentes y telemetría/decisión frescas.

El simulador usa la misma demanda e inercia y el mismo gráfico con puntos de objetivo
y nivel simulado; avanza automáticamente con datos sintéticos. No simula transferencia
térmica, RPM, Guardian ni admisión física. Editar curva/influencia reinicia su historia
para evitar mezclar configuraciones. Editar/guardar/importar no aplica hardware.
Automatic mantiene su configuración congelada hasta volver a Firmware y reentrar.

## Persistencia y migración

ProductProfiles v2 requiere el modelo nuevo para AC y batería. FanConfiguration
conserva su esquema externo v1 y campo opcional, para que los consumidores históricos
sin ese campo mantengan sus contratos. Las seis curvas heredadas quedan almacenadas
como datos de compatibilidad y se ignoran cuando UnifiedDemand está presente.

Cargar/importar ProductProfiles v1 primero valida el archivo. Después conserva
Fan completo en LegacyFan, conserva PL1/PL2/MHz, interruptores y preferencias de
inicio, y crea modelos nuevos independientes por fuente. Se informa migración
pendiente de guardar. Cargar no escribe ni inicia modo de control.
Guardar crea `product-profiles.json.v1-backup-<SHA256-prefijo>.json` con bytes exactos
antes de reemplazar v1. No sobrescribe un respaldo distinto. El respaldo integrado
LegacyFan permanece tras editar/restablecer y exportar v2. No se promete equivalencia
entre la curva nueva y las seis curvas antiguas.

## Verificación y límites

Tests: demanda MAX con carga mixta; cero en potencia/carga; temperatura mínima;
monotonicidad de ganancias; 72.485 W sin corte a 70; overrides raw ante curva plana;
telemetría inválida; esquema estricto; migración exacta y repetida; aislamiento,
configuración congelada, simulación equivalente; aplicación real del modelo a través
del controlador con backend sintético; gestos, nodos, límites y renders Windows.

La caracterización térmica/acústica de estos defaults sigue pendiente, especialmente
batería. Los logs históricos no validan físicamente el nuevo cálculo.
