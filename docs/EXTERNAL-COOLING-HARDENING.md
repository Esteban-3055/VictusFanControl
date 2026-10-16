# ExternalCooling — correcciones de robustez

Revisión posterior a la primera preliminar ESP32, sobre la base bf79edb. El producto y el
simulador siguen sin firmware ni acceso a hardware externo físico.

## Correcciones

- El ejecutable del simulador utiliza un bucle de servidor compartido con las pruebas.
  InvalidDataException, SocketException y otros errores esperados cierran solo la conexión.
  Se prueba un hello inválido antes de autenticar, secuencias repetidas y evidencia caducada,
  seguido de conexiones correctas al mismo servidor.
- La entrada oculta limita el tamaño, permite corregir con retroceso, reintenta Base64 inválido
  o de longitud distinta de 32 bytes, y admite Escape/Ctrl+C. No hay claves en argumentos,
  logs o mensajes de error. Opciones inválidas, entrada redirigida y puerto ocupado tienen
  mensajes y salidas controladas.
- Una Solicitud válida exige fechas de muestra y temperaturas presentes y vigentes dentro
  del mensaje. Automático exige además Demanda y fecha de decisión vigentes. El límite
  defensivo v1 es menor de 5000 ms; el exportador conserva las restricciones más estrictas
  del controlador. Se prueban relojes del receptor distintos, sin NTP.
- El simulador modela la caducidad remota mediante un temporizador independiente de lectura;
  caducidad, EOF o rechazo restablecen el estado local simulado. Silence ya no retiene
  indefinidamente una conexión cerrada.
- El aviso de configuración dañada sobrevive al inicio deshabilitado y a la reanudación.
  El archivo original permanece intacto hasta que el usuario guarda preferencias válidas.
- La espera progresiva alcanza 30 segundos más 0–249 ms de jitter, como indica el contrato.
- Diagnósticos incluyen categoría y fecha del último fallo, sin texto de excepciones ni
  datos de autenticación. Los errores de emparejamiento quedan visibles.
- Las reanudaciones duplicadas no reinician una conexión activa. Una suspensión más nueva
  invalida una reanudación pendiente. Desconectar continúa revocando la reconexión y las
  llamadas tardías tras Dispose no cambian el estado final Disabled.
- Una lectura cancelada tampoco devuelve mensajes que ya estaban en el buffer.

## Investigación adicional

Se inspeccionaron límites de mensajes, JSON, cancelación, propiedad de claves, sesiones,
autenticación, vigencia de la Solicitud, persistencia y cambios de ciclo de vida. Los casos
nuevos se añaden a HardeningTests.cs y a la prueba WinForms de ExternalCooling. Las pruebas
anteriores de transporte también usan el bucle real SimulatorServer, evitando una copia
del servidor que podría ocultar fallos del ejecutable.

La semántica HP WMI, control interno, curvas, SafetyGate, watchdog y Performance Guardian
no cambia. Las respuestas del dispositivo son informativas y no aplican autoridad interna.

El temporizador del simulador no demuestra el fallback de un ventilador real. Siguen
pendientes pruebas físicas de Wi-Fi/mDNS, suspensión real, NTC, LCD, PWM y duración prolongada.
El handshake autentica e integra mensajes; el protocolo TCP no cifra la telemetría.

## Verificación y publicación

Ejecutar scripts/test-external-cooling.ps1 para compilar Release y correr los 18 fixtures,
incluidos los casos nuevos dentro del fixture de protocolo/transporte y la prueba de GUI.
Los vectores HMAC v1 siguen siendo los mismos; el contrato no introduce campos nuevos.

En este equipo, el fixture preexistente performance-gui-self-test no puede leer
C:\ProgramData\VictusFanControl\WatchdogM2. No se cambian sus ACL ni registros de recuperación.
El CI en Windows limpio debe confirmar ese fixture junto con los otros 17 antes de integrar.

Este cambio no publica una versión ni reemplaza archivos de la preliminar existente.
Para la próxima preliminar:

1. Usar etiqueta numérica reconocida por 1.2.9, por ejemplo v1.2.10, y marcar Pre-release.
2. Hacer coincidir etiqueta, versión interna y nombre del instalador.
3. Comprobar con el parser del actualizador 1.2.9 la detección con preliminares habilitadas,
   la verificación del SHA, la descarga y la confirmación de la release antes de instalar.
4. Verificar que el canal estable continúa excluyendo la preliminar.
5. Publicar únicamente artefactos del SHA exacto con CI correcto, conservando las limitaciones
   físicas y sin afirmar una instalación real que no se haya ejecutado.
