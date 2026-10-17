# Observación de marcadores WMI sin bloquear su finalización

Una lectura HP WMI publica `native-inflight.json` antes de entrar en la llamada
nativa y lo elimina sólo después de un retorno confirmado. Si la llamada lanza
una excepción, el marcador se conserva y la serialización rechaza otro despacho.

El inventario de recuperación abría ese marcador con `FileShare.Read`. En Windows,
una lectura breve del inventario podía coincidir con la eliminación del marcador
tras el retorno nativo. La eliminación fallaba por uso compartido y el marcador
quedaba presente. Los intentos posteriores se rechazaban por finalización incierta,
dejando las RPM no disponibles y bloqueando Automático. La exportación de
diagnósticos también podía abrir ese archivo sin compartir eliminación.

## Corrección

- Sólo la observación de `native-inflight.json` comparte lectura y eliminación.
  No comparte escritura sobre el mismo archivo: el publicador utiliza un archivo
  temporal y reemplazo atómico. Un lector abierto conserva su publicación anterior.
- Inventario y exportación utilizan el mismo lector acotado y comprobación de
  rutas. El inventario interpreta y calcula el hash de los mismos bytes, sin abrir
  dos veces un marcador transitorio. Si la finalización normal lo retira durante
  la enumeración, no se trata esa desaparición como una recuperación pendiente.
- Los leases duraderos, bloqueos del recuperador, serialización nativa, identidad
  del propietario y comprobaciones de hash conservan su comportamiento. No se
  borra un marcador retenido ni se atribuye un retorno nativo por antigüedad.
- Inicio conserva las últimas RPM medidas, pero las muestra en amarillo como
  **Última RPM** y aclara que la animación usa datos anteriores. El estado retenido
  forma parte de la clave de las capas gráficas, incluso si el número no cambia.
  La caché de presentación no habilita control ni renueva las fechas de adquisición.

La corrección evita una causa reproducible de bloqueo persistente durante la
recuperación. No demuestra la causa de un watchdog anterior ni resuelve por sí
sola un marcador ya incierto en una instalación abierta.

## Pruebas Windows sin hardware

`ProductRecoveryInventorySelfTest` reproduce de forma determinista la infracción
de uso compartido del lector anterior, mantiene abierto el lector corregido
mientras se elimina el marcador y comprueba el reemplazo atómico y la integridad
de ambas publicaciones. También verifica que un lease duradero sigue bloqueando
su eliminación mientras se lee. Se ejecuta dentro de `--product-gui-self-test`.

Los fixtures gráficos verifican las etiquetas de RPM vigentes/retenidas y generan
`home-dashboard-held-rpm.png`. Las regresiones existentes comprueban que una
excepción nativa conserva el marcador y que un slot ocupado, abandonado o incierto
no permite recuperación ni nuevos despachos. Las pruebas se ejecutan en GitHub CI;
no necesitan acceso a HP WMI, EC, PawnIO o ventiladores físicos.
