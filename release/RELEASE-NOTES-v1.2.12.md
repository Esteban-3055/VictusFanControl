# VictusFanControl v1.2.12 · Preliminar 4 — Recuperación y ESP32-C3

## Cambios

- Las lecturas NVIDIA NVML de la telemetría del producto se ejecutan en un proceso auxiliar. Si una lectura nativa falla, el fallo queda contenido y se invalida esa muestra; no se sustituye por cero ni se cambia la autoridad de los ventiladores.
- Se corrige un bucle de recuperación: el tiempo empleado en recuperar sensores ya no se interpreta como una nueva interrupción del bucle. Se siguen detectando las interrupciones posteriores reales.
- Se conservan el diseño integrado de Base externa, el emparejamiento protegido con DPAPI y el protocolo TCP bidireccional.
- Se incorpora el código inicial del ESP32-C3 SuperMini: autenticación compatible, LCD I²C, NTC ambiente y salida PWM invertida para una etapa NOT. El perfil predeterminado no activa la salida PWM; el perfil de banco requiere habilitación explícita.
- Las pruebas comprueban la comunicación del cliente Windows con el mismo motor C++ del firmware, además del simulador independiente.

## Actualizar desde 1.2.9, 1.2.10 o 1.2.11

1. En **Actualizaciones**, activar **Incluir versiones preliminares**.
2. Ir a **Configuración → Guardar preferencias**.
3. Volver a **Actualizaciones → Buscar actualizaciones → Descargar → Instalar**.

Etiqueta numérica **v1.2.12**, aplicación e instalador **1.2.12**. Es una **Pre-release**; la estable v1.2.9 permanece en el canal estable. Se conservan las preferencias y el emparejamiento. La aplicación requiere .NET Desktop Runtime 8 x64.

El simulador separado **Esp32Simulator-v1.2.12-win-x64.zip** requiere .NET 8 Runtime x64. No forma parte del ejecutable normal ni sustituye una prueba física.

## Código desde otro dispositivo

- [Código Windows de esta versión](https://github.com/Esteban-3055/VictusFanControl/tree/v1.2.12/src).
- [Firmware ESP32-C3, conexiones y compilación](https://github.com/Esteban-3055/VictusFanControl/tree/v1.2.12/firmware/esp32-c3).
- [Descargar todo el código de esta versión](https://github.com/Esteban-3055/VictusFanControl/archive/refs/tags/v1.2.12.zip).

## Alcance de la validación

Compilaciones y pruebas automáticas sin escrituras físicas al notebook. El aislamiento contiene fallos nativos de lectura, pero no demuestra la causa exacta de la excepción original de NVML. No se cambian límites, curvas, HP WMI/EC, SafetyGate ni Performance Guardian.

Pendiente comprobar en el equipo real el inicio y la secuencia **batería → suspensión → reanudación con AC**, y confirmar que no reaparece el cierre inesperado. El firmware todavía no está validado físicamente: revisar la placa V1601, la etapa NOT y su estado al arrancar, la frecuencia adecuada del ventilador, la NTC y los niveles eléctricos del LCD antes de utilizarlo con hardware. Los detalles y las pruebas pendientes están en el README del firmware y en `docs/NVML-CRASH-AND-BATTERY-RECOVERY.md`.
