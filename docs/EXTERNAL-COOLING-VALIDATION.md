# Fase 1: integridad y validación

Base: v1.2.9, `e8a58a5cb01eb673114a8dca0c5c0e925f55fddc`.
Rama de integración: `feature/external-cooling-wifi`. La base del PR es la rama vigente
`feature/fan-54-52-preview`, para no introducir en main toda la historia aún no integrada.

## Registro por etapa

| Etapa | Resultado ejecutado |
|---|---|
| 1. Auditoría y base | API GitHub: releases/ramas/PR/CI; Release 0 warnings/0 errors; regresiones iniciales SafetyGate, autoridad, política, WMI, coordinación y GUI completas aprobadas |
| 2. Exportador | RAM física real GlobalMemoryStatusEx; fuentes CPU/GPU existentes; pruebas de validez, antigüedad, nulos, fuentes, modo, autoridad y destinos 54/52 |
| 3. Protocolo | Roundtrip, versión, tipos, duplicados JSON, campos ausentes, rangos, nulos, HMAC, secuencias/sesiones y extensiones opacas |
| 4. Transporte | TCP real localhost: autenticación mutua, fragmentación/coalescencia, desconexión, invalidez, mensaje incompleto, retraso, duplicado, silencio y cierre cancelable |
| 5. Integración | Exportador real de la aplicación conectado a servidor TCP de fixtures: primer mensaje sin orden, Automático 60/30, Manual 54 con destinos 54/52, Firmware null |
| 6. Interfaz | WinForms ejecutado: DPAPI CurrentUser real, persistencia, entrada en Configuración, capturas revisadas en dos tamaños a DPI alto, ventana oculta, suspensión/reanudación por eventos y cierre |
| 7. Robustez | Clave incorrecta con cero telemetría; servidor inaccesible; reinicio real del servidor; sesiones nuevas; concurrencia del manager; referencia de respuesta caducada conserva Local; consumo/cadencia medidos |
| 8. Entrega | Script único de regresión, nuevo workflow dedicado, flujos existentes habilitados para la rama, herramientas fuera del ejecutable normal, protocolo y guía incluidos por el empaquetador |

`scripts/test-external-cooling.ps1` genera `logs/external-cooling-self-test/validation.json`
con el SHA y los códigos de salida; `external-cooling` sube la evidencia como artifact del
SHA de GitHub. El estado final de CI y los enlaces a sus runs se registran en el PR y el
informe de entrega; no se deduce success de una compilación local ni de un run anterior.

La restauración local utilizó paquetes ya disponibles y NuGetAudit=false por falta de acceso
al servicio de vulnerabilidades; CI utiliza la restauración normal. Python se tomó del runtime
incluido en el entorno. Los tests de procesos/DPAPI/TCP requieren ejecución local permitida
fuera del aislamiento; TCP se limita a loopback. No se desactivaron protecciones del producto.

## Auditoría de independencia

* El proyecto ExternalCooling referencia solo .NET: no WMI, EC, PawnIO, NVML, watchdog,
  PerformanceGuardian ni controladores internos. Solo conoce ITelemetryExportSource.
* El adaptador usa ProductRuntimeState; la única adquisición agregada es RAM física nativa.
* No hay cambios en Hardware/, Control/, Safety/, Performance/, guardianes, curvas, límites,
  filtros, recuperación, ni escritura de hardware. ProductRuntime agrega exclusivamente un
  dato de observación de aceptación Manual para no exportar un objetivo heredado de Automático.
* Recepción de ESP32 no se vincula a ningún método de control interno. Las extensiones se
  aceptan estructuralmente y se eliminan de las proyecciones de diagnóstico/pantalla.
* Transporte: 4096 bytes, profundidad 8, deadlines completos, una escritura en vuelo, cero
  cola pendiente, diccionarios de 16 timestamps, reconexión hasta 30 s y una sola tarea activa.
* Clave aleatoria de 256 bits protegida por DPAPI CurrentUser. No aparece en mensajes,
  diagnósticos, argumentos de procesos, artifacts de CI ni logs. Comparación de HMAC constante;
  claves derivadas por dirección/sesión y memoria de claves borrada al cerrar.
* No se transmite Solicitud antes de autenticación y confirmación válida del dispositivo.
  Una referencia monotónica propia caducada impide aplicar solicitudes retrasadas en el
  simulador; el firmware deberá implementar además su temporizador físico de fallback.
* Preferencias en LocalAppData separado del payload sustituido durante actualización.
  No se exportan junto a perfiles ni diagnósticos. El proyecto del simulador solo es referencia
  de las pruebas de desarrollo; la app referencia únicamente la biblioteca ExternalCooling.

## Límites de la evidencia

La RAM fue leída realmente de Windows. CPU/GPU, Demanda y Solicitud se integraron con las
fuentes reales del programa y se comprobaron usando estados de controlador simulados: no se
activaron controles del notebook para esta fase ni se certifica una nueva prueba física de ellos.
Las regresiones sintéticas conservan el contrato existente; no reemplazan cualificación física.

Suspensión y reanudación se validaron inyectando eventos reales de la ruta WinForms y nuevos
sockets autenticados; no se suspendió físicamente el equipo. Wi-Fi perdido se cubre mediante
desconexión/silencio/servidor inaccesible/reinicio TCP; no se apagó el adaptador Wi-Fi del usuario.
Quedan pendientes el ESP32 físico, NTC, LCD, PWM, mDNS de la red real, carga prolongada y
fallback físico. **No hay PASS físico ESP32.** Telemetría autenticada no está cifrada; contrato
destinado a LAN de confianza. No hay release estable publicada ni merge automático.
