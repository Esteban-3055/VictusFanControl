# VictusFanControl v1.1.0 — instalador y actualizaciones

Para HP 8C40 / BIOS F.18. Incluye la curva silenciosa, retención experimental opcional TZ01/DTT3 disponible en AC y batería, y cambio de perfiles AC/Batería con las protecciones existentes.

- **Instalador EXE:** descarga `VictusFanControl-1.1.0-Setup-win-x64.exe` y ejecútalo con tu misma cuenta de Windows. Cierra antes la GUI desde la bandeja. Instala una versión independiente y crea un acceso en el menú Inicio. Conserva perfiles, registros y opciones de inicio. En una primera instalación activa inicio con Windows, minimizado y Automático.
- **Actualizar desde el programa:** Actualizaciones → Buscar actualizaciones. Consulta releases estables del repositorio, rechaza regresiones y verifica tamaño y SHA-256 de GitHub. Descarga antes de cerrar la sesión; libera ventiladores/CPU/GPU y rechaza recuperación pendiente. El instalador espera el cierre normal del proceso anterior.
- **Reintento manual:** después de una interrupción, Automático permite preparar control de nuevo en la misma GUI; requiere liberación completa, ausencia de recuperación pendiente y tres lecturas frescas. Conserva perfiles y borrador. No hay reintento automático ni bypass de límites térmicos.
- **Página Actualizaciones:** acceso lateral, versión instalada, comprobación separada de la descarga/instalación, estado persistente y progreso.
- **Iconos definitivos:** programa/instalador/menú Inicio y bandeja normal, Automático activo y error. El error se conserva hasta reactivar correctamente.
- **ZIP portátil:** sigue disponible, con manifiesto, SHA-256 y procedencia. No se modifica la release v1.0.0.

El instalador incluye su runtime; el programa requiere .NET Desktop Runtime 8 x64 y PawnIO ya instalado. El EXE aún no tiene firma Authenticode: Windows puede mostrar la identificación de editor desconocido. Las actualizaciones requieren una release pública con su instalador y digest SHA-256; los artefactos de Actions no forman parte del canal estable.

Instalar y consultar actualizaciones no activan control de hardware. Abrir la aplicación después de instalar respeta tus preferencias guardadas y la preparación habitual de Automático. Se conservan journals si una liberación falla. No se fuerza el cierre de procesos ni se restaura una versión antigua mientras controla el hardware.

La compatibilidad de retención con batería tiene regresiones de software; la observación física AC→Batería→AC sigue pendiente. Las comprobaciones físicas de la versión anterior siguen documentadas en PRODUCT-RELEASE.json; no se presentan las pruebas de software como validación térmica nueva.
