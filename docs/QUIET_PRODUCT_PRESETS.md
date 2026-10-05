# Presets silenciosos AC/Batería y operación de la GUI

## Evidencia y alcance

El diagnóstico GUI (5), capturado 2026-10-05 21:01:17 UTC, corresponde al target
8C40/F.18, fuente Ac, CPU/GPU sin aplicar. Automatic sostuvo 30 sin retransmisión.
El retorno explícito a Firmware comenzó 18:00:55.644 Santiago; una lectura WMI
rechazada por el cierre previsto de nuevas lecturas produjo Degraded a las
18:00:55.950 y bloqueó lifecycle. Las solicitudes de liberación terminaron a las
18:00:56.612 y Healthy regresó a las 18:01:00.520. El bloqueo persistía.
No hay prueba de reactivación de una curva modificada en esa captura.

La corrección etiqueta exclusivamente el rechazo previo al despacho causado por
una liberación explícita de la GUI. El worker aplaza esa adquisición sin renovar
ningún reloj de frescura, sin contar una muestra y sin cambiar Healthy. Las
excepciones nativas, recovery no planificado y watchdog de 3 s mantienen su
respuesta de seguridad. Automatic y las selecciones de modo comparten una cola;
los comandos Performance usan otra para que iniciar su Guardian no detenga las
adquisiciones Automatic. Firmware sigue disponible durante un Aplicar CPU/GPU.

Referencias del firmware usadas para diseñar los nuevos candidatos:

| Traza HP automático del 8C40 | Muestras | CPU RPM | GPU RPM |
|---|---:|---:|---:|
| 2026-10-02_234852_idle_hp-auto.csv | 112 | 2500–2600 | 2300–2400 |
| 2026-10-02_235753_idle_hp-auto.csv | 109 | 2500–2700 | 2300–2400 |

SHA256 respectivamente:
`1692553897057c22db3b21f07e557d14c0899148983b45d3ce6c64d5573ada93` y
`8bae10379d887e9c3ee4365d5155fe28fe499007950c8690e70a10b24db46f75`.
La primera traza incluye CPU Package hasta 95 °C y potencia hasta 87.208 W:
no es una meseta térmica de reposo ni un escenario admitido por la prueba
supervisada de bajo consumo. Los CSV no identifican la fuente de alimentación;
no se atribuyen como caracterización independiente AC/Batería.

Batería es un candidato derivado más silencioso en frío, no un ajuste medido del
firmware en batería. Ningún replay prueba ruido, temperatura resultante, caudal,
ni estabilidad física bajo todas las cargas. La antigua calibración equal 10–50
sustenta el rango de consignas; la aceptación térmica de estos presets queda pendiente.

## Seis curvas de cada preset

Entradas y niveles se interpolan linealmente; domina MAX de las seis demandas.
La temperatura CPU de demanda es el promedio de los tres P-Cores más calientes.
La seguridad conserva MAX(Package, núcleo más caliente). No se editan sus umbrales.

| Eje | AC: entrada→nivel | Batería: entrada→nivel |
|---|---|---|
| CPU °C | 40→12, 50→16, 60→21, 70→28, 78→35, 85→44, 90→50 | 40→10, 50→13, 60→18, 70→26, 78→35, 85→44, 90→50 |
| GPU °C | 35→12, 45→15, 55→20, 65→28, 72→35, 78→44, 81→50 | 35→10, 45→12, 55→17, 65→26, 72→35, 78→44, 81→50 |
| CPU W | 0→10, 15→10, 30→16, 45→23, 65→32, 90→43, 115→50 | 0→10, 10→10, 15→12, 25→17, 40→24, 60→32, 90→43, 115→50 |
| GPU W | 0→10, 20→10, 40→16, 70→24, 95→34, 115→42, 140→50 | 0→10, 10→10, 20→12, 40→17, 70→26, 95→36, 115→44, 140→50 |
| CPU carga % | 0→10, 25→10, 50→12, 75→18, 100→24 | Igual AC |
| GPU carga % | 0→10, 25→10, 50→12, 75→18, 100→24 | Igual AC |

EMA subida 8 s, confirmación 3 s y paso normal 1 nivel. Tras carga breve:
EMA bajada 6 s, confirmación 4 s. Tras carga prolongada: EMA 20 s y confirmación
16 s. Se conservan el seguimiento temporal de carga sostenida y el paso térmico 4.
No hay piso oculto 30 en las curvas de potencia/carga. El motor preparado histórico
continúa en 30–50; solo la entrada explícita de revisión producto toma 10–50.

Replay del motor puro con fechas y demandas originales, sin simulación térmica:

| Traza | Candidato | Nivel mínimo / medio / máximo | Cambios |
|---|---|---|---:|
| 234852 | AC | 15 / 18.946 / 26 | 13 |
| 234852 | Batería | 12 / 18.045 / 25 | 18 |
| 235753 | AC | 15 / 15.349 / 16 | 3 |
| 235753 | Batería | 13 / 13.229 / 15 | 4 |

En muestras frías idénticas durante 300 s, los tests exigen nivel constante 12 AC
/ 10 Batería. También exigen llegar a 50 ante una entrada caliente, no escribir
al editar/guardar y liberar antes de reconfigurar/reentrar.

## Instalación del preset en preferencias existentes

Los perfiles ya guardados y las curvas del usuario se conservan. En Firmware:
Curvas → AC → Valores iniciales del perfil; repetir con Batería; Guardar curvas.
Esto reemplaza las seis curvas y su inercia del perfil seleccionado, y mantiene
PL1, PL2 y máximo GPU previamente editados. Instalar un paquete no aplica hardware.

## CPU/GPU de uso normal

CPU PL1/PL2 usa el rango de producto, validación PL2≥PL1, journal y recuperación
por Performance Guardian. Defaults CPU AC 35/60 W y Batería 8/15 W permanecen.
GPU ajustable AC 210–1850 MHz y Batería 210–1200 MHz, mínimo locked 210 MHz.
La aplicación explícita lleva la configuración congelada al mismo controlador
NVML; no implica que cada MHz tenga cualificación física separada. Un rechazo
NVML se informa como fallo. ActiveUnverified sigue significando aceptación de
la petición, no lectura independiente del rango arbitrario ni rendimiento garantizado.

Performance puede aplicarse con fans en Firmware, Manual o Automatic si Healthy
sin interrupción ni recuperación pendiente. No se reemplaza un Guardian vivo:
liberar antes de aplicar nuevos límites. Fan Control y Performance conservan
su recuperación independiente; no forman una transacción atómica. Iniciar,
guardar/importar o cambiar la pestaña AC/Batería no aplica límites.

## Prueba física pendiente

`Start-ProductGui.ps1 -Mode AutomaticReview` requiere 8C40/F.18, empieza en
Firmware y conserva la revisión de 300 s, tres adquisiciones Healthy y el margen
CPU ≤90 °C/60 W, GPU ≤82 °C/75 W. Una liberación voluntaria limpia permite
reaplicar una curva modificada. Un fallo real, cambio de fuente, lifecycle o
vencimiento conserva el bloqueo y ahora muestra su motivo en Ventiladores.

Primero probar Automatic → Firmware → Automatic con el nuevo preset y exportar
estado activo/final. Luego, en Firmware, aplicar CPU/GPU y revisar sus estados;
solo después probar convivencia. Los limitadores Guardian responden a AC/Batería
independientemente; el cambio de fuente aún interrumpe la revisión de fans.
