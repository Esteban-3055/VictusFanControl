# VictusFanControl v1.2.4

[English](README.md) · [Estable v1.2.4](https://github.com/Esteban-3055/VictusFanControl/releases/tag/v1.2.4) · [Instalación y uso](docs/PRODUCT_V1.md) · [Estado de validación](docs/PRODUCT_VALIDATION_STATUS.md)

Control de ventiladores y preferencias CPU/GPU para Windows, limitado al destino exacto **HP 8C40 / 9D0R1LA / BIOS F.18**: HP Victus 15-fa1xxx, placa revisión 63.43, Intel Core i7-13700H y NVIDIA RTX 4060 Laptop GPU. v1.0.0 habilita Automático habitual para este equipo. Otros modelos o BIOS quedan fuera de esta autorización; HP 88F8 se conserva como histórico.

## Instalar

Descarga el instalador **VictusFanControl-1.2.4-Setup-win-x64.exe** desde la release estable y ejecútalo. También puedes usar el ZIP y PowerShell como se describe a continuación.

Requiere Windows 11 x64, .NET Desktop Runtime 8 x64, controlador PawnIO y NVIDIA/NVML. El ZIP incluye los módulos PawnIO de la aplicación; no instala estos requisitos.

1. Descarga **VictusFanControl-1.2.4-win-x64.zip** desde Releases y extrae todos los archivos. Sal de las aplicaciones Victus anteriores desde la bandeja.
2. Abre PowerShell **como administrador con tu misma cuenta de Windows**, en la carpeta extraída.
3. Ejecuta:

```powershell
Set-ExecutionPolicy -Scope Process -ExecutionPolicy Bypass -Force
.\Start-ProductGui.ps1 -Mode Install
```

`Install` verifica hashes, copia la versión bajo `%LOCALAPPDATA%\VictusFanControl\releases`, registra la tarea elevada de inicio de sesión, guarda **minimizar + activar Automático al iniciar** y abre la aplicación. Conserva curvas personalizadas y límites CPU/GPU. Los registros pendientes de recuperación bloquean la activación y se conservan.

`-Mode Verify` y `-Mode FinalCheck` comprueban el paquete sin comandos de hardware. `-Mode Open` abre sin registrar una tarea; se aplica Automático al iniciar si ya estaba guardado. En Configuración puedes desactivar el inicio con Windows y guardar las otras preferencias. Cerrar la ventana minimiza; **Salir** en la bandeja libera los controles.

## Funciones de v1.0.0

- GUI de siete páginas, perfiles AC/Batería, curva única, seis influencias de temperatura/potencia/carga, simulador, ajustes en vivo y diagnósticos por sesión.
- Curva AC predeterminada nueva: niveles **12, 12, 18, 28, 34, 44, 50** para demanda **0, 40, 50, 65, 76, 90, 100**. Batería conserva sus valores. Solo se migran presets AC anteriores intactos; guardar respalda las preferencias.
- Automático habitual sin vencimiento de 5/45 minutos, niveles iguales CPU/GPU dentro de 10–50, y ambos límites preparados mediante Guardian antes de activar ventiladores.
- Inicio opcional con Windows y un único intento tras tres lecturas distintas y frescas. Suspensión, pantalla apagada e interrupciones requieren reactivación manual; reiniciar sesión abre en Firmware.
- Retención TZ01/DTT3 opcional y experimental, desactivada de fábrica: el desarrollo actual admite AC y batería, hasta dos niveles crudos extra durante 60 s, sin superar el último nivel reconocido. El cambio de fuente conserva el episodio; los fallos de sensores siguen solicitando Firmware. Puede prolongar el ruido; el comportamiento en batería requiere observación física. El paquete publicado v1.0.0 conserva su restricción original a AC.
- Preflight de recuperación CPU/GPU que conserva journals y ofrece recuperación explícita con los IDs exactos.

## Protecciones y evidencia

Automático comienza en Firmware. La admisión requiere telemetría completa y fresca, fuente estable, CPU ≤90 °C y GPU ≤82 °C. En control, CPU ≥95 °C tiene hasta 2000 ms de confirmación; CPU ≥99 °C solicita Firmware inmediatamente. GPU >82 °C, CPU >60 W, datos caducados o fallos de lifecycle interrumpen. Se mantienen el watchdog y la transición acotada AC/Batería.

El producto usa comandos HP WMI acotados y watchdog, lecturas Intel/ACPI mediante PawnIO, NVIDIA NVML y Performance Guardian. La aceptación WMI **no demuestra propiedad independiente del firmware ni readback exacto del nivel**. La aceptación NVML **no verifica el rango exacto de clocks GPU**.

Están verificadas las pruebas de software Windows/Linux, fixtures del paquete y replay. Las pruebas físicas anteriores conservan su alcance por compilación y configuración. Menor demanda simulada **no demuestra menos dBA ni enfriamiento equivalente**. Uso representativo, suspensión/salida/inicio de sesión de esta compilación y comparación térmica/acústica siguen en `PRODUCT-RELEASE.json`. Consulta [la evidencia vigente y sus límites](docs/PRODUCT_VALIDATION_STATUS.md).

## Desarrollo y publicación

Con .NET 8 SDK, usa la propiedad explícita de entrega:

```powershell
dotnet publish src/VictusFanControl.App -c Release -r win-x64 -p:VictusProductRelease=true
```

El pipeline empaqueta todos los binarios del producto como **1.2.4.0**. Una compilación correcta de `main` o de la rama reconstruida del producto puede publicar v1.2.4 tras pasar las verificaciones OEM, WMI y CPU/GPU del mismo commit. Se comprueba cada archivo contra el manifiesto y se adjuntan SHA-256 y procedencia. No se sobrescribe una release existente.

Los arneses históricos conservan versión RC y gates originales: `control.enabledByDefault=false` y `automaticPolicyEnabled=false`. Esos registros describen la cualificación antigua; el producto tiene autorización separada para este destino exacto. Los planes anteriores se mantienen como historial.

Licencia MIT: [LICENSE](LICENSE). Componentes externos: [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).

## Instalador y actualizaciones v1.1.0

La versión estable v1.2.4 separa **Descargar** de **Instalar** y conserva el instalador verificado entre aperturas y cuando falla la instalación. Actualizaciones permite recuperar sesiones pendientes antes de volver a pulsar Instalar. El instalador conserva por defecto las preferencias de inicio anteriores. El canal estable es el predeterminado; guarda **Incluir versiones preliminares** para recibir previews. [Guía](docs/PRODUCT_V1.md) · [Revisión funcional](docs/PRODUCT_1_1_5_REVIEW.md).

La v1.1.6 elimina el corte por consumo GPU y añade causas concretas de espera/rechazo de Automático. El consumo GPU sigue participando en la curva; se mantienen los cortes CPU y térmicos seleccionados. [Cambios y pruebas](docs/PRODUCT_1_1_6_CHANGES.md).

La v1.1.7 adapta la ventana a los cambios de resolución y DPI, incluido 4K → 1440p → 4K, y conserva el tamaño elegido al volver. El diagnóstico incluye la geometría de pantalla. [Ajuste y verificación](docs/PRODUCT_1_1_7_DISPLAY.md).

La v1.1.8 filtra subidas breves de temperatura CPU antes de calcular la demanda: una subida debe mantenerse dos segundos; ≥99 °C omite el filtro. Los sensores crudos y las protecciones siguen independientes. Temperatura GPU, potencia y carga conservan su comportamiento. [Alcance y comprobaciones](docs/PRODUCT_1_1_8_CPU_SPIKES.md).

Desde v1.1.9, las sesiones pendientes pueden recuperarse automáticamente en segundo plano, también al iniciar y estando minimizada. Configuración permite desactivarlo. La reanudación conserva el perfil aplicado y exige lecturas nuevas; Firmware solicitado por el usuario la cancela. Se añaden diagnósticos CPU y del aporte de TZ01/DTT3 sin cambiar su comportamiento térmico. [Detalle](docs/AUTOMATIC_SESSION_RECOVERY.md).

La v1.2.0 renueva Inicio con dos ventiladores animados, temperaturas/uso, la curva aplicada y la demanda del controlador en tiempo real. Ventiladores muestra la curva con el mismo tamaño del editor. Las pestañas siguen el nuevo orden solicitado y la animación se pausa fuera de Inicio, con ventana oculta/minimizada o lecturas vencidas. [Interfaz y verificación](docs/HOME_DASHBOARD.md).

Desde v1.2.1, los ventiladores de Inicio giran diez veces más despacio, con inercia independiente al acelerar/desacelerar y arranque suave. La cifra de RPM conserva la lectura actual. Cero RPM termina gradualmente el giro residual; datos vencidos o ventana oculta/minimizada pausan de inmediato.

Desde v1.2.2, ambas animaciones de Inicio siguen la demanda aceptada del controlador en lugar de las RPM. Una lectura de RPM retrasada o temporalmente ausente ya no reinicia la inercia visual; las cifras centrales conservan las RPM informadas. Firmware/Manual o demanda vencida pausan la animación.

En v1.2.3, los ventiladores de Inicio giran tres veces más rápido conservando el movimiento por demanda y la inercia: una vuelta visual cada 5 segundos a 100 % de demanda y cada 10 segundos a 50 %, una vez estabilizados. Las RPM informadas y el control físico conservan su comportamiento.

La v1.2.4 anima Inicio según la solicitud aceptada (Solicitud × 100 RPM simuladas), con una vuelta visual cada 0,5 segundos a solicitud 30 una vez estabilizada la inercia. Una aspa marcada permite distinguir el movimiento. Los valores actuales conservan por separado las últimas RPM válidas informadas de cada ventilador durante los retrasos y las identifican como última lectura; antes de la primera muestran Esperando. Los gráficos de RPM y el control físico conservan su comportamiento. [Detalles](docs/HOME_DASHBOARD.md).
