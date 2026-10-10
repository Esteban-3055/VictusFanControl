# VictusFanControl v1.1.0 — instalador y actualizaciones

Para HP 8C40 / BIOS F.18. Incluye la curva silenciosa, retención experimental opcional TZ01/DTT3 disponible en AC y batería, y cambio de perfiles AC/Batería con las protecciones existentes.

- **Instalador EXE:** descarga `VictusFanControl-1.1.0-Setup-win-x64.exe` y ejecútalo con tu misma cuenta de Windows. Cierra antes la GUI desde la bandeja. Instala una versión independiente y crea un acceso en el menú Inicio. Conserva perfiles, registros y opciones de inicio. En una primera instalación activa inicio con Windows, minimizado y Automático.
- **Actualizar desde el programa:** Actualizaciones → Buscar actualizaciones. Consulta releases estables del repositorio, rechaza regresiones y verifica tamaño y SHA-256 de GitHub. Descarga antes de cerrar la sesión; libera ventiladores/CPU/GPU y rechaza recuperación pendiente. El instalador espera el cierre normal del proceso anterior.
- **Reintento manual:** después de una interrupción, Automático permite preparar control de nuevo en la misma GUI; requiere liberación completa, ausencia de recuperación pendiente y tres lecturas frescas. Conserva perfiles y borrador. No hay reintento automático ni bypass de límites térmicos.
- **Automático con Windows:** casilla marcada en el instalador, también para instalaciones anteriores. Espera tres lecturas frescas y aplica CPU/GPU antes de adquirir ventiladores.
- **Página Actualizaciones:** acceso lateral, versión instalada, comprobación separada de la descarga/instalación, estado persistente y progreso.
- **Iconos definitivos:** programa/instalador/menú Inicio y bandeja normal, Automático activo y error. El error se conserva hasta reactivar correctamente.
- **Recuperación guiada:** el instalador ofrece recuperar CPU/GPU antes de continuar. La GUI incorpora Recuperar sesiones en Rendimiento → Guardián y Configuración. Guarda respaldos, valida los identificadores y libera sin reactivar Automático; la GUI conserva el borrador y vuelve a Firmware. No requiere comandos de PowerShell y admite registros de un solo dominio. Otros controladores abiertos, registros inválidos y liberaciones no confirmadas siguen bloqueando la operación.
- **ZIP portátil:** sigue disponible, con manifiesto, SHA-256 y procedencia. No se modifica la release v1.0.0.

El instalador incluye su runtime; el programa requiere .NET Desktop Runtime 8 x64 y PawnIO ya instalado. El EXE aún no tiene firma Authenticode: Windows puede mostrar la identificación de editor desconocido. Las actualizaciones requieren una release pública con su instalador y digest SHA-256; los artefactos de Actions no forman parte del canal estable.

La instalación normal y la consulta de actualizaciones no activan control de hardware. Si hay registros pendientes, la recuperación confirmada puede restaurar límites CPU aún propios y solicitar el reset de GPU; no modifica ventiladores ni aplica perfiles. Abrir la aplicación después de instalar respeta tus preferencias guardadas y la preparación habitual de Automático. Se conservan journals si una liberación falla. No se fuerza el cierre de procesos ni se restaura una versión antigua mientras controla el hardware.

La compatibilidad de retención con batería tiene regresiones de software; la observación física AC→Batería→AC sigue pendiente. Las comprobaciones físicas de la versión anterior siguen documentadas en PRODUCT-RELEASE.json; no se presentan las pruebas de software como validación térmica nueva.


La recuperación ahora incluye ventiladores WMI, ensayos, watchdog anterior del mismo equipo y marcas de consultas huérfanas. El instalador y la GUI comparten inventario, respaldos, identidad exacta y límites de liberación. La apertura con pendientes muestra recuperación sin iniciar telemetría/controladores. Una llamada incierta requiere Reiniciar Windows; registros dañados, de otro equipo o sin propiedad se conservan para diagnóstico. El diagnóstico exporta copias limitadas de los registros.
