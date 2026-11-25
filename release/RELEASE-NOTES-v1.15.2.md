# VictusFanControl 1.15.2 preliminar

Los fallos de una función pueden recuperarse sin retirar las sesiones sanas de
las demás. Los enlaces a Automático conservan su solicitud durante una caída
temporal del controlador de ventiladores; una orden explícita del usuario sigue
teniendo prioridad. El comportamiento anterior permanece como opción.

- Recuperación CPU/GPU por dominio, preservando el supervisor y la sesión sana.
- Reintentos CPU/GPU/cTGP separados a 10, 30 y 60 segundos, con tres intentos por
  episodio y reposición tras 60 segundos de funcionamiento saludable observado.
- Un apagado individual cancela los reintentos, incluidos los que estén en cola.
- Las actualizaciones parciales conservan los valores realmente confirmados.
- El cierre compartido WMI acepta su recibo final exacto sin repetir órdenes de
  restauración; consultar ese recibo otra vez no bloquea una nueva sesión.
- Diagnósticos cTGP con tipo de excepción, HRESULT y fase, aunque el mensaje esté vacío.

Ventiladores y cTGP comparten el recurso WMI: ambos pueden necesitar pausa si
ese recurso falla. Las operaciones nativas inciertas y políticas modificadas
por otro controlador siguen requiriendo revisión. La base que sigue ventiladores
necesita datos internos realmente vigentes; una salida fija o curva propia
puede continuar con sus entradas válidas.

Validación local: once grupos de pruebas sin control del escritorio ni acceso
físico, 74 resultados de ciclo de vida, 1816 mutaciones de perfiles y 23040
combinaciones de admisión. La publicación local no implica que CI haya aprobado
este commit. Sigue pendiente observar suspensión/reanudación y fallos reales con
esta versión. Se conservan las protecciones térmicas y los límites existentes.
