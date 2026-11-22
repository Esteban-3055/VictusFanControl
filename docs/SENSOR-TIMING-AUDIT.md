# Tiempos de observación, validez y recuperación

Revisión del código de desarrollo el 21 de noviembre de 2026, basada en 1.14.1. Estos son contratos del programa, no duraciones medidas de cada operación en este PC. Las constantes de pruebas históricas pueden diferir de las del producto habitual. Esta revisión no cambia los tiempos de la versión instalada.

No existe un solo «tiempo antes del fallo». Se distinguen la frecuencia de consulta, el presupuesto de una operación, la antigüedad máxima del dato, el filtro de control y la espera para recuperar. Sumar esos valores no concede un plazo extra al controlador. El paso del tiempo tampoco prueba la terminación de una llamada nativa.

## Variables del PC

| Variable o servicio | Lectura / operación | Validez y reacción |
|---|---|---|
| Temperatura CPU de paquete y núcleos | Bucle normal: pausa configurable de 500–1500 ms, predeterminada 1000 ms, además del tiempo de adquisición. | En producto habitual, cada adquisición térmica conserva su fecha y caduca a los 5 s, sin incluir el límite. Un campo ausente puede conservarse menos de 5 s; una lectura nueva más caliente no se oculta. Los núcleos duplicados o imposibles no usan esa tolerancia. |
| Temperatura GPU | Bucle general; el proceso lector NVML tiene presupuesto de muestra de 1500 ms. | Menos de 5 s en producto habitual; solo se conserva un valor anterior cuando falta la nueva lectura y aún es reciente. No se convierte una temperatura imposible en un dato válido. |
| Potencia CPU | Lectura de energía y cálculo del intervalo en el bucle general. | Menos de 5 s desde su adquisición. Los campos ausentes pueden conservarse dentro de ese plazo, sin cambiar la fecha. |
| Potencia GPU | Proceso lector NVML, con el mismo presupuesto de muestra. | Menos de 5 s desde su adquisición; conservación limitada del campo ausente. |
| Uso CPU y GPU | Se obtiene en el ciclo de telemetría. | Usa la fecha del snapshot; no tiene el mecanismo de conservación de temperaturas/potencias. La exportación publica ausencia cuando el snapshot deja de estar vigente. |
| RPM y nivel observado CPU/GPU por HP WMI | Consulta periódica nominal cada 1000 ms. Presupuesto lógico habitual de consulta: 10000 ms; espera posterior a fallo: 5000 ms. | En uso habitual se aceptan RPM de menos de 10000 ms desde el inicio de adquisición. Para entrar o reentrar en Automático se exige menos de 3000 ms. Una consulta pendiente incierta no permite lanzar otra. |
| Lector NVML aislado | Inicio: hasta 5000 ms; muestra: 1500 ms; nuevo intento tras fallo: 5000 ms. | Un proceso que supera su presupuesto deja de producir una muestra válida. Esto no convierte su dato anterior en reciente. Las escrituras de límites GPU pertenecen a otro propietario. |
| cTGP: observación de política y cargador | Observación nominal cada 2 s; presupuesto por observación: 5 s. Espera normal del acceso WMI compartido: 2 s. | La confirmación original caduca a los 5 s. En desarrollo, una lectura no enviada por acceso ocupado se reintenta tras 250 ms y conserva la fecha original. Una operación enviada de resultado incierto sigue bloqueando nuevas órdenes hasta confirmar su finalización. |
| Fuente AC/Batería del PC | Consulta directa de Windows y confirmación del guardián; una notificación solo despierta la comprobación. | Unknown no se transforma en AC o batería. El cambio de perfil de Automático tiene presupuesto global de 8 s, al menos dos observaciones y 1 s de estabilidad, con una respuesta nueva del guardián. |
| Estado del guardián CPU/GPU | Consulta de estado: presupuesto de 5 s. Estado considerado reciente durante menos de 6 s y solo si su proceso sigue vivo. | Actualización de límites: hasta 5 s para admitir el transporte y 4 s para la respuesta de actualización. Se conserva lo confirmado por dominio. Una respuesta perdida cancela la reanudación desatendida en el código corregido. |
| Batería HyperX | Consulta USB nominal cada 60 s, más el tiempo de la operación. Presupuesto de consulta: 2 s. | Lectura en vivo durante menos de 180 s. Sin respuesta no se publica un cero nuevo. El último porcentaje puede conservarse para la LCD con su fecha original separada y sin crear una adquisición ni una alarma nuevas. |
| Silencio y encendido HyperX | Lectura de eventos HID bloqueante y cancelable, independiente del ciclo de batería. | Una notificación de silencio exige evento nuevo y de menos de 5 s. Esto es validez del aviso; no significa que se consulte el silencio cada 5 s. Cambiar de receptor invalida la generación de eventos anterior. |
| RAM para LCD | Consulta de Windows al capturar la exportación. | Si la consulta falla, se exporta ausencia; no usa el último valor para justificar control. |
| Curva, influencias, PL1/PL2 y límites GPU | Preferencias, no sensores periódicos. | Un borrador guardado no tiene caducidad ni certifica aplicación. La sesión debe confirmar configuración, fuente y propietario. |

