# HP 8C40/F.18: ajustes y cierre de operación normal

## Evidencia revisada

Captura `VFC-WMI-Diagnostico_2026-10-04_150610_d01746.zip`, SHA256 `1073564cfb93dab298857538f6fff08ec559557590cd556df2b5fc051d5a69f6`. Los 247 archivos declarados en el manifiesto coinciden. El código registrado es `8a331f2e6413acf04c5e318da95556a614bd2371`.

177 muestras, 176 decisiones y 24 cambios normales de nivel aceptados con retorno 0. Finalización por `stop-signal`, unos 296 segundos entre decisiones; supervisor y worker terminan sin llamada nativa pendiente ni lease retenido. Las peticiones de recuperación FF/FF y LegacyDefault retornan 0. No hay nuevos eventos ACPI registrados ni lecturas EC directas. La edad RPM observada es 328–890 ms. CPU Package máximo 89 °C, núcleo/control máximo 91 °C, GPU máximo 49 °C.

Esta captura cierra la evidencia de operación normal de esa versión del experimento. No ejercita CPU ≥95 °C, no verifica de forma independiente la recuperación de propiedad del firmware (`FirmwareRestorationVerified=false`) y no califica el backend final con EC/ACK/watchdog. Tampoco valida físicamente los nuevos ajustes de esta entrega. Automatic en la GUI sigue cerrado.

## Perfil candidato Firmware suave

Las capturas del firmware muestran RPM distintas a igual temperatura según el estado previo y la carga; no son un ensayo comparable de ruido/temperatura. El perfil busca aproximar demanda moderada y eliminar subidas normales causadas sólo por la memoria de un pico. Las RPM físicas pueden diferir de las nominales y entre CPU/GPU, aunque la consigna sea igual.

Al reproducir las 176 adquisiciones completas de d01746 con el algoritmo compartido, el modo anterior produce los mismos 24 cambios registrados, consigna media 37,801 y máximo 45. El candidato exportado con piso 30 produce 19 cambios, media 35,392 y máximo 42. El candidato GUI con piso 26 produce 19 cambios, media 32,392 y máximo 39. Se usan tiempos originales y máximo Package/núcleo; no se reproduce transporte ni se predice la temperatura o el ruido que habría causado otra velocidad física.

| Parámetro | Experimento previo, sin configuración | Nuevo candidato GUI |
|---|---:|---:|
| Piso | 30 | 26 (2600 RPM nominales) |
| Techo | 50 | 50 |
| EMA subida | 6 s | 4 s |
| EMA bajada | 20 s | 20 s |
| Confirmación subida | 2 s | 1 s |
| Confirmación por bajada | 12 s | 16 s |
| Paso normal subida / bajada | 1 / 1 | 1 / 1 |
| Paso térmico máximo | 4 | 4 |
| Memoria del pico en EMA normal | Sí | No |

CPU: 40→26, 50→26, 60→26, 70→28, 78→34, 85→44, 90→50. GPU: 35→26, 45→26, 55→27, 65→29, 72→34, 78→44, 84→50. Potencia/carga conservan las curvas Candidate V1 y el máximo de las seis demandas. Un pico activa demanda original inmediatamente, con el paso térmico existente, sin sobrescribir el historial normal. Después se conserva la bajada gradual; no se asume una máquina fría si la primera muestra ya está caliente.

## GUI y persistencia

Estilo oscuro inspirado en la imagen de referencia, navegación Monitor/Ventilación/Ajustes/Diagnósticos, CPU/GPU en paneles y gráfico de RPM y temperatura reales. El historial se reinicia ante un intervalo >3 s y no une muestras inválidas. Navegar no cambia el modo de ventilación.

