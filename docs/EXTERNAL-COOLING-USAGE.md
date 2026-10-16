# Base externa / ESP32 — fase Windows

Implementación para VictusFanControl basada en v1.2.9. No incluye firmware ni prueba física
del ESP32. El módulo comienza deshabilitado; el programa puede iniciar sin dispositivo.
Es independiente de WMI, SafetyGate, ventiladores internos y Performance Control.

## Probar con el simulador

Requisitos: Windows, .NET 8 SDK y los requisitos de compilación existentes del repositorio
(incluido Python para generar el código de investigación usado por la solución).

1. Compilar desde la raíz:

   ```powershell
   dotnet build VictusFanControl.sln -c Release -m:1 -warnaserror
   ```

2. Abrir VictusFanControl de la forma habitual. Ir a **Configuración → Base externa / ESP32**.
   Elegir **Monitorización únicamente**, marcar **Simulador local**, puerto 8765.
3. Pulsar **Generar clave**, marcar **Mostrar clave nueva** y copiarla para aprovisionar
   el simulador. La clave solo se muestra mientras se configura; una clave ya guardada no
   se vuelve a revelar. Guardar una clave nueva reemplaza el emparejamiento anterior.
4. En otra terminal, ejecutar:

   ```powershell
   dotnet run --project tools/Esp32Simulator -c Release --no-build -- --port 8765
   ```

   Introducir la misma clave en la entrada oculta y pulsar Enter. No pasar claves como
   argumentos ni guardarlas en scripts. El simulador escucha solo en 127.0.0.1 por defecto.
   La clave generada tiene 44 caracteres y termina en `=`. Una entrada incorrecta permite
   reintentar; Escape o Ctrl+C cancelan la entrada. Se requiere una terminal interactiva.
   Los argumentos incorrectos y un puerto ocupado generan un mensaje breve y salida controlada.
5. Pulsar **Conectar** en la aplicación; guarda preferencias y emparejamiento. El campo de
   clave se vacía. Esperar **Connected / recepción válida**. TCP abierto o autenticado sin
   respuesta válida todavía no se presenta como Connected.
6. Revisar CPU/GPU/RAM, Demanda, Solicitud y estados en los mensajes mostrados. Los valores
   ausentes aparecen como null. Firmware no genera Solicitud. Demanda solo existe cuando
   hay una decisión automática aceptada y vigente; no se recalcula desde las curvas editadas.
7. Para probar Solicitud, elegir **Monitorización y transmisión de Solicitud** y conectar.
   Seleccionar/aplicar Manual o Automático en los controles internos habituales únicamente
   si el equipo está autorizado. Conectar el módulo externo nunca aplica esos modos.
   Sin una aplicación Manual aceptada en ese modo, Solicitud permanece null aunque el
   controlador haya conservado un nivel de Automático. El primer mensaje de la conexión
   también suprime Solicitud hasta recibir confirmación válida del dispositivo.
8. Minimizar a bandeja u ocultar con la X mantiene la transmisión. **Desconectar** detiene
   la reconexión hasta otra orden Conectar. **Deshabilitado + Guardar** no abre sockets.
   Guardar por sí solo conserva configuración y deja la comunicación desconectada.

La clave DPAPI se conserva en `%LOCALAPPDATA%\VictusFanControl\external-cooling.json`;
está ligada al usuario y equipo de Windows. No editar ese archivo para trasladar una clave:
volver a emparejar. El archivo queda fuera del directorio reemplazado por el actualizador
y no se incluye en los diagnósticos. Perfiles de ventiladores/rendimiento usan sus archivos
existentes. Actualizar el programa conserva ambos tipos de preferencias.

## Escenarios del simulador

Añadir `--fault OPCION --after 3` al comando anterior:

