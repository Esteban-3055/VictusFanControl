# Reiniciar sesión

El botón **Reiniciar sesión** está en Configuración → Registros y estado,
en Ventiladores cuando la sesión está interrumpida y en el menú de la bandeja.
Sirve también durante Manual, Automático o la preparación de CPU/GPU.

1. Bloquea nuevas operaciones y cancela la entrada pendiente a Automático.
2. Conserva un diagnóstico del estado previo en
   `%LOCALAPPDATA%\VictusFanControl\logs\sessions\<sesión>\diagnostic-before-restart.zip`.
3. Espera la construcción del runtime, los comandos y las liberaciones de lifecycle
   ya pendientes. Ejecuta una sola vez la salida normal de ventiladores, Guardian
   CPU/GPU y telemetría. Cada dominio intenta su limpieza aunque otro falle.
4. Comprueba que no queden los journals CPU/GPU ni las leases WMI de las rutas
   existentes. No elimina, modifica ni recupera a ciegas esos registros.
5. Después de una liberación completa, escribe `restart-state.json` en la carpeta
   anterior. Conserva por separado el borrador, la última preferencia guardada,
   el indicador de cambios y la pestaña/perfil editado. No guarda el borrador como
   preferencia permanente. El recibo está ligado a los MVID de esta compilación.
6. Cierra la GUI y retira el mutex del propietario. Abre el mismo ejecutable con
   los argumentos mínimos: módulos, recibo y, si existía, revisión explícita de
   cinco o 45 minutos. No hereda parámetros de cualificación arbitrarios.

La nueva apertura tiene otro PID, identificador y carpeta de diagnóstico.
Se muestra en primer plano aunque se prefiera iniciar minimizado.
Empieza en **Firmware, sin límites CPU/GPU aplicados**; no transfiere autoridad,
inercia, muestras antiguas, plazo ni estados de los Guardians. Para continuar,
hay que seleccionar Automático o Aplicar límites explícitamente, después de
recuperar telemetría y cumplir la admisión habitual. El gate normal sigue cerrado.
Los registros anteriores permanecen disponibles en Abrir carpeta de logs.

Si falla la preparación, la liberación o la comprobación de recuperación,
no abre otro controlador. Muestra el error, mantiene el borrador y deshabilita
otro intento en ese runtime parcialmente cerrado. Salir desde la bandeja sigue
disponible y no repite esa limpieza fallida. Las leases/journals se conservan
para la recuperación explícita existente. Si falla abrir el ejecutable después
de una salida limpia, el recibo y el diagnóstico quedan en la sesión anterior.

Salir durante el reinicio tiene prioridad: espera la limpieza y no relanza.
Suspender/apagar la pantalla durante la operación cancela el relanzamiento;
no hace Resume ni Release contra un servicio que se está disponiendo.
Un reinicio no evita una temperatura crítica, un controlador externo, un sensor
no disponible ni un defecto repetible. La nueva sesión vuelve a evaluarlos.

Las pruebas de software cubren comandos pendientes, doble clic, cierre durante
startup/reinicio, disposición fallida, registros conservados, estado incompleto,
draft/discard, rutas literales, revisión conservada y un proceso hijo real con
runtime de prueba sin hardware. La regresión física de salida/reapertura incluye
ahora este botón y sigue pendiente en la candidata final.