Ajustes permite editar las seis curvas, mínimos/máximos, ambos filtros, tiempos de confirmación, pasos normales, umbrales de respuesta térmica dentro de límites conservadores, pausa normal y memoria del pico. Los cambios se mantienen en borrador hasta Aplicar y guardar, sólo en Firmware con autoridad Firmware. El panel se bloquea mientras guarda. El archivo JSON estricto y atómico se guarda en `%LOCALAPPDATA%\VictusFanControl\fan-configuration.json`; un archivo inválido usa los valores predeterminados y muestra aviso. La configuración nunca guarda modo, propiedad ni autorización. Manual conserva su ruta y rango calificados.

Las protecciones se muestran en la GUI: CPU 95 °C con cinco muestras únicas o 2 s, CPU 99 °C/GPU 87 °C inmediatos, frescura 3 s, paso térmico 4, Package/núcleo más caliente y autorización Automatic. Permanecen protegidas. No son preferencias operativas editables.

El editor de perfiles de Ventilación conserva su función de vista previa. Para incorporarlo a la configuración guardada, abrir el editor desde Ajustes y después Aplicar y guardar.

## Prueba de los nuevos ajustes

Exportar prueba 30–50 desde Ajustes crea un JSON con piso ≥30; no aplica cambios a la GUI ni inicia hardware. El experimento activo rechaza archivos con piso <30 antes de conectar WMI. La GUI puede preparar el piso 26, pero su validación física queda pendiente. Sin archivo explícito, el experimento mantiene todos sus valores previos.

Volver a Firmware, cerrar la GUI y usar PowerShell como administrador, desde el repositorio actualizado:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\Start-Victus-WmiFanExperiment.ps1 -CaptureMinutes 5 -Control -FanConfigurationPath C:\ruta\fan-test-30-50.json
```

El supervisor congela el JSON de la sesión y el worker guarda configuración, política e inercia efectivas en la evidencia. No cambian whitelist, guardas térmicas ni recuperación. Usar carga habitual, sin provocar temperatura de emergencia. Conservar ZIP/SHA256 y comparar ruido percibido, consignas, RPM y temperaturas. Esa captura decide el ajuste del perfil; la calificación del backend final sigue siendo una fase separada.

## Verificación de software

Las suites deterministas verifican serialización/copia, rechazo de parámetros inválidos y campos de autorización, conservación tras fallo de persistencia, rechazo de aplicar en Manual, ausencia de órdenes al guardar, subida térmica sin subidas normales artificiales, bajada de 16 s, inicio caliente, EMA de precisión completa y redondeo 3,05→3,0 / 3,06→3,1. `--dashboard-self-test` usa controles WinForms reales con backend sintético y renderiza Ajustes a dos tamaños y Ventilación; verifica borrador, seis ejes, bloqueo durante guardado y liberación de todas las páginas. No constituye una prueba física del portátil.


## CPU Average como fuente de demanda

Las configuraciones nuevas usan `CoreAverage`: promedio aritmético de todos los
núcleos físicos de una misma muestra (6 P + 8 E en el objetivo validado). No es
un promedio temporal ni una lectura externa de CPU Package. Ajustes permite
seleccionar «CPU Average (núcleos físicos)» y guardar/exportar la elección.
Los JSON v1 anteriores sin `cpuTemperatureSource` conservan Package/núcleo más
caliente; para migrarlos se selecciona Average y se aplica en modo Firmware.

La selección se comparte entre experimento, Automatic preparado, vista previa
y marcadores del editor. Solo cambia la entrada térmica CPU y su respuesta
rápida configurable; potencia/carga CPU y las tres entradas GPU siguen
participando en MAX. Los registros del experimento separan fuente, temperatura
de demanda, promedio y temperatura de seguridad. Un promedio incompleto o
inválido rechaza la demanda sin sustituirla silenciosamente por Package.

La emergencia sigue usando MAX(Package, núcleo más caliente), con confirmación
95 °C y entrega inmediata 99 °C. La GPU conserva 87 °C. Average puede ser mucho
menor que el núcleo más caliente; las curvas existentes requieren validación
física con la nueva fuente antes de afirmar una mejora térmica o acústica.
La selección no abre la autorización de Automatic ni altera Manual.