| Opción | Comportamiento |
|---|---|
| None | Telemetría bidireccional normal |
| Fragmented | Respuestas en fragmentos de 7 bytes |
| Disconnect | Cierra TCP al alcanzar el número de mensajes |
| Invalid | Envía JSON inválido |
| Partial | Envía medio mensaje sin LF y espera |
| Delayed | Retrasa la respuesta 6 segundos |
| Duplicate | Reenvía una respuesta anterior |
| Silence | Deja de responder |

Ctrl+C cierra el servidor. Reiniciarlo permite probar reconexión y sesiones nuevas. Una clave
distinta debe producir error de autenticación y cero telemetría enviada. Para pruebas desde
otro Windows de la LAN se puede usar `--bind DIRECCION_IP_LOCAL`; configurar esa IP en la
aplicación y desmarcar simulador local. No se cambian reglas del firewall automáticamente.

Los paquetes de protocolo rechazados cierran solo esa conexión; el servidor continúa aceptando
sesiones. En Silence consume entrada sin responder y detecta EOF para permitir otra conexión.
El diagnóstico de Windows conserva la categoría y fecha del último fallo (conexión,
autenticación, timeout, protocolo o emparejamiento), sin registrar secretos ni texto del par.

El simulador calcula un PWM ilustrativo para sus respuestas, con fallback Local=25%. Esa
política no se instala en Windows ni define el firmware futuro. Su comprobación de referencia
a una respuesta propia reciente evita aplicar solicitudes retrasadas por TCP. Las pruebas
físicas de PWM, arranque, pérdida de red, temporizador de fallback, NTC y LCD quedan para fase 2.
El estado simulado dispone además de un temporizador independiente que vuelve a Local y 25%
al caducar la Solicitud, aunque no lleguen más mensajes. No acredita el fallback físico del ESP32.

## Pruebas reproducibles

```powershell
.\scripts\test-external-cooling.ps1
```

Compila Release con advertencias como errores; ejecuta protocolo, TCP de loopback,
autenticación, fallos, reinicio del servidor, control de antigüedad, ciclo de vida WinForms,
DPAPI y regresiones de control/rendimiento/recuperación. Nunca ejecuta órdenes físicas.
Para ejecutar solo el módulo:

```powershell
dotnet tools/ExternalCooling.Tests/bin/Release/net8.0/ExternalCooling.Tests.dll
dotnet src/VictusFanControl.App/bin/Release/net8.0-windows/VictusFanControl.App.dll --external-cooling-self-test
dotnet src/VictusFanControl.App/bin/Release/net8.0-windows/VictusFanControl.App.dll --external-cooling-ui-self-test
```

Resultados, SHA, capturas y mediciones: `logs/external-cooling-self-test/`. CI sube solamente
logs, PNG y reportes expresamente enumerados; no sube los archivos temporales de emparejamiento.
La medición de 10 segundos incluye cliente y simulador en el proceso de pruebas: es una
comprobación de consumo y cadencia en localhost, no una caracterización física de Wi-Fi.

## Preparar el firmware después

Usar [el contrato v1](EXTERNAL-COOLING-PROTOCOL.md), incluidos HMAC, nonces, secuencias,
referencias a respuestas y temporizador monotónico de 2000 ms. El ESP32 no necesita reloj
UTC ni NTP para validar la edad de una solicitud. `extensions.slot1` y `slot2` se mantienen
reservados e inertes. La recepción del ESP32 es solo informativa para Windows.

Pendientes físicos: ESP32-C3, resolución mDNS en la red concreta, estabilidad del enlace LAN,
NTC, LCD, PWM real, política local segura, suspensión/hibernación real del equipo y prueba
prolongada. La suspensión/reanudación validada aquí se inyecta mediante los eventos de la
aplicación y un servidor TCP real; no se suspendió físicamente el notebook durante el desarrollo.

El código se entrega en una rama y PR para revisión. No fusionar ni publicar una release
estable sin esa revisión. El simulador y sus pruebas son herramientas de desarrollo: no son
dependencias del ejecutable ni se agregan al payload normal de la aplicación.
