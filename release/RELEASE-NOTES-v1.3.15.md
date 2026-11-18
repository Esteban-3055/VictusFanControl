# VictusFanControl 1.3.15 preliminar

Incorpora cTGP optativo en Rendimiento → cTGP, entre GPU NVML y Fuente de energía. Desactivado por defecto. Selecciona la opción en Firmware y después inicia Automático; Guardar conserva la preferencia. CPU RAPL y GPU NVML siguen siendo independientes.

- cTGP se activa únicamente con Automático y alimentación AC en la configuración calificada: HP 8C40 / BIOS F.18, DSDT auditada, RTX 4060 Laptop subsystem 8C40, VBIOS 95.07.2B.00.54, controlador 621.01 y cargador HP reconocido de 200 W. Otra configuración se rechaza. No introduce un valor de vatios: el máximo de VBIOS continúa siendo 75 W.
- El guardián separado registra la intención antes de escribir, verifica la política y restaura los bytes originales con dos lecturas al salir de Automático, desconectar AC o terminar la sesión. Una respuesta incierta conserva la recuperación pendiente. Si otra política HP cambia cTGP, no se fuerza su reactivación ni se reanuda Automático sin intervención.
- Manual y Automático admiten ahora consignas CPU/GPU de nivel 54/54, equivalentes nominalmente a 5400 RPM para ambos. Se conservan curvas y preferencias existentes. Para solicitar ese máximo en Automático, ajusta el último punto de tu curva a 54. La velocidad física sigue dependiendo del firmware y de los ventiladores.

Las comprobaciones locales de software y el ensayo del guardián usan transportes simulados y dibujos de la interfaz en segundo plano. La calificación de identidad se comprueba mediante lecturas. El ensayo físico previo de tres minutos observó aproximadamente 75 W con cTGP y restauración posterior; no demuestra mejora de FPS ni estabilidad prolongada. La integración completa y la nueva consigna GPU 54 requieren verificación física posterior. No se ha flasheado VBIOS, modificado hardware ni instalado esta versión durante su preparación.

Versión preliminar optativa: activar «Incluir versiones preliminares» en el actualizador. No sustituye la última versión estable. El paquete conserva las funciones anteriores de HyperX y de la base externa sin cambiar su firmware.