El modo histórico de calificación usa un contrato de snapshot de 3 s y RPM de 3 s, distinto del margen habitual de 5/10 s. No se deben sustituir sin más todas las constantes que contienen «3» o «5».

Existe además un detector general de progreso: revisa cada 500 ms y considera vencida la adquisición global tras 5 s sin completar una lectura en el producto habitual (`ProductTelemetryTolerance`); el contrato histórico de calificación conserva 3 s. Una lectura retenida no renueva ese progreso ni su fecha de adquisición. La nueva separación aísla la indisponibilidad de un canal de los servicios que no lo requieren, conservando sus fechas reales.

## Base externa y LCD

| Variable o servicio | Tiempo del código actual | Consecuencia |
|---|---|---|
| Envío PC → base | Un envío por segundo; no se acumulan snapshots pendientes. | Se captura el estado más nuevo al enviar. |
| Conexión TCP y lectura de trama | Hasta 5 s para conectar y 5 s por lectura de trama. | Fallo de enlace y nuevo intento con espera creciente de aproximadamente 0,5–30 s más variación. |
| Vigencia de solicitud remota | 2000 ms; el PC retira la solicitud si no tiene estado reciente del dispositivo dentro de ese plazo. | El firmware deja el control remoto al caducar la solicitud. El retorno local no equivale a apagar la base: su salida local es 130/255. |
| Antigüedad máxima de fuente en el protocolo | 5000 ms. | Es un límite adicional; no sustituye los requisitos actuales del exportador de Automático/Manual. |
| Confirmación de una trama enviada | Se rechaza una confirmación con más de 5 s. | Una confirmación antigua no valida una solicitud actual. |
| NTC ambiente del ESP32 | Promedio de 40 muestras convertidas, separadas aproximadamente 100 ms; necesita llenar la ventana de unos 4 s. Caducidad de muestra: menos de 3 s. | Un hueco de adquisición de 3 s o lectura imposible reinicia la ventana. No se usa el promedio antiguo para ocultar un sensor inválido. Es informativo en el controlador externo actual. |
| Página LCD de temperaturas | 30 s según la presentación del firmware. | Duración de página, no caducidad del sensor. Las páginas de uso, demanda y conexión duran 5 s. |
| Filtro y descenso de la curva externa | Parámetros guardados de filtro, histéresis y espera de bajada. | Son respuesta del actuador; no prolongan la vigencia de la solicitud remota. |

Estos valores del ESP32 se obtuvieron leyendo el firmware del repositorio; no se verificó qué binario está grabado en la placa ni se la actualizó en esta etapa.

## Plazos que deben conservar su significado

La respuesta térmica CPU no es una tolerancia a un sensor perdido: con la cesión activada, una lectura de 95 °C o superior inicia confirmación de hasta 2 s; 99 °C solicita Firmware inmediatamente. La cesión habitual GPU, cuando está activada, solicita Firmware por encima de 95 °C. Desactivar una cesión del software no modifica las protecciones térmicas del firmware.

La reanudación automática tiene esperas de 10, 30 y 60 s, hasta tres intentos por episodio. Para volver a preparar el control requiere 30 s de adquisiciones nuevas estables. Tras agotar intentos, la recuperación adicional exige enfriamiento temporal de 5 min y estabilidad; 10 min de Automático estable permiten reiniciar el episodio. No son diez minutos permitidos de telemetría antigua.

El enfriamiento de apagado admitido conserva su fase de 20 s. Ese tiempo pertenece al ciclo de apagado, con sus comprobaciones y liberación posteriores; no es una ampliación de los sensores ni una garantía de que Windows mantenga el proceso si fuerza el cierre.

Para tolerar mejor errores transitorios, la nueva versión debe conservar la solicitud de cada función, mostrar «en espera» cuando no se envió una operación, reintentar dentro de presupuestos conocidos y recuperar solo el recurso afectado. Las lecturas caducadas y las operaciones inciertas seguirán indicándose como tales.

## Referencias de implementación

`TelemetrySnapshot`, `ProductTelemetryContinuity`, `TelemetryWorker`, `HpWmiFanTelemetryReader`, `NvmlTelemetryProcessClient`, `WmiFanExperimentBoundary`, `WmiFanGuiGuardianHost`, `PerformanceGuardianClient`, `ProductAutomaticSourceTransition`, `ProductAutomaticReview`, `ProductRecoveryEpisode`, `ProductStartupAutomatic`, `HyperXMonitor`, `HyperXSnapshot`, `HyperXNotificationPolicy`, `TelemetryExportService`, `Esp32TcpClient`, `ExternalCoolingProtocol`, y en firmware `NtcSensor.h`, `LcdPresentation.h`, `ExternalController.cpp`.
