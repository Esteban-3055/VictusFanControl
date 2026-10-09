# VictusFanControl v1.0.0 — HP 8C40 / 9D0R1LA / BIOS F.18

Esta entrega habilita el uso habitual solicitado para el destino exacto. La curva AC predeterminada es la candidata de menor demanda intermedia: 12, 12, 18, 28, 34, 44, 50 para entradas 0, 40, 50, 65, 76, 90, 100. Conserva las protecciones térmicas crudas, subida e histéresis; descenso normal 25 s/confirmación 12 s y descenso corto 10 s/confirmación 4 s. Batería conserva su curva y tiempos de descenso. La transición usa el mismo motor, cambia los tiempos compatibles del perfil de destino y reinicia las confirmaciones, conservando el nivel aplicado y el filtro; los rangos y pasos protegidos no pueden cambiar en vivo. Se migran solamente los presets AC anteriores intactos; se conservan curvas personalizadas y límites CPU/GPU. Guardar crea un respaldo exacto previo a v1.0.

## Instalar e iniciar con Windows

1. Sal de las aplicaciones Victus anteriores desde su bandeja. Extrae el ZIP completo.
2. Abre PowerShell **como administrador con tu misma cuenta**, entra en la carpeta extraída y ejecuta `./Start-ProductGui.ps1 -Mode Install`.
3. El instalador verifica hashes, copia la entrega a `%LOCALAPPDATA%/VictusFanControl/releases/1.0.0-{commit}`, registra una tarea elevada para tu inicio de sesión y guarda **minimizado + Automático al iniciar**. Abre la aplicación al finalizar. Requiere el controlador PawnIO y NVIDIA/NVML que ya usa la instalación anterior; .NET Desktop Runtime 8 x64.

La instalación conserva tus perfiles CPU/GPU y sus journals. No cierra procesos a la fuerza ni restaura registros pendientes. Se puede verificar sin activar hardware con `./Start-ProductGui.ps1 -Mode FinalCheck`.

En **Configuración** puedes desactivar “Iniciar con Windows” inmediatamente y editar “Activar Automático al iniciar” o “Minimizar al iniciar”; pulsa **Guardar preferencias** para persistir estas últimas opciones. Abrir la GUI con la opción guardada también solicita Automático. La tarea espera 10 s tras iniciar sesión, utiliza el usuario interactivo y admite funcionamiento en batería.

## Activación y límites

El arranque comienza en Firmware y hace un único intento: hasta 30 s para tres adquisiciones completas, frescas y continuas, en una fuente estable. CPU ≤90 °C, GPU ≤82 °C y admisión válida. Después usa el mismo camino que el botón Automático: confirma ambos límites CPU/GPU con Guardian antes de permitir comandos de ventiladores. No borra ni recupera automáticamente journals. Una preparación fallida intenta liberar ambos dominios y muestra el error; no reintenta.

Automático habitual no tiene el plazo de 5/45 minutos de las revisiones. Conserva temperatura CPU ≥95 °C con confirmación máxima de 2000 ms, CPU ≥99 °C inmediata, GPU >82 °C, frescura de 3 s y el watchdog. Suspensión, pantalla apagada, pérdida de sensores o interrupción solicitan Firmware y bloquean reentrada. **Reiniciar sesión** abre una sesión nueva en Firmware y omite la activación al iniciar, incluso si está guardada. La reactivación tras una interrupción es manual. Cerrar la ventana minimiza; **Salir** en la bandeja libera los controles.

TZ01/DTT3 permanece experimental y desactivado de fábrica. El desarrollo posterior a la release permite activarlo en AC o batería y mantenerlo durante el cambio de perfil: la extensión no evalúa la alimentación ni reinicia su episodio al cambiarla. Conserva +2 niveles crudos durante hasta 60 s y el último nivel reconocido; falta de frescura/continuidad interrumpe la sesión. El controlador principal sigue confirmando los límites CPU/GPU de destino y conserva las protecciones térmicas. Activarlo agrega dependencia de esas lecturas y puede prolongar el ruido. El comportamiento físico en batería y en el cambio de fuente con retención aún necesita observación. El ZIP publicado v1.0.0 conserva la restricción original a AC.

## Qué acredita la entrega

Las pruebas de software y replay verifican contratos, migración, arranque, cancelación, emergencia y recuperación. Las evidencias físicas previas conservan su identidad histórica. El replay de 390 decisiones produjo menos demanda y menos cambios de nivel, pero **no demuestra menos dBA ni temperaturas equivalentes con la curva nueva**. Tampoco una aceptación NVML demuestra lectura exacta del rango de clocks, ni WMI propiedad independiente del firmware. Las observaciones restantes del arranque real, uso representativo, suspensión y salida de esta compilación se conservan en `PRODUCT-RELEASE.json`; no se convierten en PASS por cambiar la versión.

Compilar la entrega: `dotnet publish src/VictusFanControl.App -c Release -r win-x64 -p:VictusProductRelease=true`. El pipeline conserva el versionado original del arnés P14 por separado y publica todos los binarios Victus de esta entrega con versión 1.0.0.0.
